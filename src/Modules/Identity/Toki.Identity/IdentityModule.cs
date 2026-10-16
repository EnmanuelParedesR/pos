using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    /// <summary>Header carrying the device secret of registers paired before TK-091, or over plain http in development.</summary>
    public const string DeviceHeader = "X-Device";

    /// <summary>Whether a device may still prove itself with its secret (<see cref="DeviceHeader"/>): always over plain http (development,
    /// tests), and over https only while <c>Toki:LegacyDeviceSecrets</c> keeps registers paired before TK-091 working (ADR-0009).</summary>
    public static bool AcceptsDeviceSecrets(IConfiguration config) =>
        !config.GetValue<bool>("Toki:HttpsWithNodeCertificate") || config.GetValue<bool>("Toki:LegacyDeviceSecrets");

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<IdentityDbContext>();
        services.AddSingleton<StoreCertificateAuthority>();
        services.AddScoped<ISupervisorApprovals, SupervisorApprovals>();
        services.AddScoped<ICloudSnapshot, UserSnapshot>();
        services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(Scheme, null);
        // Secure by default: an endpoint that forgets its authorization call still requires a session.
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
}

/// <summary>Which paired device sent a request: by its client certificate (TK-091, issued by the store CA and checked by Kestrel), or by
/// its secret where <see cref="IdentityModule.AcceptsDeviceSecrets"/> allows it. A revoked device is not found either way.</summary>
internal static class PairedDevices
{
    public static Task<Guid?> IdOfAsync(this IdentityDbContext db, HttpContext http, IConfiguration config, CancellationToken ct)
    {
        IQueryable<Device> devices = db.Devices.Where(d => !d.IsRevoked);
        if (http.Connection.ClientCertificate is { } certificate)
        {
            // A certificate decides alone: an unknown or revoked one never falls back to a secret sent next to it.
            var thumbprint = StoreCertificateAuthority.Thumbprint(certificate);
            devices = devices.Where(d => d.CertificateThumbprint == thumbprint);
        }
        else
        {
            var secret = http.Request.Headers[IdentityModule.DeviceHeader].ToString();
            if (secret.Length == 0 || !IdentityModule.AcceptsDeviceSecrets(config)) return Task.FromResult<Guid?>(null);
            var hash = Token.Hash(secret);
            devices = devices.Where(d => d.TokenHash == hash);
        }
        return devices.Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
    }
}

/// <summary>Authenticates "Authorization: Bearer &lt;session&gt;" on the paired device the session was opened on.</summary>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IdentityDbContext db, IConfiguration config, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        if (await db.IdOfAsync(Context, config, Context.RequestAborted) is not { } deviceId) return AuthenticateResult.Fail("Unknown device.");

        var tokenHash = Token.Hash(auth["Bearer ".Length..].Trim());
        var now = clock.GetUtcNow().UtcDateTime;

        var data = await (
            from s in db.Sessions
            join u in db.Users on s.UserId equals u.Id
            join r in db.Roles on u.RoleId equals r.Id
            where s.TokenHash == tokenHash && s.ClosedUtc == null && s.ExpiresUtc > now && s.DeviceId == deviceId && u.IsActive
            select new { SessionId = s.Id, UserId = u.Id, u.Name, DeviceId = s.DeviceId, u.RoleId, r.Permissions })
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
