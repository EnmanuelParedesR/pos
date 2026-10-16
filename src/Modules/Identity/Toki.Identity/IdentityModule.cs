using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Identity.Contracts;
using Toki.Identity.Domain;
using Toki.Identity.Persistence;
using Toki.SharedKernel;
using Toki.Sync.Contracts;

namespace Toki.Identity;

public static class IdentityModule
{
    public const string Scheme = "Toki";
    /// <summary>Header carrying the paired device's secret on every request.</summary>
    public const string DeviceHeader = "X-Device";

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<IdentityDbContext>();
        services.AddScoped<ISupervisorApprovals, SupervisorApprovals>();
        services.AddScoped<ICloudSnapshot, UserSnapshot>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(Scheme, null);
        // Secure by default: an endpoint that forgets its authorization call still requires a session.
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
}

internal sealed class UserDirectory(IdentityDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyList<UserName>> AllAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(u => u.Name).Select(u => new UserName(u.Id, u.Name)).ToListAsync(ct);
}

/// <summary>Authenticates "Authorization: Bearer &lt;session&gt;" plus the device header the session was opened on.</summary>
/// <remarks>shortcut: device identity is a bearer secret, not a client certificate; move to mTLS pairing before the pilot (TK-091).</remarks>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IdentityDbContext db, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        var device = Request.Headers[IdentityModule.DeviceHeader].ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || device.Length == 0) return AuthenticateResult.NoResult();

        var tokenHash = Token.Hash(auth["Bearer ".Length..].Trim());
        var deviceHash = Token.Hash(device);
        var now = clock.GetUtcNow().UtcDateTime;

        var data = await (
            from s in db.Sessions
            join d in db.Devices on s.DeviceId equals d.Id
            join u in db.Users on s.UserId equals u.Id
            join r in db.Roles on u.RoleId equals r.Id
            where s.TokenHash == tokenHash && s.ClosedUtc == null && s.ExpiresUtc > now
                  && d.TokenHash == deviceHash && !d.IsRevoked && u.IsActive
            select new { SessionId = s.Id, UserId = u.Id, u.Name, DeviceId = d.Id, u.RoleId, r.Permissions })
            .AsNoTracking().FirstOrDefaultAsync(Context.RequestAborted);
        if (data is null) return AuthenticateResult.Fail("Invalid or expired session.");
        // The administrator always has every permission, including ones added after the database was created.
        var permissions = data.RoleId == Role.AdministratorId ? Permissions.All : data.Permissions;

        List<Claim> claims =
        [
            new(TokiClaims.User, data.UserId.ToString()),
            new(TokiClaims.Name, data.Name),
            new(TokiClaims.Device, data.DeviceId.ToString()),
            new(TokiClaims.Session, data.SessionId.ToString()),
            .. permissions.Select(p => new Claim(TokiClaims.Permission, p)),
        ];
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}

internal sealed class UserSnapshot(IdentityDbContext db, IOutbox outbox) : ICloudSnapshot
{
    public async Task EnqueueAsync(CancellationToken ct)
    {
        foreach (var u in await db.Users.AsNoTracking().Select(u => new { u.Id, u.Name, u.IsActive }).ToListAsync(ct))
            outbox.Add(new UserProfileChangedV1(u.Id, u.Name, u.IsActive));
    }
}

internal sealed class SupervisorApprovals(IdentityDbContext db, IUserContext user, TimeProvider clock) : ISupervisorApprovals
{
    public async Task<Result<Guid>> ConsumeAsync(Guid approvalId, string permission, Guid resourceId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var valid = db.Approvals.Where(a => a.Id == approvalId && a.UsedUtc == null && a.ExpiresUtc > now
            && a.Permission == permission && a.ResourceId == resourceId && a.DeviceId == user.DeviceId);
        // Conditional update: only one command can consume it, even when two race.
        if (await valid.ExecuteUpdateAsync(s => s.SetProperty(a => a.UsedUtc, now), ct) == 0)
            return IdentityErrors.InvalidApproval;
        return await db.Approvals.Where(a => a.Id == approvalId).Select(a => a.SupervisorId).SingleAsync(ct);
    }
}
