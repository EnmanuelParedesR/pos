using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Fe;

/// <summary>The consent the owner and the certificate holder accept at the store (texto en docs/ecf-propio.md §10.3). A new text gets a new version.</summary>
public static class FeConsent
{
    /// <summary>TK-151: CloudCustody, a copy of the certificate in Key Vault.</summary>
    public const string Version = "2026-10";
    /// <summary>TK-246: StoreSigned, no copy of the certificate in the cloud.</summary>
    public const string StoreSignedVersion = "2026-10-tienda";
}

/// <param name="File">The .p12/.pfx in Base64. Used once to import it non-exportable; neither it nor the password is stored.</param>
public sealed record FeConsentRequest(string? File, string? Password, string? Rnc, string? OwnerName, string? HolderName, bool Accepted, string? ConsentVersion);

public sealed record FeReceiverUrls(string Environment, string Reception, string Approval, string Authentication);

/// <param name="SigningMode">StoreSigned or CloudCustody (TK-246) while active.</param>
/// <param name="DefaultSigningMode">What the activation screen offers first (<c>Fe:DefaultSigningMode</c>).</param>
/// <param name="CloudCustodyAvailable">False while <c>Fe:AllowCloudCustody</c> is off: the screens show CloudCustody as "Próximamente".</param>
public sealed record FeReceiverStatus(bool Active, string? Rnc = null, string? Subject = null, DateTime? NotAfterUtc = null, DateTime? ConsentedUtc = null,
    string? OwnerName = null, string? HolderName = null, IReadOnlyList<FeReceiverUrls>? Urls = null, string? SigningMode = null,
    string? DefaultSigningMode = null, bool CloudCustodyAvailable = false);

/// <param name="XmlGzip">The document exactly as the supplier sent it (gzip, base64): its encoding is the one it declares.</param>
/// <param name="Conflict">R9 #1: the same e-NCF arrived with other content too; every version comes, flagged.</param>
/// <param name="AcknowledgedUtc">TK-246: when its signed ARECF was made; null while the store has not signed it (it was off when it arrived).</param>
public sealed record FeReceivedItem(long Id, string Environment, string IssuerRnc, string IssuerName, string ENcf, int Type, DateOnly IssuedOn, decimal Total,
    decimal Itbis, DateTime ReceivedUtc, string XmlGzip, string? Approval, string? ApprovalReason, DateTime? ApprovalUtc, DateTime? ApprovalDeliveredUtc,
    DateTime? ApprovalDgiiUtc, string? ApprovalMessage, bool Conflict, DateTime? AcknowledgedUtc);

/// <summary>A received e-CF as the panel lists it (no XML).</summary>
/// <param name="PanelDecision">TK-250: the owner's decision from the panel; pending at the store while <paramref name="Approval"/> is null.</param>
/// <param name="SignerUnverified">R11 B-High2: "firmante sin verificar"; the store never gets it.</param>
public sealed record FePanelReceived(long Id, string Environment, string IssuerRnc, string IssuerName, string ENcf, int Type, DateOnly IssuedOn, decimal Total,
    decimal Itbis, DateTime ReceivedUtc, string? Approval, string? ApprovalReason, DateTime? ApprovalUtc, DateTime? ApprovalDeliveredUtc, DateTime? ApprovalDgiiUtc,
    string? ApprovalMessage, bool Conflict, DateTime? AcknowledgedUtc, string? PanelDecision = null, string? PanelReason = null, string? PanelDecidedBy = null,
    DateTime? PanelDecidedUtc = null, bool SignerUnverified = false);

public sealed record FePullRequest(long? After);

/// <summary>TK-250: a commercial approval an owner decided in the panel, for the store to sign and send. <paramref name="Id"/> is the cloud id.</summary>
public sealed record FePendingDecision(long Id, string Decision, string? Reason, string DecidedBy, DateTime DecidedUtc);

public sealed record FeDecisionRequest(bool? Approve, string? Reason);

