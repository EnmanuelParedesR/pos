using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Accounts;

public static class TenantClaims
{
    public const string TenantId = "tenant_id";
    public const string StoreId = "store_id";
    public const string NodeId = "node_id";

    /// <summary>The tenant always comes from the signed-in identity (panel token or node secret), never from the request.</summary>
    public static Guid GetTenantId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(TenantId)!);
}

/// <summary>Registering a panel user creates their tenant in the same SaveChanges (the store's), so there is never a user without one.</summary>
internal sealed class CloudUserStore(CloudDbContext db, TimeProvider clock, IdentityErrorDescriber? describer = null)
    : UserStore<CloudUser, IdentityRole, CloudDbContext>(db, describer)
{
    public override Task<IdentityResult> CreateAsync(CloudUser user, CancellationToken cancellationToken = default)
    {
        if (user.TenantId == Guid.Empty)
        {
            var tenant = new Tenant { Id = Guid.CreateVersion7(), Name = user.Email ?? user.UserName ?? "", CreatedUtc = clock.GetUtcNow().UtcDateTime };
            Context.Tenants.Add(tenant);
            user.TenantId = tenant.Id;
        }
        return base.CreateAsync(user, cancellationToken);
    }
}

/// <summary>Puts the tenant in the bearer token.</summary>
internal sealed class CloudClaimsFactory(UserManager<CloudUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<CloudUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(CloudUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(TenantClaims.TenantId, user.TenantId.ToString()));
        return identity;
    }
}

public sealed record CreateStoreRequest(string? Name);
public sealed record StoreResponse(Guid Id, string Name, DateTime CreatedUtc, DateTime? LastEventUtc, DateTime? LastSyncUtc, int Nodes);
public sealed record ConnectionCodeResponse(string Code, DateTime ExpiresUtc);
public sealed record MeResponse(string? Email, Guid TenantId, string TenantName);

internal static class StoreEndpoints
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(30);

    public static void MapStores(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(CloudAuth.PanelPolicy);

        api.MapGet("/me", async (ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
            return Results.Ok(new MeResponse(user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name, tenant.Id, tenant.Name));
        });

        api.MapGet("/stores", async (ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var stores = await db.Stores.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.Name).ToListAsync(ct);
            var nodes = await db.Nodes.AsNoTracking().Where(n => n.TenantId == tenantId).ToListAsync(ct);
            return Results.Ok(stores.Select(s =>
            {
                var own = nodes.Where(n => n.StoreId == s.Id).ToList();
                return new StoreResponse(s.Id, s.Name, s.CreatedUtc, own.Max(n => n.LastEventUtc), own.Max(n => n.LastSyncUtc), own.Count);
            }));
        });

        api.MapPost("/stores", async (CreateStoreRequest request, ClaimsPrincipal user, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100)
                return EndpointExtensions.Problem(Error.Validation("Stores.InvalidName", "Escribe el nombre de la tienda (hasta 100 letras)."));
            var store = new Store { Id = Guid.CreateVersion7(), TenantId = user.GetTenantId(), Name = name, CreatedUtc = clock.GetUtcNow().UtcDateTime };
            db.Stores.Add(store);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/stores/{store.Id}", new StoreResponse(store.Id, store.Name, store.CreatedUtc, null, null, 0));
        });

        api.MapPost("/stores/{id:guid}/connection-codes", async (Guid id, ClaimsPrincipal user, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            if (!await db.Stores.AnyAsync(s => s.Id == id && s.TenantId == tenantId, ct))
                return EndpointExtensions.Problem(Error.NotFound("Stores.NotFound", "No encontramos esa tienda."));
            var now = clock.GetUtcNow().UtcDateTime;
            string code;
            // Two live codes must never be equal: the code alone picks the store.
            do code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("00000000", System.Globalization.CultureInfo.InvariantCulture);
            while (await db.ConnectionCodes.AnyAsync(c => c.Code == code && c.UsedUtc == null && c.ExpiresUtc > now, ct));
            var connection = new ConnectionCode { Id = Guid.CreateVersion7(), TenantId = tenantId, StoreId = id, Code = code, ExpiresUtc = now + CodeLifetime };
            db.ConnectionCodes.Add(connection);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ConnectionCodeResponse(code, connection.ExpiresUtc));
        });
    }
}
