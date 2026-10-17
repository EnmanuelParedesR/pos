using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Platform;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Accounts;

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
            Context.TenantMembers.Add(new TenantMember { TenantId = tenant.Id, UserId = user.Id, Role = MemberRole.Owner, JoinedUtc = tenant.CreatedUtc });
            user.TenantId = tenant.Id;
        }
        return base.CreateAsync(user, cancellationToken);
    }
}

/// <summary>Puts the tenant in the bearer token. The platform role (TK-189) is only the stored user claim that <c>grant-platform-admin</c> adds
/// (the base factory copies it); R8 #1 removed the <c>Platform:Admins</c> e-mail list, since anyone could register one of those e-mails.</summary>
/// <remarks>TK-217 / R8 #18: the token takes the current tenant if the user is still its member and it is active, else the first active tenant they
/// belong to. The role is not in the token: the policies read it from the database on every call, so a change or a removal counts at once.</remarks>
internal sealed class CloudClaimsFactory(UserManager<CloudUser> users, IOptions<IdentityOptions> options, CloudDbContext db)
    : UserClaimsPrincipalFactory<CloudUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(CloudUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var active = await CloudSignInManager.ActiveTenantsAsync(db, user.Id);
        var tenantId = active.Count == 0 || active.Contains(user.TenantId) ? user.TenantId : active[0];
        identity.AddClaim(new Claim(TenantClaims.TenantId, tenantId.ToString()));
        return identity;
    }
}

/// <summary>A user signs in while at least one of their tenants is active (TK-189, R8 #18); otherwise the login answers 401 "NotAllowed".
/// An account with no membership left (removed everywhere) keeps the old rule: its own tenant must be active (the panel then refuses every call).</summary>
internal sealed class CloudSignInManager(UserManager<CloudUser> users, IHttpContextAccessor http, IUserClaimsPrincipalFactory<CloudUser> claims,
    IOptions<IdentityOptions> options, ILogger<SignInManager<CloudUser>> logger, IAuthenticationSchemeProvider schemes, IUserConfirmation<CloudUser> confirmation,
    CloudDbContext db, LoginThrottle throttle)
    : SignInManager<CloudUser>(users, http, claims, options, logger, schemes, confirmation)
{
    /// <summary>R11 B-Low6: wrong passwords count per IP and account (<see cref="LoginThrottle"/>); a blocked pair answers as a lockout.</summary>
    public override async Task<SignInResult> PasswordSignInAsync(CloudUser user, string password, bool isPersistent, bool lockoutOnFailure)
    {
        var key = LoginThrottle.Key(Context, user);
        if (throttle.IsBlocked(key)) return SignInResult.LockedOut;
        var result = await base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
        if (result == SignInResult.Failed) throttle.Fail(key);
        else if (result.Succeeded) throttle.Succeeded(key);
        return result;
    }

    /// <summary>The same count for the authenticator code of an account with 2FA (R11 B-Med6).</summary>
    public override Task<SignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient) =>
        CountedAsync(() => base.TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient));

    /// <summary>TK-298: a backup code instead of the app's code (lost phone) counts the same.</summary>
    public override Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode) =>
        CountedAsync(() => base.TwoFactorRecoveryCodeSignInAsync(recoveryCode));

    private async Task<SignInResult> CountedAsync(Func<Task<SignInResult>> signIn)
    {
        var user = await GetTwoFactorAuthenticationUserAsync();
        var key = user is null ? null : LoginThrottle.Key(Context, user);
        if (key is not null && throttle.IsBlocked(key)) return SignInResult.LockedOut;
        var result = await signIn();
        if (key is not null && result == SignInResult.Failed) throttle.Fail(key);
        else if (key is not null && result.Succeeded) throttle.Succeeded(key);
        return result;
    }

    public override async Task<bool> CanSignInAsync(CloudUser user)
    {
        if (!await base.CanSignInAsync(user)) return false;
        if (await db.TenantMembers.AnyAsync(m => m.UserId == user.Id)) return (await ActiveTenantsAsync(db, user.Id)).Count > 0;
        return !await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.DisabledUtc != null);
    }

    /// <summary>The user's tenants that are not disabled, oldest membership first.</summary>
    public static Task<List<Guid>> ActiveTenantsAsync(CloudDbContext db, string userId) =>
        db.TenantMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Join(db.Tenants.Where(t => t.DisabledUtc == null), m => m.TenantId, t => t.Id, (m, t) => new { m.TenantId, m.JoinedUtc })
            .OrderBy(x => x.JoinedUtc).Select(x => x.TenantId).ToListAsync();
}

/// <summary>The panel policies, checked against the database on every call: the token's tenant must be active (TK-189: a token issued before
/// the tenant was disabled stops working too) and the user must still be its member (TK-217), an Owner for <see cref="CloudAuth.OwnerPolicy"/>.</summary>
internal static class PanelAccess
{
    public static Task<bool> IsMemberAsync(AuthorizationHandlerContext context) => HasRoleAsync(context, ownerOnly: false);

    public static Task<bool> IsOwnerAsync(AuthorizationHandlerContext context) => HasRoleAsync(context, ownerOnly: true);

    /// <summary>TK-201: Owners, and ReadOnly members an Owner allowed. Without it the panel and the assistant get no cost or profit.</summary>
    public static Task<bool> CanViewCostsAsync(ClaimsPrincipal user, CloudDbContext db, CancellationToken ct)
    {
        var (tenantId, userId) = (user.GetTenantId(), user.FindFirstValue(ClaimTypes.NameIdentifier));
        return db.TenantMembers.AnyAsync(m => m.TenantId == tenantId && m.UserId == userId && (m.Role == MemberRole.Owner || m.CanViewCosts), ct);
    }