internal static class FeErrors
{
    public static readonly Error ConsentRequired = Error.Validation("Fe.ConsentRequired",
        "Para activar el receptor en la nube hace falta aceptar el consentimiento, con el nombre del dueño y del titular del certificado.");
    public static readonly Error InvalidRnc = Error.Validation("Fe.InvalidRnc", "Configure el RNC del negocio (9 u 11 dígitos) antes de activar el receptor.");
    public static readonly Error CertificateUnreadable = Error.Validation("Fe.CertificateUnreadable",
        "No se pudo abrir el certificado. Revise que sea el archivo .p12 o .pfx y que la contraseña sea la correcta.");
    public static readonly Error CertificateWithoutKey = Error.Validation("Fe.CertificateWithoutKey", "Ese archivo no trae la llave privada RSA del certificado.");
    public static readonly Error CertificateExpired = Error.Validation("Fe.CertificateExpired", "Ese certificado ya venció.");
    public static readonly Error CertificateUntrusted = Error.Validation("Fe.CertificateUntrusted",
        "Ese certificado no lo emitió una entidad de certificación autorizada por INDOTEL (o está revocado). Use el certificado de firma digital del negocio.");
    public static readonly Error CertificateRncMismatch = Error.Validation("Fe.CertificateRncMismatch",
        "Ese certificado no es de este RNC: debe traer el RNC del negocio o la cédula de la persona delegada como firmante.");
    public static readonly Error RncTaken = Error.Conflict("Fe.RncTaken",
        "Ese RNC ya tiene los servicios de receptor activos en otra cuenta de Abastalia. Escriba a soporte de Abastalia.");
    public static readonly Error NotFound = Error.NotFound("Fe.NotFound", "Ese comprobante recibido no existe.");
    public static readonly Error ProofInvalid = Error.Validation("Fe.ProofInvalid",
        "La prueba firmada del RNC no es válida o venció. Intente activar el receptor otra vez.");
    public static readonly Error CloudCustodyUnavailable = Error.Conflict("Fe.CloudCustodyUnavailable",
        "La copia del certificado en la nube todavía no está disponible. Active el receptor con la firma en la tienda.");
    public static readonly Error DecisionRequired = Error.Validation("Fe.DecisionRequired", "Elija aceptar o rechazar.");
    public static readonly Error ReasonRequired = Error.Validation("Fe.ReasonRequired", "Escriba el motivo del rechazo (hasta 250 caracteres).");
    public static readonly Error ReceiverInactive = Error.Conflict("Fe.ReceiverInactive",
        "El receptor no está activo: la aprobación se firma en la PC de la tienda que lo activó. Actívelo desde la tienda.");
    public static readonly Error AlreadyDecided = Error.Conflict("Fe.AlreadyDecided",
        "Ese comprobante ya tiene otra decisión (en la tienda o en el panel) y no se puede cambiar.");
    public static readonly Error OtherNodeActive = Error.Conflict("Fe.OtherNodeActive",
        "El receptor está activo desde otra PC del negocio. Retírelo desde esa PC o, como dueño, desde el panel, y actívelo aquí.");
    public static readonly Error SignerUnverified = Error.Conflict("Fe.SignerUnverified",
        "Ese comprobante lo firmó una persona (cédula) por la empresa y no se puede verificar que firme por ella. Confírmelo con el suplidor antes de usarlo.");
}

