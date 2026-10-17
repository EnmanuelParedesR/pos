using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Nodes;

public static class CloudAuth
{
    public const string NodeScheme = "Node";
    public const string NodePolicy = "Node";
    public const string PanelPolicy = "Panel";
    /// <summary>TK-217: every panel write. A ReadOnly member gets 403.</summary>
    public const string OwnerPolicy = "PanelOwner";
    public const string RegisterNodeRateLimit = "register-node";
}

/// <param name="PublicKey">TK-241: the node's ECDSA P-256 public key (SubjectPublicKeyInfo, Base64).</param>
public sealed record RegisterNodeRequest(string? Code, string? NodeName, string? PublicKey = null);
public sealed record RegisterNodeResponse(Guid TenantId, Guid StoreId, Guid NodeId);

internal static class NodeEndpoints
{
    public static void MapNodes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/nodes/register", async (RegisterNodeRequest request, CloudDbContext db, TimeProvider clock, FailedCodes failed, CancellationToken ct) =>
        {
            var invalid = Error.Validation("Nodes.InvalidCode", "El código no es válido o ya venció. Pida uno nuevo en el panel.");
            if (failed.Blocked)
                return Results.Problem(detail: "Hubo demasiados intentos de conexión. Espere unos minutos y vuelva a intentar.", statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Nodes.TooManyAttempts", extensions: new Dictionary<string, object?> { ["code"] = "Nodes.TooManyAttempts" });
            var code = request.Code?.Trim() ?? "";
            var name = request.NodeName?.Trim() ?? "";
            if (code.Length != 8 || name.Length is 0 or > 100) return EndpointExtensions.Problem(invalid);
            if (NodeSignature.PublicKey(request.PublicKey) is not { } publicKey)
                return EndpointExtensions.Problem(Error.Validation("Nodes.UpdateRequired", "Actualice Abastalia en la PC principal de la tienda y vuelva a conectarla."));

            var now = clock.GetUtcNow().UtcDateTime;
            var found = await db.ConnectionCodes.AsNoTracking()
                .Where(c => c.Code == code && c.UsedUtc == null && c.ExpiresUtc > now).SingleOrDefaultAsync(ct);
            // Taking the code is one conditional update, so two PCs typing it at once cannot both get in.
            if (found is null || await db.ConnectionCodes.Where(c => c.Id == found.Id && c.UsedUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedUtc, now), ct) != 1)
            {
                failed.Add();
                return EndpointExtensions.Problem(invalid);
            }

            var node = new Node
            {
                Id = Guid.CreateVersion7(), TenantId = found.TenantId, StoreId = found.StoreId, Name = name, PublicKey = publicKey, CreatedUtc = now,
            };
            db.Nodes.Add(node);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RegisterNodeResponse(node.TenantId, node.StoreId, node.Id));
        }).AllowAnonymous().RequireRateLimiting(CloudAuth.RegisterNodeRateLimit);

        // TK-241: the store disconnected; its credentials stop working here too.
        app.MapPost("/api/nodes/revoke", async (ClaimsPrincipal user, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var nodeId = NodeIdentity.From(user).NodeId;
            var now = clock.GetUtcNow().UtcDateTime;
            await db.Nodes.Where(n => n.Id == nodeId && n.RevokedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.RevokedUtc, now), ct);
            return Results.NoContent();
        }).RequireAuthorization(CloudAuth.NodePolicy);

        // TK-241: an Owner removes a register from "Tiendas" (a PC that was replaced or lost).
        app.MapPost("/api/panel/nodes/{nodeId:guid}/revoke", async (Guid nodeId, ClaimsPrincipal user, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var tenant = user.GetTenantId();
            var now = clock.GetUtcNow().UtcDateTime;
            if (!await db.Nodes.AnyAsync(n => n.Id == nodeId && n.TenantId == tenant, ct))
                return EndpointExtensions.Problem(Error.NotFound("Nodes.NotFound", "No encontramos esa caja."));
            await db.Nodes.Where(n => n.Id == nodeId && n.TenantId == tenant && n.RevokedUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.RevokedUtc, now), ct);
            return Results.NoContent();
        }).RequireAuthorization(CloudAuth.OwnerPolicy);
    }
}