    private static async Task<bool> HasRoleAsync(AuthorizationHandlerContext context, bool ownerOnly)
    {
        if (context.Resource is not HttpContext http || context.User.FindFirstValue(TenantClaims.TenantId) is not { } value || !Guid.TryParse(value, out var tenantId)
            || context.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { } userId)
            return false;
        var db = http.RequestServices.GetRequiredService<CloudDbContext>();
        if (await db.Tenants.AnyAsync(t => t.Id == tenantId && t.DisabledUtc != null, http.RequestAborted)) return false;
        var role = await db.TenantMembers.Where(m => m.TenantId == tenantId && m.UserId == userId).Select(m => (MemberRole?)m.Role).SingleOrDefaultAsync(http.RequestAborted);
        return role == MemberRole.Owner || (role is not null && !ownerOnly);
    }
}

public sealed record CreateStoreRequest(string? Name);
public sealed record StoreResponse(Guid Id, string Name, DateTime CreatedUtc, DateTime? LastEventUtc, DateTime? LastSyncUtc, int Nodes);
public sealed record ConnectionCodeResponse(string Code, DateTime ExpiresUtc);
public sealed record MyTenant(Guid TenantId, string Name, MemberRole Role);
/// <param name="IsPlatformAdmin">Abastalia staff (TK-189): the panel shows the "Plataforma" area.</param>
/// <param name="Role">In the current tenant (TK-217): the panel hides what a ReadOnly member cannot do (the API refuses it anyway).</param>
/// <param name="Tenants">Every tenant the user belongs to; with more than one the panel lets them switch.</param>
/// <param name="CanViewCosts">TK-201: cost and profit are shown (the API leaves them null otherwise).</param>
/// <param name="EmailConfirmed">TK-231: false = no reset e-mail reaches it; the panel offers to resend the confirmation.</param>
public sealed record MeResponse(string? Email, Guid TenantId, string TenantName, bool IsPlatformAdmin, MemberRole Role, IReadOnlyList<MyTenant> Tenants,
    bool CanViewCosts, bool EmailConfirmed);
public sealed record SwitchTenantRequest(Guid TenantId);

internal static class StoreEndpoints
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(30);
    public const int MaxLiveCodes = 5;

    public static void MapStores(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(CloudAuth.PanelPolicy);

        api.MapGet("/me", async (ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var tenants = await db.TenantMembers.AsNoTracking().Where(m => m.UserId == userId)
                .Join(db.Tenants.Where(t => t.DisabledUtc == null), m => m.TenantId, t => t.Id, (m, t) => new MyTenant(t.Id, t.Name, m.Role))
                .ToListAsync(ct);
            var current = tenants.Single(t => t.TenantId == tenantId); // the panel policy already checked the membership
            return Results.Ok(new MeResponse(user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name, tenantId, current.Name,
                await PlatformAuth.IsAdminInDbAsync(db, userId, ct), current.Role,
                [.. tenants.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)], await PanelAccess.CanViewCostsAsync(user, db, ct),
                await db.Users.Where(u => u.Id == userId).Select(u => u.EmailConfirmed).SingleAsync(ct)));
        });

        // TK-217: the panel then renews its token (POST /api/auth/refresh), which takes the new tenant.
        api.MapPost("/me/tenant", async (SwitchTenantRequest request, ClaimsPrincipal user, UserManager<CloudUser> users, CloudDbContext db, CancellationToken ct) =>
        {
            var account = await users.GetUserAsync(user);
            if (account is null || !await db.TenantMembers.AnyAsync(m => m.UserId == account.Id && m.TenantId == request.TenantId, ct)
                || await db.Tenants.AnyAsync(t => t.Id == request.TenantId && t.DisabledUtc != null, ct))
                return EndpointExtensions.Problem(Error.NotFound("Members.TenantNotFound", "No encontramos ese negocio entre los suyos."));
            account.TenantId = request.TenantId;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
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
        }).RequireAuthorization(CloudAuth.OwnerPolicy);

        api.MapPost("/stores/{id:guid}/connection-codes", async (Guid id, ClaimsPrincipal user, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            if (!await db.Stores.AnyAsync(s => s.Id == id && s.TenantId == tenantId, ct))
                return EndpointExtensions.Problem(Error.NotFound("Stores.NotFound", "No encontramos esa tienda."));
            var now = clock.GetUtcNow().UtcDateTime;
            // R11 B-Low5: few live codes per business, so guessing one stays hopeless.
            if (await db.ConnectionCodes.CountAsync(c => c.TenantId == tenantId && c.UsedUtc == null && c.ExpiresUtc > now, ct) >= MaxLiveCodes)
                return EndpointExtensions.Problem(Error.Conflict("Stores.TooManyCodes",
                    $"Ya hay {MaxLiveCodes} códigos de conexión sin usar. Use uno de ellos o espere a que venzan (30 minutos)."));
            string code;
            // Two live codes must never be equal: the code alone picks the store.
            do code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("00000000", System.Globalization.CultureInfo.InvariantCulture);
            while (await db.ConnectionCodes.AnyAsync(c => c.Code == code && c.UsedUtc == null && c.ExpiresUtc > now, ct));
            var connection = new ConnectionCode { Id = Guid.CreateVersion7(), TenantId = tenantId, StoreId = id, Code = code, ExpiresUtc = now + CodeLifetime };
            db.ConnectionCodes.Add(connection);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ConnectionCodeResponse(code, connection.ExpiresUtc));
        }).RequireAuthorization(CloudAuth.OwnerPolicy);
    }
}