internal static class FeServices
{
    /// <summary><c>Fe:Vault</c> = <c>KeyVault</c> (with <c>Fe:KeyVaultUri</c>; required in Production) or <c>Local</c> (in memory, never in Production).
    /// <c>Fe:PublicUrl</c>: the base of the receiver URLs shown to the owner (default: the request's host). <c>Fe:RequireToken</c> (true).
    /// <c>Fe:DefaultSigningMode</c> (StoreSigned) and <c>Fe:AllowCloudCustody</c> (false): TK-246, see ADR-0011.</summary>
    public static IServiceCollection AddFe(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        FeSigningModes.Default(config); // a wrong value stops the start
        services.AddDataProtection();
        services.AddSingleton<FeTrust>();
        services.Configure<RateLimiterOptions>(o => o.GlobalLimiter = FeReceiverEndpoints.RateLimiter(config));
        services.AddSingleton<FeTokens>();
        services.AddSingleton<FeSignBroker>();
        services.AddScoped<FeSigner>();
        services.AddScoped<FeLateAcknowledger>();
        if (config.GetValue("Fe:LateAcksInBackground", true)) services.AddHostedService<FeLateAcknowledgerWorker>();
        switch (config["Fe:Vault"] ?? "Local")
        {
            case "KeyVault":
                var uri = new Uri(config["Fe:KeyVaultUri"] ?? throw new InvalidOperationException("Fe:Vault KeyVault needs Fe:KeyVaultUri."));
                var credential = new DefaultAzureCredential();
                services.AddSingleton<IFeCertificateVault>(new KeyVaultFeVault(new CertificateClient(uri, credential), credential));
                break;
            case "Local" when !env.IsProduction():
                services.AddSingleton<IFeCertificateVault, LocalFeVault>();
                break;
            case var other:
                throw new InvalidOperationException($"Fe:Vault '{other}' is not allowed here: use KeyVault (Local only outside Production).");
        }
        return services;
    }

    /// <summary>TK-246 (council): turning CloudCustody on waits for the DGII's written answer (§11 question 1) and a separate signing service.</summary>
    public static bool CloudCustodyAllowed(IConfiguration config) => config.GetValue("Fe:AllowCloudCustody", false);

    public static void MapFe(this IEndpointRouteBuilder app)
    {
        app.MapFeReceiver();
        app.MapFeStore();
        app.MapFePanel();
    }

