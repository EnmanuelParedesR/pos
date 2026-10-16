using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
    /// <summary>Header carrying the device secret over plain http (development, tests).</summary>
    public const string DeviceHeader = "X-Device";

    /// <summary>Whether a device may prove itself with its secret (<see cref="DeviceHeader"/>): only over plain http (development, tests);
    /// over https a client certificate is the only way (ADR-0009).</summary>
    public static bool AcceptsDeviceSecrets(IConfiguration config) => !config.GetValue<bool>("Toki:HttpsWithNodeCertificate");

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<IdentityDbContext>();
        services.AddSingleton<StoreCertificateAuthority>();
        services.AddHealthChecks().AddCheck<StoreCaHealthCheck>("store-ca");
        services.AddScoped<IRestoreGuard, IdentityRestoreGuard>();
        services.AddScoped<ISupervisorApprovals, SupervisorApprovals>();
        services.AddScoped<ICloudSnapshot, UserSnapshot>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IDevicePeripherals, DevicePeripherals>();
        services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(Scheme, null);
        // Secure by default: an endpoint that forgets its authorization call still requires a session.
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
}

/// <summary>R9 #8: registers paired with a certificate of a store CA that could not be read and was replaced cannot connect any more.</summary>
internal sealed class StoreCaHealthCheck(StoreCertificateAuthority ca, IdentityDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var created = ca.CreatedUtc;
        var stale = await db.Devices.Where(d => !d.IsRevoked && d.CertificateThumbprint != null && d.CreatedUtc < created)
            .Select(d => d.Name).ToListAsync(cancellationToken);
        return stale.Count == 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded(
            $"El certificado de la tienda no se pudo leer y se creó uno nuevo. Vuelva a unir estas cajas (Configuración → Equipos: revocar y generar código): {string.Join(", ", stale)}.");
    }
}

internal sealed class UserDirectory(IdentityDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyList<UserName>> AllAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(u => u.Name).Select(u => new UserName(u.Id, u.Name)).ToListAsync(ct);
}

internal sealed class DevicePeripherals(IdentityDbContext db) : IDevicePeripherals
{
    public async Task<PeripheralsSummary> SummaryAsync(CancellationToken ct)
    {
        var reported = db.Devices.Where(d => !d.IsRevoked && d.PeripheralsReportedUtc != null);
        return new PeripheralsSummary(await reported.CountAsync(ct), await reported.CountAsync(d => d.ScaleProtocol != null, ct),
            await reported.CountAsync(d => d.PrinterWidth != null, ct));
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
            select new { SessionId = s.Id, UserId = u.Id, u.Name, DeviceId = s.DeviceId, Role = r })
            .AsNoTracking().FirstOrDefaultAsync(Context.RequestAborted);
        if (data is null) return AuthenticateResult.Fail("Invalid or expired session.");
        var permissions = data.Role.Effective;

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
