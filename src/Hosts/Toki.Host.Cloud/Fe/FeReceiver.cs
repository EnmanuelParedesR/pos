using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Fe;

/// <summary>TK-152: the services the DGII requires of every electronic issuer as a receiver ("Descripción Técnica Servicios Emisores Electrónicos",
/// docs/ecf-propio.md §2.6), published by the cloud for each tenant that gave its consent (TK-151):
/// <list type="bullet">
/// <item><c>GET /{amb}/{rnc}/fe/autenticacion/api/semilla</c> and <c>POST …/validacioncertificado</c>: the optional authentication, same scheme as the
/// DGII's (signed seed → 1-hour Bearer token). With <c>Fe:RequireToken</c> (default true) the other two require that token.</item>
/// <item><c>POST /{amb}/{rnc}/fe/recepcion/api/ecf</c> (multipart field <c>xml</c>): answers a signed ARECF, Estado 0 (received) or 1 with the reason
/// (1 specification, 2 signature, 3 duplicate, 4 buyer RNC does not match).</item>
/// <item><c>POST /{amb}/{rnc}/fe/aprobacioncomercial/api/ecf</c>: a buyer's ACECF about an e-CF the tenant issued; HTTP 200 or 400.</item>
/// </list>
/// <c>{amb}</c> is testecf, certecf or ecf; <c>{rnc}</c> picks the tenant (one active consent per RNC). Routing ignores case. Without consent (or a
/// disabled tenant) every route answers 503 and logs it. (verificar) that the DGII accepts a path prefix instead of a host per taxpayer (§11, question 7).</summary>
internal static class FeReceiverEndpoints
{
    public static readonly TimeSpan SeedLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    public static readonly string[] Environments = ["testecf", "certecf", "ecf"];

    private const string Route = "/{amb}/{rnc}/fe";

    /// <summary>R9 #2: anyone can call the receiver, and each call may sign in Key Vault (one vault for every tenant): limited per caller IP
    /// (<c>Fe:RequestsPerMinutePerIp</c>, 60) and per RNC (<c>Fe:RequestsPerMinutePerRnc</c>, 600), whatever the environment or the case in the URL.</summary>
    /// <remarks>R11 B-Med7: the anonymous authentication routes (seed, validacioncertificado) have their own per-RNC bucket
    /// (<c>Fe:SeedRequestsPerMinutePerRnc</c>, 60), so a flood of seeds never uses up the reception and approval calls of that RNC.</remarks>
    public static PartitionedRateLimiter<HttpContext> RateLimiter(IConfiguration config)
    {
        var perIp = config.GetValue("Fe:RequestsPerMinutePerIp", 60);
        var perRnc = config.GetValue("Fe:RequestsPerMinutePerRnc", 600);
        var seedsPerRnc = config.GetValue("Fe:SeedRequestsPerMinutePerRnc", 60);
        static FixedWindowRateLimiterOptions PerMinute(int limit) => new() { PermitLimit = limit, Window = TimeSpan.FromMinutes(1) };
        return PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(http => ReceiverPattern(http) is not null
                ? RateLimitPartition.GetFixedWindowLimiter("ip:" + http.Connection.RemoteIpAddress, _ => PerMinute(perIp))
                : RateLimitPartition.GetNoLimiter("")),
            PartitionedRateLimiter.Create<HttpContext, string>(http => ReceiverPattern(http) is { } pattern
                ? pattern.Contains("/autenticacion/", StringComparison.Ordinal)
                    ? RateLimitPartition.GetFixedWindowLimiter("seed-rnc:" + Rnc(http), _ => PerMinute(seedsPerRnc))
                    : RateLimitPartition.GetFixedWindowLimiter("rnc:" + Rnc(http), _ => PerMinute(perRnc))
                : RateLimitPartition.GetNoLimiter("")));