    /// <summary>Called by the store's main PC (node identity): activation (TK-151, TK-246), the sign requests and the received e-CF (TK-154).
    /// Commercial approvals are signed and sent by the store itself (TK-246), in both modes.</summary>
    private static void MapFeStore(this IEndpointRouteBuilder app)
    {
        var store = app.MapGroup("/fiscal/v1").RequireAuthorization(CloudAuth.NodePolicy).WithTags("Fe");

        // CloudCustody. R9 #6: the receiver URLs are published for the RNC, so the certificate must be of an INDOTEL authority and belong to that RNC (or
        // be a cédula on a company's RNC that Abastalia support approved, R11 B-High2); the database keeps one active receiver per RNC. A disputed RNC
        // stays with support.
        store.MapPost("/receiver/certificate", async (FeConsentRequest r, ClaimsPrincipal user, HttpRequest http, CloudDbContext db, IFeCertificateVault vault,
            FeTrust trust, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            if (!CloudCustodyAllowed(config)) return EndpointExtensions.Problem(FeErrors.CloudCustodyUnavailable);
            var node = NodeIdentity.From(user);
            var (owner, holder, rnc) = (r.OwnerName?.Trim() ?? "", r.HolderName?.Trim() ?? "", r.Rnc?.Trim() ?? "");
            if (!r.Accepted || r.ConsentVersion != FeConsent.Version || owner.Length is 0 or > 150 || holder.Length is 0 or > 150)
                return EndpointExtensions.Problem(FeErrors.ConsentRequired);
            if (!Rnc.IsValid(rnc)) return EndpointExtensions.Problem(FeErrors.InvalidRnc);
            var now = clock.GetUtcNow().UtcDateTime;
            var pfx = r.File is { Length: > 0 and < 300_000 } ? Base64(r.File) : null;
            if (pfx is null) return EndpointExtensions.Problem(FeErrors.CertificateUnreadable);
            try
            {
                using var opened = X509CertificateLoader.LoadPkcs12(pfx, r.Password, X509KeyStorageFlags.EphemeralKeySet);
                using var key = opened.GetRSAPrivateKey();
                if (key is null) return EndpointExtensions.Problem(FeErrors.CertificateWithoutKey);
                if (opened.NotAfter.ToUniversalTime() <= now) return EndpointExtensions.Problem(FeErrors.CertificateExpired);
                if (!trust.IsTrusted(opened)) return EndpointExtensions.Problem(FeErrors.CertificateUntrusted);
                if (await FeDelegations.CheckAsync(db, node, rnc, opened, holder, now, ct) is { } refused) return refused; // R11 B-High2
            }
            catch (CryptographicException) { return EndpointExtensions.Problem(FeErrors.CertificateUnreadable); }
            if (await db.FeCertificates.AnyAsync(c => c.Rnc == rnc && c.RevokedUtc == null && c.TenantId != node.TenantId, ct))
                return EndpointExtensions.Problem(FeErrors.RncTaken);

            var id = Guid.CreateVersion7();
            var name = $"fe-{node.TenantId:N}-{id:N}";
            using var imported = await vault.ImportAsync(name, pfx, r.Password, ct);
            return await ActivateAsync(db, vault, new FeCertificate
            {
                Id = id, TenantId = node.TenantId, Rnc = rnc, Subject = FeXml.Cut(imported.Subject, 500), Thumbprint = imported.Thumbprint,
                NotAfterUtc = imported.NotAfter.ToUniversalTime(), SigningMode = FeSigningModes.CloudCustody, KeyName = name, OwnerName = owner,
                HolderName = holder, ConsentVersion = FeConsent.Version, ConsentedByNodeId = node.NodeId, ConsentedUtc = now,
            }, http, config, ct);
        });

        store.MapFeRemoteSigning(); // TK-246: StoreSigned activation and the node's sign requests

        store.MapPost("/receiver/status", async (ClaimsPrincipal user, HttpRequest http, CloudDbContext db, IConfiguration config, CancellationToken ct) =>
            Results.Ok(await StatusAsync(db, NodeIdentity.From(user).TenantId, BaseUrl(http, config), config, ct)));

        // R11 B-Med4: only the PC that activated it withdraws it from the store (an Owner can always do it in the panel).
        store.MapPost("/receiver/revoke", async (ClaimsPrincipal user, CloudDbContext db, IFeCertificateVault vault, TimeProvider clock, CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            if (await db.FeCertificates.AnyAsync(c => c.TenantId == node.TenantId && c.RevokedUtc == null && c.ConsentedByNodeId != node.NodeId, ct))
                return EndpointExtensions.Problem(FeErrors.OtherNodeActive);
            await RevokeAsync(db, vault, node.TenantId, "Retirado desde la tienda", clock.GetUtcNow().UtcDateTime, ct);
            return Results.Ok(new FeReceiverStatus(false));
        });

        // The store asks every 30 s for what arrived after the last one it has (the cursor is the cloud id), and (TK-250) for the approvals the owner
        // decided in the panel that this PC has to sign and send. One stays pending until the store's CommercialApprovalRecordedV1 sets Approval.
        // R11 B-Med4: only the PC that activated the receiver gets either (another store of the business gets nothing). R11 B-High2: never a document
        // whose signer is unverified: it is not a purchase until the DGII can tell (the panel shows it).
        store.MapPost("/received/pull", async (FePullRequest r, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            var after = r.After ?? 0;
            var signer = await db.FeCertificates.AnyAsync(c => c.TenantId == node.TenantId && c.RevokedUtc == null && c.ConsentedByNodeId == node.NodeId, ct);
            var items = signer
                ? await db.FeReceived.AsNoTracking().Where(d => d.TenantId == node.TenantId && d.Id > after && !d.SignerUnverified).OrderBy(d => d.Id).Take(100)
                    .ToListAsync(ct)
                : [];
            var decisions = signer
                ? await db.FeReceived.AsNoTracking().Where(d => d.TenantId == node.TenantId && d.PanelDecision != null && d.Approval == null)
                    .OrderBy(d => d.Id).Take(20)
                    .Select(d => new FePendingDecision(d.Id, d.PanelDecision!, d.PanelReason, d.PanelDecidedBy!, d.PanelDecidedUtc!.Value)).ToListAsync(ct)
                : [];
            return Results.Ok(new { items = items.Select(Item), decisions });
        });
    }