/// <summary>TK-241: <c>Authorization: Node &lt;nodeId&gt;.&lt;unixSeconds&gt;.&lt;nonce&gt;.&lt;signature&gt;</c>, the signature (ECDSA P-256, SHA-256,
/// IEEE P1363, Base64Url) over <c>nodeId.unixSeconds.nonce.METHOD /path?query bodyHash</c> with the node's private key, bodyHash being
/// Base64Url(SHA-256(body)) (of nothing for an empty body, R10 #7; of the JSON for a gzip batch, which RequestDecompression inflates first). Valid for
/// <see cref="Window"/> either side of the cloud's clock and once only (the nonce). The store signs the same text (Abastalia.Sync CloudClient).</summary>
public static class NodeSignature
{
    /// <summary>Wide on purpose: shop PCs' clocks drift; the store also corrects by the cloud's Date header.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public static string Payload(Guid nodeId, long unixSeconds, string nonce, string method, string pathAndQuery, string bodyHash) =>
        $"{nodeId}.{unixSeconds}.{nonce}.{method.ToUpperInvariant()} {pathAndQuery} {bodyHash}";

    public static string BodyHash(ReadOnlySpan<byte> body) => Base64Url(SHA256.HashData(body));

    /// <summary>R12 #1: the bodyHash the node signed, sent in clear so the signature is checked before the body is read.</summary>
    public const string BodyHashHeader = "X-Body-Sha256";

    /// <summary>The SubjectPublicKeyInfo of a P-256 key, or null.</summary>
    public static byte[]? PublicKey(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > 200) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out _);
            return key.KeySize == 256 ? bytes : null;
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return null; }
    }

    public static bool Verify(byte[] publicKey, string payload, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(payload), FromBase64Url(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return false; }
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>A node nonce used inside the signature window (replay protection), until <see cref="ExpiresUtc"/>.</summary>
public sealed class UsedNonce
{
    /// <summary><c>nodeId.nonce</c>.</summary>
    public string Key { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }

    internal static void Configure(ModelBuilder builder) => builder.Entity<UsedNonce>(e =>
    {
        e.ToTable("NodeNonces");
        e.HasKey(n => n.Key);
        e.Property(n => n.Key).HasMaxLength(110);
        e.HasIndex(n => n.ExpiresUtc);
    });
}

/// <summary>Nonces already used inside the signature window (replay protection). R11 B-Low3: kept in the database, so a restart or a second
/// instance never accepts a replay; the primary key refuses the second use. Expired ones go at most once a minute.</summary>
public sealed class NodeNonces
{
    private long _lastSweepTicks;

    public async Task<bool> TryUseAsync(CloudDbContext db, string key, DateTime nowUtc, CancellationToken ct)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (nowUtc.Ticks - last > TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref _lastSweepTicks, nowUtc.Ticks, last) == last)
            await db.NodeNonces.Where(n => n.ExpiresUtc < nowUtc).ExecuteDeleteAsync(ct);
        // Runs before the endpoint, so nothing else is tracked yet; detached either way, so the endpoint's own SaveChanges never sees it.
        var entry = db.NodeNonces.Add(new UsedNonce { Key = key, ExpiresUtc = nowUtc + 2 * NodeSignature.Window });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // used already
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }
}