        static string? Rnc(HttpContext http) => (http.GetRouteValue("rnc") as string)?.ToLowerInvariant();
    }

    private static string? ReceiverPattern(HttpContext http) =>
        http.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } pattern } && pattern.StartsWith(Route, StringComparison.Ordinal) ? pattern : null;

    public static void MapFeReceiver(this IEndpointRouteBuilder app)
    {
        var fe = app.MapGroup(Route).AllowAnonymous().WithTags("Fe");

        fe.MapGet("/autenticacion/api/semilla", async (string amb, string rnc, HttpContext http, FeTokens tokens, CancellationToken ct) =>
        {
            var (receiver, env, refusal) = await OpenAsync(amb, rnc, http, needsToken: false, ct);
            if (refusal is not null) return refusal;
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            var seed = new XDocument(new XElement("SemillaModel",
                new XElement("valor", tokens.NewSeed(receiver!.TenantId, env)), new XElement("fecha", now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))));
            return Results.Text(seed.ToString(SaveOptions.DisableFormatting), "application/xml", Encoding.UTF8);
        });

        // R9 #1: the seed must be signed by a certificate of an INDOTEL authority (no RNC in a seed: the e-CF it then sends must be its issuer's).
        fe.MapPost("/autenticacion/api/validacioncertificado", async (string amb, string rnc, HttpContext http, FeTokens tokens, FeTrust trust, TimeProvider clock,
            CancellationToken ct) =>
        {
            var (receiver, env, refusal) = await OpenAsync(amb, rnc, http, needsToken: false, ct);
            if (refusal is not null) return refusal;
            var doc = await XmlFieldAsync(http.Request, ct) is { } bytes ? FeXml.Parse(bytes) : null;
            if (doc?.DocumentElement?.Name != "SemillaModel" || FeXml.Text(doc, "/SemillaModel/valor") is not { } valor
                || !tokens.SeedIsValid(valor, receiver!.TenantId, env) || !trust.SignedByTrusted(doc))
                return Results.Unauthorized();
            var (token, expires) = tokens.NewToken(receiver.TenantId, env);
            return Results.Ok(new { token, expira = expires.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                expedido = clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) });
        });

        fe.MapPost("/recepcion/api/ecf", ReceiveAsync);
        fe.MapPost("/aprobacioncomercial/api/ecf", ReceiveApprovalAsync);
    }

    /// <summary>Most versions of one e-NCF kept as a <see cref="FeReceivedDocument.Conflict"/>; more are answered as duplicates.</summary>
    public const int MaxVersions = 3;

    /// <summary>Receives a supplier's e-CF and answers the signed acknowledgement (ARECF). A document without a readable issuer RNC and e-NCF
    /// cannot be acknowledged (the ARECF needs both): 400.</summary>
    /// <remarks>R9 #1: the signature counts only from a certificate of an INDOTEL authority that belongs to <c>RNCEmisor</c>, and the same e-NCF
    /// with other content is kept next to the first one, both flagged, instead of answered as a duplicate.
    /// shortcut: the DGII is not asked whether it accepted the e-CF before the store gets it (consultaestado; question 34).</remarks>
    private static async Task<IResult> ReceiveAsync(string amb, string rnc, HttpContext http, CloudDbContext db, FeSigner signer, FeTrust trust, TimeProvider clock,
        CancellationToken ct)
    {
        var (receiver, env, refusal) = await OpenAsync(amb, rnc, http, needsToken: true, ct);
        if (refusal is not null) return refusal;
        var bytes = await XmlFieldAsync(http.Request, ct);
        var doc = bytes is null ? null : FeXml.Parse(bytes);
        var issuer = doc is null ? null : FeXml.Text(doc, "/ECF/Encabezado/Emisor/RNCEmisor");
        var encf = doc is null ? null : FeXml.Text(doc, "/ECF/Encabezado/IdDoc/eNCF");
        if (doc is null || !Rnc.IsValid(issuer) || encf is not { Length: 13 } || !encf.All(char.IsAsciiLetterOrDigit))
            return Results.BadRequest();

        var buyer = FeXml.Text(doc, "/ECF/Encabezado/Comprador/RNCComprador");
        var now = clock.GetUtcNow().UtcDateTime;
        var received = Read(doc, bytes!, receiver!.TenantId, env, issuer!, encf, buyer ?? "", now);
        var versions = db.FeReceived.Where(d => d.TenantId == receiver.TenantId && d.Environment == env && d.IssuerRnc == issuer && d.ENcf == encf);
        // TK-246: the same bytes kept pending (the store was off and the supplier got a 503) are the supplier's retry: acknowledged now as received.
        var pending = received is null ? null : await versions.FirstOrDefaultAsync(d => d.Sha256 == received.Sha256 && d.AcknowledgedUtc == null, ct);
        var signedBy = FeSignerCheck.Untrusted;
        int? reason = pending is not null ? null
            : received is null || (FeXml.EcfSchema(received.Type) is { } schema && FeXml.Validate(bytes!, schema).Count > 0) ? 1
            : (signedBy = trust.Check(doc, issuer)) == FeSignerCheck.Untrusted ? 2
            : buyer != receiver.Rnc ? 4
            : await versions.AnyAsync(d => d.Sha256 == received.Sha256, ct) || await versions.CountAsync(ct) >= MaxVersions ? 3
            : null;
        // R11 B-High2: a cédula signing for the issuer's company is acknowledged (the supplier did deliver it) but flagged "firmante sin verificar".
        if (reason is null && pending is null) received!.SignerUnverified = signedBy == FeSignerCheck.UnverifiedDelegate;
        var kept = pending ?? (reason is null ? received : null);
        if (kept is not null) kept.AcknowledgedUtc = now;
        if (pending is null && kept is not null) db.FeReceived.Add(kept);

        string? signed;
        try { signed = await AcknowledgeAsync(signer, receiver, issuer!, Rnc.IsValid(buyer) ? buyer! : receiver.Rnc, encf, reason, clock, ct); }
        catch (DbUpdateException) when (reason is null)
        {
            db.ChangeTracker.Clear(); // the same e-CF arrived twice at once: the other request kept it
            kept = null;
            signed = await AcknowledgeAsync(signer, receiver, issuer!, buyer!, encf, 3, clock, ct);
            reason = 3;
        }
        if (signed is null)
        {
            // TK-246: the store did not sign in time. What was received stays, pending its acknowledgement (signed and kept when the store is back,
            // FeLateAcknowledger); the supplier is told to retry. (verificar) §11, question 44.
            if (kept is not null) kept.AcknowledgedUtc = null;
            await db.SaveChangesAsync(ct);
            http.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        // After the save, so two versions arriving at once are flagged too.
        if (reason is null && await versions.CountAsync(ct) > 1)
            await versions.ExecuteUpdateAsync(s => s.SetProperty(d => d.Conflict, true), ct);
        return Results.Text(signed, "application/xml", Encoding.UTF8);
    }

    /// <summary>What a 503 of the receiver tells the supplier to wait before retrying (TK-246).</summary>
    public const int RetryAfterSeconds = 60;

    /// <summary>A buyer's commercial approval (ACECF) of an e-CF this tenant issued, signed by a certificate of an INDOTEL authority that belongs to
    /// <c>RNCComprador</c> (R9 #1; R11 B-High2: a cédula for a company buyer is not that). Kept (10 years) and shown with the archived e-CF.
    /// R11 B-Med3: the first approval of a buyer for an e-NCF stays; a repeat is answered 200 and not stored again.</summary>
    private static async Task<IResult> ReceiveApprovalAsync(string amb, string rnc, HttpContext http, CloudDbContext db, FeTrust trust, TimeProvider clock,
        CancellationToken ct)
    {
        var (receiver, env, refusal) = await OpenAsync(amb, rnc, http, needsToken: true, ct);
        if (refusal is not null) return refusal;
        var bytes = await XmlFieldAsync(http.Request, ct);
        var doc = bytes is null ? null : FeXml.Parse(bytes);
        const string detail = "/ACECF/DetalleAprobacionComercial/";
        if (doc?.DocumentElement?.Name != "ACECF" || FeXml.Validate(bytes!, FeXml.Acecf).Count > 0 || FeXml.Text(doc, detail + "RNCEmisor") != receiver!.Rnc
            || FeXml.Text(doc, detail + "RNCComprador") is not { } buyer || FeXml.Text(doc, detail + "eNCF") is not { } encf || !trust.SignedByTrusted(doc, buyer))
            return Results.BadRequest();
        if (await db.FeApprovalsReceived.AnyAsync(a => a.TenantId == receiver.TenantId && a.Environment == env && a.ENcf == encf && a.BuyerRnc == buyer, ct))
            return Results.Ok();
        db.FeApprovalsReceived.Add(new FeApprovalReceived
        {
            TenantId = receiver.TenantId, Environment = env, ENcf = encf, BuyerRnc = buyer,
            Approved = FeXml.Text(doc, detail + "Estado") == "1", Reason = FeXml.Text(doc, detail + "DetalleMotivoRechazo"), XmlGzip = FeXml.Zip(bytes!),
            ReceivedUtc = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    /// <summary>The fields the store needs (purchases, 606), or null when one the format requires is missing or unreadable. The document is kept as
    /// its original bytes (R9 #17).</summary>
    private static FeReceivedDocument? Read(XmlDocument doc, byte[] bytes, Guid tenant, string env, string issuer, string encf, string buyer, DateTime now)
    {
        const string head = "/ECF/Encabezado/";
        if (!int.TryParse(FeXml.Text(doc, head + "IdDoc/TipoeCF"), NumberStyles.None, CultureInfo.InvariantCulture, out var type)
            || !DateOnly.TryParseExact(FeXml.Text(doc, head + "Emisor/FechaEmision"), "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issued)
            || !decimal.TryParse(FeXml.Text(doc, head + "Totales/MontoTotal"), NumberStyles.Number, CultureInfo.InvariantCulture, out var total))
            return null;
        _ = decimal.TryParse(FeXml.Text(doc, head + "Totales/TotalITBIS"), NumberStyles.Number, CultureInfo.InvariantCulture, out var itbis);
        var name = FeXml.Text(doc, head + "Emisor/RazonSocialEmisor") ?? "";
        return new FeReceivedDocument
        {
            TenantId = tenant, Environment = env, IssuerRnc = issuer, IssuerName = name.Length > 250 ? name[..250] : name, BuyerRnc = buyer, ENcf = encf,
            Type = type, IssuedOn = issued, Total = total, Itbis = itbis, XmlGzip = FeXml.Zip(bytes), Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            ReceivedUtc = now,
        };
    }

    internal static async Task<string?> AcknowledgeAsync(FeSigner signer, FeCertificate receiver, string issuer, string buyer, string encf, int? reason,
        TimeProvider clock, CancellationToken ct)
    {
        var ack = new XDocument(new XElement("ARECF", new XElement("DetalleAcusedeRecibo",
            new XElement("Version", "1.0"), new XElement("RNCEmisor", issuer), new XElement("RNCComprador", buyer), new XElement("eNCF", encf),
            new XElement("Estado", reason is null ? 0 : 1), reason is null ? null : new XElement("CodigoMotivoNoRecibido", reason),
            new XElement("FechaHoraAcuseRecibo", FeXml.DateTimeText(clock.GetUtcNow().UtcDateTime)))));
        if (FeXml.Validate(ack.ToString(SaveOptions.DisableFormatting), FeXml.Arecf) is [var error, ..])
            throw new InvalidOperationException($"Toki built an ARECF that fails its XSD: {error}");
        // R9 #2: a refusal is signed and logged (hash only); only what was received keeps its acknowledgement, the one the store may need.
        return await signer.SignAsync(receiver, ack, encf, keepXml: reason is null, ct);
    }

    /// <summary>The tenant behind <c>{rnc}</c>, or the answer when there is none: 404 for an unknown environment or a malformed RNC, 503 without
    /// consent (logged), 401 without a valid token when <paramref name="needsToken"/>.</summary>
    private static async Task<(FeCertificate? Receiver, string Env, IResult? Refusal)> OpenAsync(string amb, string rnc, HttpContext http, bool needsToken,
        CancellationToken ct)
    {
        var env = amb.ToLowerInvariant();
        if (!Environments.Contains(env) || !Rnc.IsValid(rnc)) return (null, env, Results.NotFound());
        var db = http.RequestServices.GetRequiredService<CloudDbContext>();
        var receiver = await db.FeCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Rnc == rnc && c.RevokedUtc == null, ct);
        if (receiver is null || await db.Tenants.AnyAsync(t => t.Id == receiver.TenantId && t.DisabledUtc != null, ct))
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Toki.Fe")
                .LogWarning("e-CF receiver of RNC {Rnc} ({Environment}) answered 503: no consent on file", rnc, env);
            return (null, env, Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
        if (needsToken && !http.RequestServices.GetRequiredService<FeTokens>().Authorized(http.Request, receiver.TenantId, env))
            return (null, env, Results.Unauthorized());
        return (receiver, env, null);
    }

    /// <summary>The <c>xml</c> field of the multipart body (file or value) as its bytes, capped at <see cref="FeXml.MaxBytes"/>.</summary>
    internal static async Task<byte[]?> XmlFieldAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType || request.ContentLength > 2L * FeXml.MaxBytes) return null;
        try
        {
            var form = await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = FeXml.MaxBytes, ValueLengthLimit = FeXml.MaxBytes }, ct);
            if (form.Files.GetFile("xml") is not { } file) return form["xml"].FirstOrDefault() is { } value ? Encoding.UTF8.GetBytes(value) : null;
            if (file.Length > FeXml.MaxBytes) return null;
            using var bytes = new MemoryStream();
            await file.CopyToAsync(bytes, ct);
            return bytes.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or BadHttpRequestException) { return null; }
    }
}