    /// <summary>Registers the tenant's receiver: the previous activation ends (its vault key, if any, is deleted) and this one starts in one transaction
    /// (the old one first, since only one active row per RNC may exist). A switch of mode is a new activation.</summary>
    /// <remarks>R11 B-Med4: while it is active from one PC, another PC of the business cannot take it over (an Owner withdraws it in the panel first).</remarks>
    internal static async Task<IResult> ActivateAsync(CloudDbContext db, IFeCertificateVault vault, FeCertificate row, HttpRequest http, IConfiguration config,
        CancellationToken ct)
    {
        var refusal = await db.FeCertificates.AnyAsync(c => c.Rnc == row.Rnc && c.RevokedUtc == null && c.TenantId != row.TenantId, ct) ? FeErrors.RncTaken
            : await db.FeCertificates.AnyAsync(c => c.TenantId == row.TenantId && c.RevokedUtc == null && c.ConsentedByNodeId != row.ConsentedByNodeId, ct)
                ? FeErrors.OtherNodeActive
                : null;
        if (refusal is not null)
        {
            if (row.KeyName.Length > 0) await vault.DeleteAsync(row.KeyName, ct);
            return EndpointExtensions.Problem(refusal);
        }
        var replaced = await db.Database.CreateExecutionStrategy().ExecuteAsync<List<string>?>(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var previous = await db.FeCertificates.Where(c => c.TenantId == row.TenantId && c.RevokedUtc == null).ToListAsync(ct);
            foreach (var c in previous) (c.RevokedUtc, c.RevokedBy) = (row.ConsentedUtc, "Reemplazado por una activación nueva");
            await db.SaveChangesAsync(ct);
            db.FeCertificates.Add(row);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return null; } // another tenant took the RNC a moment ago; the rollback keeps this tenant's old consent
            await tx.CommitAsync(ct);
            return previous.Select(c => c.KeyName).ToList();
        });
        if (replaced is null)
        {
            if (row.KeyName.Length > 0) await vault.DeleteAsync(row.KeyName, ct);
            return EndpointExtensions.Problem(FeErrors.RncTaken);
        }
        foreach (var old in replaced.Where(k => k.Length > 0)) await vault.DeleteAsync(old, ct); // switching to StoreSigned deletes the vault key
        return Results.Ok(await StatusAsync(db, row.TenantId, BaseUrl(http, config), config, ct));
    }

    /// <summary>The owner sees the receiver, the signatures made for it, the hours it was without its store (StoreSigned) and the approvals buyers
    /// sent; any owner can withdraw the consent. Approvals of received e-CF are signed and sent at the store (TK-246); an owner can decide one here
    /// (TK-250) and it waits for the store.</summary>
    private static void MapFePanel(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/panel/fe", async (ClaimsPrincipal user, HttpRequest http, CloudDbContext db, IConfiguration config, TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = user.GetTenantId();
            var signatures = await db.FeSignatures.AsNoTracking().Where(s => s.TenantId == tenant).OrderByDescending(s => s.Id).Take(100)
                .Select(s => new { s.AtUtc, s.Root, s.ENcf, s.Sha256 }).ToListAsync(ct);
            var approvals = await db.FeApprovalsReceived.AsNoTracking().Where(a => a.TenantId == tenant).OrderByDescending(a => a.Id).Take(100)
                .Select(a => new { a.ENcf, a.BuyerRnc, a.Approved, a.Reason, a.Environment, a.ReceivedUtc }).ToListAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var since = now.AddDays(-30);
            var outages = (await db.FeReceiverOutages.AsNoTracking().Where(o => o.TenantId == tenant && (o.ToUtc == null || o.ToUtc > since)).ToListAsync(ct))
                .GroupBy(o => o.NodeId).ToList();
            var nodeIds = outages.Select(g => g.Key).ToList();
            var names = await db.Nodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Name, ct);
            var offline = outages.Select(g => new
            {
                node = names.GetValueOrDefault(g.Key, "PC de la tienda"),
                hours = Math.Round(g.Sum(o => ((o.ToUtc ?? now) - (o.FromUtc < since ? since : o.FromUtc)).TotalHours), 1),
                offlineSinceUtc = g.Where(o => o.ToUtc == null).Select(o => (DateTime?)o.FromUtc).FirstOrDefault(),
            }).ToList();
            return Results.Ok(new { status = await StatusAsync(db, tenant, BaseUrl(http, config), config, ct), signatures, approvals, offline });
        }).RequireAuthorization(CloudAuth.PanelPolicy).WithTags("Fe");

        app.MapPost("/api/panel/fe/revoke", async (ClaimsPrincipal user, CloudDbContext db, IFeCertificateVault vault, TimeProvider clock, CancellationToken ct) =>
        {
            await RevokeAsync(db, vault, user.GetTenantId(), $"Retirado desde el panel por {user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name}",
                clock.GetUtcNow().UtcDateTime, ct);
            return Results.NoContent();
        }).RequireAuthorization(CloudAuth.OwnerPolicy).WithTags("Fe");

        // TK-154: the e-CF suppliers sent, newest first; every member sees them.
        app.MapGet("/api/panel/fe/received", async (ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenant = user.GetTenantId();
            return Results.Ok(await db.FeReceived.AsNoTracking().Where(d => d.TenantId == tenant).OrderByDescending(d => d.Id).Take(200)
                .Select(PanelItem).ToListAsync(ct));
        }).RequireAuthorization(CloudAuth.PanelPolicy).WithTags("Fe");

        // TK-250: an owner accepts or rejects a received e-CF from the panel. The cloud only keeps the decision: the store's main PC pulls it, signs the
        // ACECF with its certificate and sends it (ADR-0011). One conditional update: the first decision wins, the same one again is a no-op, and once
        // the store recorded its own (Approval set) nothing changes. No quota meter: one decision per received document, under the panel's rate limit.
        app.MapPost("/api/panel/fe/received/{id:long}/decision", async (long id, FeDecisionRequest r, ClaimsPrincipal user, CloudDbContext db, TimeProvider clock,
            ILoggerFactory logs, CancellationToken ct) =>
            (await DecideAsync(db, user, id, r.Approve, r.Reason, clock.GetUtcNow().UtcDateTime, logs, ct)).ToHttp())
            .RequireAuthorization(CloudAuth.OwnerPolicy).RequireRateLimiting(Quotas.QuotaRateLimits.PanelUser).WithTags("Fe");

        app.MapGet("/api/panel/fe/received/{id:long}/xml", async (long id, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenant = user.GetTenantId();
            var xml = await db.FeReceived.AsNoTracking().Where(d => d.Id == id && d.TenantId == tenant).Select(d => d.XmlGzip).SingleOrDefaultAsync(ct);
            return xml is null ? EndpointExtensions.Problem(FeErrors.NotFound) : Results.Ok(new { xml = FeXml.ReceivedText(xml) });
        }).RequireAuthorization(CloudAuth.PanelPolicy).WithTags("Fe");
    }

    /// <summary>The panel's decision on a received e-CF (TK-250), also Talia's received_decision (TK-341).</summary>
    internal static async Task<Result<FePanelReceived>> DecideAsync(CloudDbContext db, ClaimsPrincipal user, long id, bool? approved, string? rejectReason,
        DateTime now, ILoggerFactory logs, CancellationToken ct)
    {
        if (approved is not { } approve) return FeErrors.DecisionRequired;
        var reason = approve ? null : rejectReason?.Trim();
        if (!approve && (string.IsNullOrEmpty(reason) || reason.Length > 250)) return FeErrors.ReasonRequired;
        var decision = approve ? "Approved" : "Rejected";
        var tenant = user.GetTenantId();
        var by = FeXml.Cut(user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "", 256);
        var row = db.FeReceived.Where(d => d.Id == id && d.TenantId == tenant);
        if (await row.Select(d => (bool?)d.SignerUnverified).SingleOrDefaultAsync(ct) is not { } unverified) return FeErrors.NotFound;
        if (unverified) return FeErrors.SignerUnverified; // R11 B-High2: the store never got it
        if (!await db.FeCertificates.AnyAsync(c => c.TenantId == tenant && c.RevokedUtc == null, ct)) return FeErrors.ReceiverInactive;

        var taken = await row.Where(d => d.Approval == null && d.PanelDecision == null).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.PanelDecision, decision).SetProperty(d => d.PanelReason, reason).SetProperty(d => d.PanelDecidedBy, by)
            .SetProperty(d => d.PanelDecidedUtc, now), ct) == 1;
        var current = await row.AsNoTracking().Select(PanelItem).SingleAsync(ct);
        if (taken)
            logs.CreateLogger("Abastalia.Fe.Decisions").LogInformation("Commercial approval {Decision} of received {Id} decided in the panel by {By}", decision, id, by);
        else if ((current.Approval ?? current.PanelDecision) != decision) return FeErrors.AlreadyDecided;
        return current;
    }

    /// <summary>Withdraws the tenant's consent: marked first (the signer and the receiver stop at once), then a vault key, if any, is deleted.</summary>
    private static async Task RevokeAsync(CloudDbContext db, IFeCertificateVault vault, Guid tenant, string by, DateTime now, CancellationToken ct)
    {
        var active = await db.FeCertificates.Where(c => c.TenantId == tenant && c.RevokedUtc == null).ToListAsync(ct);
        foreach (var c in active) (c.RevokedUtc, c.RevokedBy) = (now, FeXml.Cut(by, 256));
        await db.SaveChangesAsync(ct);
        foreach (var c in active.Where(c => c.KeyName.Length > 0)) await vault.DeleteAsync(c.KeyName, ct);
    }

    private static async Task<FeReceiverStatus> StatusAsync(CloudDbContext db, Guid tenant, string baseUrl, IConfiguration config, CancellationToken ct)
    {
        var (mode, custody) = (FeSigningModes.Default(config), CloudCustodyAllowed(config));
        var c = await db.FeCertificates.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant && x.RevokedUtc == null, ct);
        if (c is null) return new FeReceiverStatus(false, DefaultSigningMode: mode, CloudCustodyAvailable: custody);
        var urls = FeReceiverEndpoints.Environments.Select(env => $"{baseUrl}/{env}/{c.Rnc}/fe")
            .Select((u, i) => new FeReceiverUrls(FeReceiverEndpoints.Environments[i], $"{u}/recepcion/api/ecf", $"{u}/aprobacioncomercial/api/ecf", $"{u}/autenticacion/api/semilla"))
            .ToList();
        return new FeReceiverStatus(true, c.Rnc, c.Subject, c.NotAfterUtc, c.ConsentedUtc, c.OwnerName, c.HolderName, urls, c.SigningMode, mode, custody);
    }

    private static readonly System.Linq.Expressions.Expression<Func<FeReceivedDocument, FePanelReceived>> PanelItem = d => new FePanelReceived(d.Id,
        d.Environment, d.IssuerRnc, d.IssuerName, d.ENcf, d.Type, d.IssuedOn, d.Total, d.Itbis, d.ReceivedUtc, d.Approval, d.ApprovalReason, d.ApprovalUtc,
        d.ApprovalDeliveredUtc, d.ApprovalDgiiUtc, d.ApprovalMessage, d.Conflict, d.AcknowledgedUtc, d.PanelDecision, d.PanelReason, d.PanelDecidedBy,
        d.PanelDecidedUtc, d.SignerUnverified);

    private static FeReceivedItem Item(FeReceivedDocument d) => new(d.Id, d.Environment, d.IssuerRnc, d.IssuerName, d.ENcf, d.Type, d.IssuedOn, d.Total, d.Itbis,
        d.ReceivedUtc, Convert.ToBase64String(d.XmlGzip), d.Approval, d.ApprovalReason, d.ApprovalUtc, d.ApprovalDeliveredUtc, d.ApprovalDgiiUtc, d.ApprovalMessage,
        d.Conflict, d.AcknowledgedUtc);

    private static string BaseUrl(HttpRequest http, IConfiguration config) => (config["Fe:PublicUrl"] ?? $"{http.Scheme}://{http.Host}").TrimEnd('/');

    private static byte[]? Base64(string s)
    {
        var buffer = new byte[s.Length * 3 / 4 + 3];
        return Convert.TryFromBase64String(s, buffer, out var written) ? buffer[..written] : null;
    }
}