/// <summary>R11 B-Low5: wrong connection codes from anywhere, counted together. Past <c>Nodes:FailedCodesPer10Minutes</c> (200) every attempt waits
/// for the window to end, so many IPs together cannot walk the 8-digit space either.</summary>
public sealed class FailedCodes(TimeProvider clock, IConfiguration config)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly Lock _gate = new();
    private (int Count, DateTime Since) _failures;

    public bool Blocked
    {
        get
        {
            lock (_gate) return clock.GetUtcNow().UtcDateTime - _failures.Since < Window && _failures.Count >= config.GetValue("Nodes:FailedCodesPer10Minutes", 200);
        }
    }

    public void Add()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        lock (_gate) _failures = now - _failures.Since >= Window ? (1, now) : (_failures.Count + 1, _failures.Since);
    }
}

/// <summary>Node calls, signed with the node's key (<see cref="NodeSignature"/>, ADR-0008). The tenant and store come from the node row, never from the body.</summary>
internal sealed class NodeAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, CloudDbContext db,
    TimeProvider clock, NodeNonces nonces)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(CloudAuth.NodeScheme + " ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var parts = header[(CloudAuth.NodeScheme.Length + 1)..].Trim().Split('.');
        if (parts.Length != 4 || !Guid.TryParse(parts[0], out var nodeId)) return AuthenticateResult.Fail("Malformed node credentials.");

        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nodeId, Context.RequestAborted);
        // TK-189: revoked or disabled. The node keeps its events and retries; nothing is lost. R12 #1: checked before any of the body is read.
        if (node is null) return AuthenticateResult.Fail("Invalid node credentials.");
        if (node.RevokedUtc is not null) return AuthenticateResult.Fail("Revoked node.");
        if (!await SignatureMatchesAsync(node, parts)) return AuthenticateResult.Fail("Invalid node credentials.");
        if (await db.Tenants.AnyAsync(t => t.Id == node.TenantId && t.DisabledUtc != null, Context.RequestAborted)) return AuthenticateResult.Fail("Disabled tenant.");

        var identity = new ClaimsIdentity(
        [
            new Claim(TenantClaims.TenantId, node.TenantId.ToString()),
            new Claim(TenantClaims.StoreId, node.StoreId.ToString()),
            new Claim(TenantClaims.NodeId, node.Id.ToString()),
        ], CloudAuth.NodeScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), CloudAuth.NodeScheme));
    }

    private async Task<bool> SignatureMatchesAsync(Node node, string[] parts)
    {
        var (nonce, signature) = (parts[2], parts[3]);
        if (node.PublicKey is null || !long.TryParse(parts[1], out var seconds) || nonce.Length is < 16 or > 64) return false;
        var now = clock.GetUtcNow();
        if ((now - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253_402_300_799))).Duration() > NodeSignature.Window) return false;
        var pathAndQuery = $"{Request.Path}{Request.QueryString}";
        // R12 #1: the node says the body's hash in a header, so the signature is checked before a byte of the body is read; the body is hashed
        // afterwards and must match. Without the header (an older store) the body is hashed first, as before, except on a large upload.
        var claimed = Request.Headers[NodeSignature.BodyHashHeader].ToString();
        if (claimed.Length == 0)
        {
            if (Context.GetEndpoint()?.Metadata.GetMetadata<LargeNodeUpload>() is not null) return false;
            var hash = await BodyHashAsync();
            return NodeSignature.Verify(node.PublicKey, NodeSignature.Payload(node.Id, seconds, nonce, Request.Method, pathAndQuery, hash), signature)
                && await nonces.TryUseAsync(db, $"{node.Id}.{nonce}", now.UtcDateTime, Context.RequestAborted);
        }
        return claimed.Length <= 64
            && NodeSignature.Verify(node.PublicKey, NodeSignature.Payload(node.Id, seconds, nonce, Request.Method, pathAndQuery, claimed), signature)
            && await nonces.TryUseAsync(db, $"{node.Id}.{nonce}", now.UtcDateTime, Context.RequestAborted)
            && await BodyHashAsync() == claimed;
    }

    /// <summary>R10 #7: the body is signed too. Buffered so the endpoint still reads it.</summary>
    private async Task<string> BodyHashAsync()
    {
        Request.EnableBuffering();
        var hash = NodeSignature.Base64Url(await SHA256.HashDataAsync(Request.Body, Context.RequestAborted));
        Request.Body.Position = 0;
        return hash;
    }
}

