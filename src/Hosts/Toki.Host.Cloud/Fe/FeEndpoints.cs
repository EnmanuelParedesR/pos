using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Fe;

/// <summary>The consent the owner and the certificate holder accept at the store (texto en docs/ecf-propio.md §10.3). A new text gets a new version.</summary>
public static class FeConsent
{
    public const string Version = "2026-10";
}

/// <param name="File">The .p12/.pfx in Base64. Used once to import it non-exportable; neither it nor the password is stored.</param>
public sealed record FeConsentRequest(string? File, string? Password, string? Rnc, string? OwnerName, string? HolderName, bool Accepted, string? ConsentVersion);

public sealed record FeReceiverUrls(string Environment, string Reception, string Approval, string Authentication);

public sealed record FeReceiverStatus(bool Active, string? Rnc = null, string? Subject = null, DateTime? NotAfterUtc = null, DateTime? ConsentedUtc = null,
    string? OwnerName = null, string? HolderName = null, IReadOnlyList<FeReceiverUrls>? Urls = null);

public sealed record FeReceivedItem(long Id, string Environment, string IssuerRnc, string IssuerName, string ENcf, int Type, DateOnly IssuedOn, decimal Total,
    decimal Itbis, DateTime ReceivedUtc, string XmlGzip, string? Approval, string? ApprovalReason, DateTime? ApprovalUtc, DateTime? ApprovalDeliveredUtc,
    DateTime? ApprovalDgiiUtc, string? ApprovalMessage);

/// <summary>A received e-CF as the panel lists it (no XML).</summary>
public sealed record FePanelReceived(long Id, string Environment, string IssuerRnc, string IssuerName, string ENcf, int Type, DateOnly IssuedOn, decimal Total,
    decimal Itbis, DateTime ReceivedUtc, string? Approval, string? ApprovalReason, DateTime? ApprovalUtc, DateTime? ApprovalDeliveredUtc, DateTime? ApprovalDgiiUtc,
    string? ApprovalMessage);

public sealed record FePullRequest(long? After);

public sealed record FeApprovalRequest(bool? Approve, string? Reason);

internal static class FeErrors
{
    public static readonly Error ConsentRequired = Error.Validation("Fe.ConsentRequired",
        "Para guardar la copia del certificado en la nube hace falta aceptar el consentimiento, con el nombre del dueño y del titular del certificado.");
    public static readonly Error InvalidRnc = Error.Validation("Fe.InvalidRnc", "Configure el RNC del negocio (9 u 11 dígitos) antes de activar el receptor.");
    public static readonly Error CertificateUnreadable = Error.Validation("Fe.CertificateUnreadable",
        "No se pudo abrir el certificado. Revise que sea el archivo .p12 o .pfx y que la contraseña sea la correcta.");
    public static readonly Error CertificateWithoutKey = Error.Validation("Fe.CertificateWithoutKey", "Ese archivo no trae la llave privada RSA del certificado.");
    public static readonly Error CertificateExpired = Error.Validation("Fe.CertificateExpired", "Ese certificado ya venció.");
    public static readonly Error RncTaken = Error.Conflict("Fe.RncTaken",
        "Ese RNC ya tiene los servicios de receptor activos en otra cuenta de Toki. Escriba a soporte de Toki.");
    public static readonly Error NoConsent = Error.Conflict("Fe.NoConsent", "El receptor de comprobantes no está activo: falta el consentimiento del certificado.");
    public static readonly Error NotFound = Error.NotFound("Fe.NotFound", "Ese comprobante recibido no existe.");
    public static readonly Error ApprovalReasonRequired = Error.Validation("Fe.ApprovalReasonRequired", "Escriba el motivo del rechazo (hasta 250 caracteres).");
    public static readonly Error ApprovalAlreadySigned = Error.Conflict("Fe.ApprovalAlreadySigned",
        "Ese comprobante ya tiene una aprobación comercial firmada con otra decisión: no se puede cambiar.");
}