/// <summary>Seeds and tokens of the receiver authentication. Stateless (Data Protection, so any instance reads them) and tied to one tenant and
/// environment; the expiry follows the app clock.</summary>
internal sealed class FeTokens(IDataProtectionProvider protection, TimeProvider clock, IConfiguration config)
{
    private readonly IDataProtector _seeds = protection.CreateProtector("Toki.Fe.Seed");
    private readonly IDataProtector _tokens = protection.CreateProtector("Toki.Fe.Token");

    public bool Required => config.GetValue("Fe:RequireToken", true);

    public string NewSeed(Guid tenant, string env) => _seeds.Protect(Payload(tenant, env, clock.GetUtcNow() + FeReceiverEndpoints.SeedLifetime));

    public bool SeedIsValid(string seed, Guid tenant, string env) => Valid(_seeds, seed, tenant, env);

    public (string Token, DateTimeOffset Expires) NewToken(Guid tenant, string env)
    {
        var expires = clock.GetUtcNow() + FeReceiverEndpoints.TokenLifetime;
        return (_tokens.Protect(Payload(tenant, env, expires)), expires);
    }

    public bool Authorized(HttpRequest request, Guid tenant, string env)
    {
        if (!Required) return true;
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && Valid(_tokens, header[7..].Trim(), tenant, env);
    }

    private static string Payload(Guid tenant, string env, DateTimeOffset expires) =>
        string.Join('|', tenant.ToString("N"), env, expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), Guid.NewGuid().ToString("N"));

    private bool Valid(IDataProtector protector, string value, Guid tenant, string env)
    {
        try
        {
            var parts = protector.Unprotect(value).Split('|');
            return parts.Length == 4 && parts[0] == tenant.ToString("N") && parts[1] == env
                && long.TryParse(parts[2], CultureInfo.InvariantCulture, out var expires) && expires > clock.GetUtcNow().ToUnixTimeSeconds();
        }
        catch (CryptographicException) { return false; }
    }
}