/// <summary>R12 #1: an endpoint that takes a large file from a node (a backup): the signed body-hash header is required and the body is never
/// compressed. <paramref name="TooLarge"/>: what the store shows when the file is over the route's limit.</summary>
public sealed record LargeNodeUpload(Error TooLarge);

/// <summary>R12 #1: what stops a node call before it is authenticated (so before its body is buffered and hashed): per IP, at most
/// <c>Nodes:ConcurrentPerIp</c> (8) at once and <c>Nodes:RequestsPerMinutePerIp</c> (300) a minute; a body needs its Content-Length, within the
/// route's limit; a large upload is never compressed. A node call is any <c>/api/nodes/*</c> or one signed <c>Node …</c>.</summary>
internal static class NodeGate
{
    public static void UseNodeGate(this WebApplication app)
    {
        var config = app.Configuration;
        var concurrent = config.GetValue("Nodes:ConcurrentPerIp", 8);
        var perMinute = config.GetValue("Nodes:RequestsPerMinutePerIp", 300);
        var limiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(h => RateLimitPartition.GetConcurrencyLimiter(Ip(h),
                _ => new ConcurrencyLimiterOptions { PermitLimit = concurrent, QueueLimit = 0 })),
            PartitionedRateLimiter.Create<HttpContext, string>(h => RateLimitPartition.GetFixedWindowLimiter(Ip(h),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) })));
        app.Use(async (http, next) =>
        {
            if (!IsNodeCall(http.Request))
            {
                await next(http);
                return;
            }
            using var lease = await limiter.AcquireAsync(http, 1, http.RequestAborted);
            if (!lease.IsAcquired)
            {
                if (lease.TryGetMetadata(MetadataName.RetryAfter, out var after))
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(after.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }
            if (Refused(http) is { } refused)
            {
                await refused.ExecuteAsync(http);
                return;
            }
            await next(http);
        });
    }

    private static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "";

    private static bool IsNodeCall(HttpRequest request) =>
        request.Path.StartsWithSegments("/api/nodes") || request.Headers.Authorization.ToString().StartsWith(CloudAuth.NodeScheme + " ", StringComparison.Ordinal);

    /// <summary>411 a body without its length, 413 one over the route's limit, 415 a compressed large upload.</summary>
    private static IResult? Refused(HttpContext http)
    {
        var request = http.Request;
        var endpoint = http.GetEndpoint();
        var large = endpoint?.Metadata.GetMetadata<LargeNodeUpload>();
        if (request.ContentLength is not { } length)
            return http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
                ? Problem(StatusCodes.Status411LengthRequired, "Nodes.LengthRequired", "El envío no dice cuánto pesa. Actualice Abastalia en la PC de la tienda.") : null;
        var limit = endpoint?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize
                    ?? http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
        if (length > limit)
            return Problem(StatusCodes.Status413PayloadTooLarge, large?.TooLarge.Code ?? "Nodes.TooLarge", large?.TooLarge.Description ?? "El envío pesa más de lo que acepta la nube.");
        if (large is not null && request.Headers.ContentEncoding.Count > 0)
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Nodes.CompressedUpload", "Ese archivo se envía sin comprimir. Actualice Abastalia en la PC de la tienda.");
        return null;
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(detail: detail, statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>Who is calling, for node endpoints.</summary>
public sealed record NodeIdentity(Guid TenantId, Guid StoreId, Guid NodeId)
{
    public static NodeIdentity From(ClaimsPrincipal user) => new(
        Guid.Parse(user.FindFirstValue(TenantClaims.TenantId)!),
        Guid.Parse(user.FindFirstValue(TenantClaims.StoreId)!),
        Guid.Parse(user.FindFirstValue(TenantClaims.NodeId)!));
}
