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
using Abastalia.BuildingBlocks;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Identity.Contracts;
using Abastalia.Identity.Domain;
using Abastalia.Identity.Persistence;
using Abastalia.SharedKernel;
using Abastalia.Sync.Contracts;

namespace Abastalia.Identity;

public static class IdentityModule
{
    public const string Scheme = "Abastalia";
    /// <summary>Header carrying the device secret over plain http (development, tests).</summary>
    public const string DeviceHeader = "X-Device";

    /// <summary>Whether a device may prove itself with its secret (<see cref="DeviceHeader"/>): only over plain http (development, tests);
    /// over https a client certificate is the only way (ADR-0009).</summary>
    public static bool AcceptsDeviceSecrets(IConfiguration config) => !config.GetValue<bool>("Abastalia:HttpsWithNodeCertificate");

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<IdentityDbContext>();
        services.AddSingleton<StoreCertificateAuthority>();
        services.AddHealthChecks().AddCheck<StoreCaHealthCheck>("store-ca").AddCheck<PinFailuresHealthCheck>("pin-failures")
            .AddCheck<SharedPinHealthCheck>("shared-pin");
        services.AddSingleton<PinPepper>();
        services.AddSingleton<AttemptLimiter>();
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
            devices = devices.Where(d => d.CertificateThumbprint == thumbprint || d.PreviousCertificateThumbprint == thumbprint);
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
            select new { SessionId = s.Id, UserId = u.Id, u.Name, DeviceId = s.DeviceId, Role = r, Seen = s.LastSeenUtc ?? s.CreatedUtc })
            .AsNoTracking().FirstOrDefaultAsync(Context.RequestAborted);
        if (data is null) return AuthenticateResult.Fail("Invalid or expired session.");
        var permissions = data.Role.Effective;

        // R11 A4: whoever can authorize (or manage users) is signed out after a few idle minutes, so nobody approves from a register left open.
        // The register's own polling (AbastaliaApi.PollAsync) does not count as activity.
        if (permissions.Any(IdleLock.Applies) && data.Seen + IdleLock.Timeout(config) <= now) return AuthenticateResult.Fail("Session idle.");
        // TK-277: written for everyone (at most once a minute): the same person working at two registers at once is the shared-PIN signal.
        if (Request.Headers[IdleLock.BackgroundHeader].Count == 0 && data.Seen + IdleLock.WriteEvery <= now)
            await db.Sessions.Where(s => s.Id == data.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenUtc, now), Context.RequestAborted);

        List<Claim> claims =
        [
            new(AbastaliaClaims.User, data.UserId.ToString()),
            new(AbastaliaClaims.Name, data.Name),
            new(AbastaliaClaims.Device, data.DeviceId.ToString()),
            new(AbastaliaClaims.Session, data.SessionId.ToString()),
            .. permissions.Select(p => new Claim(AbastaliaClaims.Permission, p)),
        ];
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}

/// <summary>R11 A4: sessions of users who can approve close after <c>Identity:IdleMinutes</c> (5) without a request of their own.</summary>
internal static class IdleLock
{
    /// <summary>Sent by the register's periodic checks (status bar): they keep the screen fresh but are not the person working.</summary>
    public const string BackgroundHeader = "X-Abastalia-Background";
    public static readonly TimeSpan WriteEvery = TimeSpan.FromMinutes(1);

    private static readonly HashSet<string> Approving =
        [Permissions.RegisterApproveClose, Permissions.SalesVoid, Permissions.SalesReturn, Permissions.RegisterWithdraw, Permissions.UsersManage];

    public static bool Applies(string permission) => Approving.Contains(permission);
    public static TimeSpan Timeout(IConfiguration config) => TimeSpan.FromMinutes(config.GetValue("Identity:IdleMinutes", 5));
}

/// <summary>R11 A3: the server's PIN pepper (a <see cref="ServiceKeys"/> secret, never in the database or a backup).</summary>
internal sealed class PinPepper(ServiceKeys keys)
{
    public byte[] Value => keys.GetOrCreate("pin-pepper");
}

/// <summary>R11 A4: degraded while someone is locked out or collected several wrong PINs in the last hour, so the owner sees it in Salud
/// (and the cloud with the health summary) instead of finding it in the audit log.</summary>
internal sealed class PinFailuresHealthCheck(IdentityDbContext db, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now - User.FailureMemory;
        var names = await db.Users.Where(u => u.LockedUntilUtc > now || (u.FailedAttempts >= 3 && u.LastFailedUtc > since))
            .OrderBy(u => u.Name).Select(u => u.Name).ToListAsync(cancellationToken);
        return names.Count == 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded(
            $"Varios PIN incorrectos en la última hora para: {string.Join(", ", names)}. Revise quién lo intentó en Auditoría.");
    }
}

/// <summary>TK-277: a user who worked at one register after signing in at another (both sessions in use at once): a PIN that may be shared.
/// A cashier cannot hold two open shifts, so the sessions are the signal. Only a warning; nothing is blocked. The Administrador is left out.</summary>
internal static class SharedPins
{
    public sealed record Item(Guid UserId, string Name, int Times, DateTime LastUtc);

    public static async Task<List<Item>> SinceAsync(IdentityDbContext db, DateTime since, CancellationToken ct)
    {
        var rows = await (from b in db.Sessions
                          where b.CreatedUtc >= since
                          join u in db.Users on b.UserId equals u.Id
                          where u.RoleId != Role.AdministratorId
                          where db.Sessions.Any(a => a.UserId == b.UserId && a.DeviceId != b.DeviceId && a.CreatedUtc <= b.CreatedUtc && a.LastSeenUtc > b.CreatedUtc)
                          select new { u.Id, u.Name, b.CreatedUtc }).AsNoTracking().ToListAsync(ct);
        return [.. rows.GroupBy(r => (r.Id, r.Name)).Select(g => new Item(g.Key.Id, g.Key.Name, g.Count(), g.Max(r => r.CreatedUtc))).OrderByDescending(i => i.LastUtc)];
    }
}

/// <summary>TK-277: degraded while someone used one PIN at two registers at once in the last day (the cloud sees it with the health summary).</summary>
internal sealed class SharedPinHealthCheck(IdentityDbContext db, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var names = (await SharedPins.SinceAsync(db, clock.GetUtcNow().UtcDateTime.AddDays(-1), cancellationToken)).Select(i => i.Name).ToList();
        return names.Count == 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded(
            $"El mismo usuario trabajó en dos cajas a la vez: {string.Join(", ", names)}. Puede que compartan el PIN; cada persona debe tener el suyo.");
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
    public async Task<Result<Guid>> ConsumeAsync(Guid approvalId, string permission, Guid resourceId, string summary, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = SupervisorApproval.HashOf(summary);
        // R11 A1: also the cashier who asked and the content the supervisor saw (amount, lines): RD$ 100 approved is not RD$ 20,000.
        var valid = db.Approvals.Where(a => a.Id == approvalId && a.UsedUtc == null && a.ExpiresUtc > now
            && a.Permission == permission && a.ResourceId == resourceId && a.DeviceId == user.DeviceId
            && a.RequesterId == user.UserId && a.PayloadHash == hash);
        // Conditional update: only one command can consume it, even when two race.
        if (await valid.ExecuteUpdateAsync(s => s.SetProperty(a => a.UsedUtc, now), ct) == 0)
            return IdentityErrors.InvalidApproval;
        return await db.Approvals.Where(a => a.Id == approvalId).Select(a => a.SupervisorId).SingleAsync(ct);
    }
}
