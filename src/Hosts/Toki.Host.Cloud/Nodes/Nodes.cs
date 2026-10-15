using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Nodes;

public static class CloudAuth
{
    public const string NodeScheme = "Node";
    public const string NodePolicy = "Node";
    public const string PanelPolicy = "Panel";
    /// <summary>TK-217: every panel write. A ReadOnly member gets 403.</summary>
    public const string OwnerPolicy = "PanelOwner";
    public const string RegisterNodeRateLimit = "register-node";
}

public sealed record RegisterNodeRequest(string? Code, string? NodeName);
public sealed record RegisterNodeResponse(Guid TenantId, Guid StoreId, Guid NodeId, string NodeSecret);

internal static class NodeEndpoints
{
    public static void MapNodes(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/nodes/register", async (RegisterNodeRequest request, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var invalid = Error.Validation("Nodes.InvalidCode", "El código no es válido o ya venció. Pide uno nuevo en el panel.");
            var code = request.Code?.Trim() ?? "";
            var name = request.NodeName?.Trim() ?? "";
            if (code.Length != 8 || name.Length is 0 or > 100) return EndpointExtensions.Problem(invalid);

            var now = clock.GetUtcNow().UtcDateTime;
            var found = await db.ConnectionCodes.AsNoTracking()
                .Where(c => c.Code == code && c.UsedUtc == null && c.ExpiresUtc > now).SingleOrDefaultAsync(ct);
            // Taking the code is one conditional update, so two PCs typing it at once cannot both get in.
            if (found is null || await db.ConnectionCodes.Where(c => c.Id == found.Id && c.UsedUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedUtc, now), ct) != 1)
                return EndpointExtensions.Problem(invalid);

            var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
            var node = new Node
            {
                Id = Guid.CreateVersion7(), TenantId = found.TenantId, StoreId = found.StoreId, Name = name,
                SecretHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret)), CreatedUtc = now,
            };
            db.Nodes.Add(node);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RegisterNodeResponse(node.TenantId, node.StoreId, node.Id, secret));
        }).AllowAnonymous().RequireRateLimiting(CloudAuth.RegisterNodeRateLimit);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary><c>Authorization: Node &lt;nodeId&gt;.&lt;nodeSecret&gt;</c>. The tenant and store come from the node row, never from the body.</summary>
/// <remarks>shortcut: a shared secret per node instead of a client certificate or private_key_jwt (ADR-0008); change it before a second paying customer.</remarks>
internal sealed class NodeAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, CloudDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(CloudAuth.NodeScheme + " ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var value = header[(CloudAuth.NodeScheme.Length + 1)..].Trim();
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || !Guid.TryParse(value[..dot], out var nodeId)) return AuthenticateResult.Fail("Malformed node credentials.");

        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nodeId, Context.RequestAborted);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value[(dot + 1)..]));
        if (node is null || !CryptographicOperations.FixedTimeEquals(hash, node.SecretHash)) return AuthenticateResult.Fail("Invalid node credentials.");
        // TK-189: Toki staff revoked the node or disabled the tenant. The node keeps its events and retries; nothing is lost.
        if (node.RevokedUtc is not null) return AuthenticateResult.Fail("Revoked node.");
        if (await db.Tenants.AnyAsync(t => t.Id == node.TenantId && t.DisabledUtc != null, Context.RequestAborted)) return AuthenticateResult.Fail("Disabled tenant.");

        var identity = new ClaimsIdentity(
        [
            new Claim(TenantClaims.TenantId, node.TenantId.ToString()),
            new Claim(TenantClaims.StoreId, node.StoreId.ToString()),
            new Claim(TenantClaims.NodeId, node.Id.ToString()),
        ], CloudAuth.NodeScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), CloudAuth.NodeScheme));
    }
}

/// <summary>Who is calling, for node endpoints.</summary>
public sealed record NodeIdentity(Guid TenantId, Guid StoreId, Guid NodeId)
{
    public static NodeIdentity From(ClaimsPrincipal user) => new(
        Guid.Parse(user.FindFirstValue(TenantClaims.TenantId)!),
        Guid.Parse(user.FindFirstValue(TenantClaims.StoreId)!),
        Guid.Parse(user.FindFirstValue(TenantClaims.NodeId)!));
}