internal static class FeServices
{
    /// <summary><c>Fe:Vault</c> = <c>KeyVault</c> (with <c>Fe:KeyVaultUri</c>; required in Production) or <c>Local</c> (in memory, never in Production).
    /// <c>Fe:PublicUrl</c>: the base of the receiver URLs shown to the owner (default: the request's host). <c>Fe:RequireToken</c> (true).</summary>
    public static IServiceCollection AddFe(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        services.AddDataProtection();
        services.AddSingleton<FeTokens>();
        services.AddSingleton<FeDgiiTokenCache>();
        services.AddScoped<FeSigner>();
        services.AddScoped<FeOutbound>();
        services.AddHttpClient(FeOutbound.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
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

    public static void MapFe(this IEndpointRouteBuilder app)
    {
        app.MapFeReceiver();
        app.MapFeStore();
        app.MapFePanel();
    }

    /// <summary>Called by the store's main PC (node identity): consent and certificate (TK-151), received e-CF and the owner's approval (TK-154).</summary>
    private static void MapFeStore(this IEndpointRouteBuilder app)
    {
        var store = app.MapGroup("/fiscal/v1").RequireAuthorization(CloudAuth.NodePolicy).WithTags("Fe");

        store.MapPost("/receiver/certificate", async (FeConsentRequest r, ClaimsPrincipal user, HttpRequest http, CloudDbContext db, IFeCertificateVault vault,
            TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
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
            }
            catch (CryptographicException) { return EndpointExtensions.Problem(FeErrors.CertificateUnreadable); }
            if (await db.FeCertificates.AnyAsync(c => c.Rnc == rnc && c.RevokedUtc == null && c.TenantId != node.TenantId, ct))
                return EndpointExtensions.Problem(FeErrors.RncTaken);

            var id = Guid.CreateVersion7();
            var name = $"fe-{node.TenantId:N}-{id:N}";
            using var imported = await vault.ImportAsync(name, pfx, r.Password, ct);
            await RevokeAsync(db, vault, node.TenantId, "Reemplazado por un certificado nuevo", now, ct);
            db.FeCertificates.Add(new FeCertificate
            {
                Id = id, TenantId = node.TenantId, Rnc = rnc, Subject = Cut(imported.Subject, 500), Thumbprint = imported.Thumbprint,
                NotAfterUtc = imported.NotAfter.ToUniversalTime(), KeyName = name, OwnerName = owner, HolderName = holder, ConsentVersion = FeConsent.Version,
                ConsentedByNodeId = node.NodeId, ConsentedUtc = now,
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(await StatusAsync(db, node.TenantId, BaseUrl(http, config), ct));
        });

        store.MapPost("/receiver/status", async (ClaimsPrincipal user, HttpRequest http, CloudDbContext db, IConfiguration config, CancellationToken ct) =>
            Results.Ok(await StatusAsync(db, NodeIdentity.From(user).TenantId, BaseUrl(http, config), ct)));

        store.MapPost("/receiver/revoke", async (ClaimsPrincipal user, CloudDbContext db, IFeCertificateVault vault, TimeProvider clock, CancellationToken ct) =>
        {
            await RevokeAsync(db, vault, NodeIdentity.From(user).TenantId, "Retirado desde la tienda", clock.GetUtcNow().UtcDateTime, ct);
            return Results.Ok(new FeReceiverStatus(false));
        });

        // The store asks every 30 s for what arrived after the last one it has (the cursor is the cloud id).
        store.MapPost("/received/pull", async (FePullRequest r, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenant = NodeIdentity.From(user).TenantId;
            var after = r.After ?? 0;
            var items = await db.FeReceived.AsNoTracking().Where(d => d.TenantId == tenant && d.Id > after).OrderBy(d => d.Id).Take(100).ToListAsync(ct);
            return Results.Ok(new { items = items.Select(Item) });
        });

        store.MapPost("/received/{id:long}/approval", (long id, FeApprovalRequest r, ClaimsPrincipal user, CloudDbContext db, FeSigner signer, FeOutbound outbound,
            TimeProvider clock, ILoggerFactory logs, CancellationToken ct) => ApproveAsync(NodeIdentity.From(user).TenantId, id, r, db, signer, outbound, clock, logs, ct));
    }

    /// <summary>TK-154: the owner's commercial approval (or rejection) of a received e-CF, from the store or the panel. Signed once in the cloud (ACECF,
    /// XSD-checked), then sent to the supplier and to the DGII; calling again retries only what has not gone yet. The decision cannot change once signed.</summary>
    private static async Task<IResult> ApproveAsync(Guid tenant, long id, FeApprovalRequest r, CloudDbContext db, FeSigner signer, FeOutbound outbound,
        TimeProvider clock, ILoggerFactory logs, CancellationToken ct)
    {
        var doc = await db.FeReceived.SingleOrDefaultAsync(d => d.Id == id && d.TenantId == tenant, ct);
        if (doc is null) return EndpointExtensions.Problem(FeErrors.NotFound);
        var certificate = await db.FeCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant && c.RevokedUtc == null, ct);
        if (certificate is null) return EndpointExtensions.Problem(FeErrors.NoConsent);
        if (r.Approve is not { } approve) return EndpointExtensions.Problem(Error.Validation("Fe.ApprovalRequired", "Elija aprobar o rechazar."));
        var reason = r.Reason?.Trim();
        if (!approve && (string.IsNullOrEmpty(reason) || reason.Length > 250)) return EndpointExtensions.Problem(FeErrors.ApprovalReasonRequired);
        var decision = approve ? "Approved" : "Rejected";
        var now = clock.GetUtcNow().UtcDateTime;

        if (doc.ApprovalXmlGzip is null)
        {
            var acecf = new XDocument(new XElement("ACECF", new XElement("DetalleAprobacionComercial",
                new XElement("Version", "1.0"), new XElement("RNCEmisor", doc.IssuerRnc), new XElement("eNCF", doc.ENcf),
                new XElement("FechaEmision", doc.IssuedOn.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
                new XElement("MontoTotal", doc.Total.ToString("0.00", CultureInfo.InvariantCulture)), new XElement("RNCComprador", doc.BuyerRnc),
                new XElement("Estado", approve ? 1 : 2), approve ? null : new XElement("DetalleMotivoRechazo", reason),
                new XElement("FechaHoraAprobacionComercial", FeXml.DateTimeText(now)))));
            if (FeXml.Validate(acecf.ToString(SaveOptions.DisableFormatting), FeXml.Acecf) is [var error, ..])
                return EndpointExtensions.Problem(Error.Validation("Fe.InvalidApproval", $"La aprobación no cumple el formato de la DGII: {error}"));
            var signed = await signer.SignAsync(certificate, acecf, doc.ENcf, keepXml: false, ct);
            if (signed is null) return EndpointExtensions.Problem(FeErrors.NoConsent);
            (doc.Approval, doc.ApprovalReason, doc.ApprovalXmlGzip, doc.ApprovalUtc) = (decision, approve ? null : reason, FeXml.Zip(signed), now);
            await db.SaveChangesAsync(ct);
        }
        else if (doc.Approval != decision) return EndpointExtensions.Problem(FeErrors.ApprovalAlreadySigned);

        var xml = FeXml.Unzip(doc.ApprovalXmlGzip!);
        var file = $"{doc.BuyerRnc}{doc.ENcf}.xml";
        var messages = new List<string>();
        var log = logs.CreateLogger("Toki.Fe");
        if (doc.ApprovalDeliveredUtc is null)
        {
            try
            {
                if (await outbound.DirectoryAsync(certificate, doc.Environment, doc.IssuerRnc, ct) is { Approval: { } url, Authentication: var auth })
                {
                    await outbound.PostToTaxpayerAsync(certificate, url, auth, "aprobacioncomercial/api/ecf", file, xml, ct);
                    doc.ApprovalDeliveredUtc = clock.GetUtcNow().UtcDateTime;
                }
                else messages.Add("El suplidor no aparece en el directorio de la DGII: la aprobación va solo a la DGII.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Could not deliver the commercial approval of {ENcf} to its issuer", doc.ENcf);
                messages.Add($"No se pudo entregar al suplidor: {e.Message}");
            }
        }
        if (doc.ApprovalDgiiUtc is null)
        {
            try
            {
                if (await outbound.SendApprovalToDgiiAsync(certificate, doc.Environment, file, xml, ct) is { } refused) messages.Add(refused);
                else doc.ApprovalDgiiUtc = clock.GetUtcNow().UtcDateTime;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Could not send the commercial approval of {ENcf} to the DGII", doc.ENcf);
                messages.Add($"No se pudo enviar a la DGII: {e.Message}");
            }
        }
        doc.ApprovalMessage = messages.Count == 0 ? null : Cut(string.Join(" ", messages), 1000);
        await db.SaveChangesAsync(ct);
        return Results.Ok(Item(doc));
    }

    /// <summary>The owner sees the receiver, the cloud signatures (TK-151) and the approvals buyers sent; any owner can withdraw the consent.</summary>
    private static void MapFePanel(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/panel/fe", async (ClaimsPrincipal user, HttpRequest http, CloudDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var tenant = user.GetTenantId();
            var signatures = await db.FeSignatures.AsNoTracking().Where(s => s.TenantId == tenant).OrderByDescending(s => s.Id).Take(100)
                .Select(s => new { s.AtUtc, s.Root, s.ENcf, s.Sha256 }).ToListAsync(ct);
            var approvals = await db.FeApprovalsReceived.AsNoTracking().Where(a => a.TenantId == tenant).OrderByDescending(a => a.Id).Take(100)
                .Select(a => new { a.ENcf, a.BuyerRnc, a.Approved, a.Reason, a.Environment, a.ReceivedUtc }).ToListAsync(ct);
            return Results.Ok(new { status = await StatusAsync(db, tenant, BaseUrl(http, config), ct), signatures, approvals });
        }).RequireAuthorization(CloudAuth.PanelPolicy).WithTags("Fe");

        app.MapPost("/api/panel/fe/revoke", async (ClaimsPrincipal user, CloudDbContext db, IFeCertificateVault vault, TimeProvider clock, CancellationToken ct) =>
        {
            await RevokeAsync(db, vault, user.GetTenantId(), $"Retirado desde el panel por {user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name}",
                clock.GetUtcNow().UtcDateTime, ct);
            return Results.NoContent();
        }).RequireAuthorization(CloudAuth.OwnerPolicy).WithTags("Fe");

        // TK-154: the e-CF suppliers sent, newest first; every member sees them, only an Owner approves or rejects.
        app.MapGet("/api/panel/fe/received", async (ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenant = user.GetTenantId();
            return Results.Ok(await db.FeReceived.AsNoTracking().Where(d => d.TenantId == tenant).OrderByDescending(d => d.Id).Take(200)
                .Select(d => new FePanelReceived(d.Id, d.Environment, d.IssuerRnc, d.IssuerName, d.ENcf, d.Type, d.IssuedOn, d.Total, d.Itbis, d.ReceivedUtc,
                    d.Approval, d.ApprovalReason, d.ApprovalUtc, d.ApprovalDeliveredUtc, d.ApprovalDgiiUtc, d.ApprovalMessage)).ToListAsync(ct));
        }).RequireAuthorization(CloudAuth.PanelPolicy).WithTags("Fe");

        app.MapGet("/api/panel/fe/received/{id:long}/xml", async (long id, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenant = user.GetTenantId();
            var xml = await db.FeReceived.AsNoTracking().Where(d => d.Id == id && d.TenantId == tenant).Select(d => d.XmlGzip).SingleOrDefaultAsync(ct);
            return xml is null ? EndpointExtensions.Problem(FeErrors.NotFound) : Results.Ok(new { xml = FeXml.Unzip(xml) });
        }).RequireAuthorization(CloudAuth.PanelPolicy).WithTags("Fe");

        // shortcut: the store learns of a decision taken here only when it tries one itself (the same one is accepted again, another is refused).
        app.MapPost("/api/panel/fe/received/{id:long}/approval", (long id, FeApprovalRequest r, ClaimsPrincipal user, CloudDbContext db, FeSigner signer,
            FeOutbound outbound, TimeProvider clock, ILoggerFactory logs, CancellationToken ct) => ApproveAsync(user.GetTenantId(), id, r, db, signer, outbound, clock, logs, ct))
            .RequireAuthorization(CloudAuth.PanelPolicy).RequireAuthorization(CloudAuth.OwnerPolicy).WithTags("Fe");
    }

    /// <summary>Withdraws the tenant's consent: marked first (the signer and the receiver stop at once), then the key is deleted from the vault.</summary>
    private static async Task RevokeAsync(CloudDbContext db, IFeCertificateVault vault, Guid tenant, string by, DateTime now, CancellationToken ct)
    {
        var active = await db.FeCertificates.Where(c => c.TenantId == tenant && c.RevokedUtc == null).ToListAsync(ct);
        foreach (var c in active) (c.RevokedUtc, c.RevokedBy) = (now, Cut(by, 256));
        await db.SaveChangesAsync(ct);
        foreach (var c in active) await vault.DeleteAsync(c.KeyName, ct);
    }

    private static async Task<FeReceiverStatus> StatusAsync(CloudDbContext db, Guid tenant, string baseUrl, CancellationToken ct)
    {
        var c = await db.FeCertificates.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant && x.RevokedUtc == null, ct);
        if (c is null) return new FeReceiverStatus(false);
        var urls = FeReceiverEndpoints.Environments.Select(env => $"{baseUrl}/{env}/{c.Rnc}/fe")
            .Select((u, i) => new FeReceiverUrls(FeReceiverEndpoints.Environments[i], $"{u}/recepcion/api/ecf", $"{u}/aprobacioncomercial/api/ecf", $"{u}/autenticacion/api/semilla"))
            .ToList();
        return new FeReceiverStatus(true, c.Rnc, c.Subject, c.NotAfterUtc, c.ConsentedUtc, c.OwnerName, c.HolderName, urls);
    }

    private static FeReceivedItem Item(FeReceivedDocument d) => new(d.Id, d.Environment, d.IssuerRnc, d.IssuerName, d.ENcf, d.Type, d.IssuedOn, d.Total, d.Itbis,
        d.ReceivedUtc, Convert.ToBase64String(d.XmlGzip), d.Approval, d.ApprovalReason, d.ApprovalUtc, d.ApprovalDeliveredUtc, d.ApprovalDgiiUtc, d.ApprovalMessage);

    private static string BaseUrl(HttpRequest http, IConfiguration config) => (config["Fe:PublicUrl"] ?? $"{http.Scheme}://{http.Host}").TrimEnd('/');

    private static byte[]? Base64(string s)
    {
        var buffer = new byte[s.Length * 3 / 4 + 3];
        return Convert.TryFromBase64String(s, buffer, out var written) ? buffer[..written] : null;
    }

    private static string Cut(string s, int max) => s.Length > max ? s[..max] : s;
}
