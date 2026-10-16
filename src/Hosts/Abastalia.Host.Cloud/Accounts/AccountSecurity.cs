using System.Collections.Concurrent;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Abastalia.Host.Cloud.Persistence;

namespace Abastalia.Host.Cloud.Accounts;

/// <summary>R11 B-High1: an account made by someone else with this e-mail (pre-hijack) never reaches its real owner as it was. Sign-in needs a
/// confirmed e-mail (Program.cs), and the first confirmation clears what the account gathered before it: other members and pending invites of
/// the business it created at sign-up, and every session (new security stamp). The panel then tells the owner to change the password if they
/// did not create the account.</summary>
internal sealed class CloudUserManager(IUserStore<CloudUser> store, IOptions<IdentityOptions> options, IPasswordHasher<CloudUser> hasher,
    IEnumerable<IUserValidator<CloudUser>> userValidators, IEnumerable<IPasswordValidator<CloudUser>> passwordValidators, ILookupNormalizer normalizer,
    IdentityErrorDescriber errors, IServiceProvider services, ILogger<UserManager<CloudUser>> logger, CloudDbContext db, TimeProvider clock)
    : UserManager<CloudUser>(store, options, hasher, userValidators, passwordValidators, normalizer, errors, services, logger)
{
    /// <summary>Both confirm paths (Identity's GET /api/auth/confirmEmail and <c>POST /api/auth/confirm</c>) come through here.</summary>
    public override async Task<IdentityResult> ConfirmEmailAsync(CloudUser user, string token)
    {
        var first = !user.EmailConfirmed;
        var result = await base.ConfirmEmailAsync(user, token);
        if (result.Succeeded && first) await ClearBeforeConfirmationAsync(user);
        return result;
    }

    /// <summary>Removes the other members and revokes the pending invites of the businesses this account created at sign-up (its Owner membership
    /// dates from the business's creation), then rotates the security stamp so no earlier token can be refreshed.</summary>
    public async Task ClearBeforeConfirmationAsync(CloudUser user)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var own = await db.TenantMembers.Where(m => m.UserId == user.Id && m.Role == MemberRole.Owner)
            .Join(db.Tenants, m => m.TenantId, t => t.Id, (m, t) => new { m.TenantId, m.JoinedUtc, t.CreatedUtc }).ToListAsync();
        var created = own.Where(x => x.JoinedUtc == x.CreatedUtc).Select(x => x.TenantId).ToList();
        var members = await db.TenantMembers.Where(m => created.Contains(m.TenantId) && m.UserId != user.Id).ExecuteDeleteAsync();
        var invites = await db.TenantInvites.Where(i => created.Contains(i.TenantId) && i.AcceptedUtc == null && i.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedUtc, now));
        if (members + invites > 0)
            Logger.LogWarning("First e-mail confirmation of user {UserId}: removed {Members} members and {Invites} invites added before it", user.Id, members, invites);
        await UpdateSecurityStampAsync(user);
    }
}

/// <summary>R11 B-Low6: wrong passwords (and 2FA codes) are counted per IP and account, so a third party cannot lock the owner out from elsewhere.
/// Identity's own per-account lockout stays only as a high backstop (<c>Lockout.MaxFailedAccessAttempts</c>, Program.cs).</summary>
internal sealed class LoginThrottle(TimeProvider clock, IConfiguration config)
{
    private readonly ConcurrentDictionary<string, (int Failures, DateTime Since)> _failures = new();
    private int MaxFailures => config.GetValue("Auth:FailuresPerIpAndAccount", 5);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public bool IsBlocked(string key) =>
        _failures.TryGetValue(key, out var f) && clock.GetUtcNow().UtcDateTime - f.Since < Window && f.Failures >= MaxFailures;

    public void Fail(string key)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (_failures.Count > 10_000)
            foreach (var (k, v) in _failures)
                if (now - v.Since >= Window) _failures.TryRemove(k, out _);
        _failures.AddOrUpdate(key, (1, now), (_, f) => now - f.Since >= Window ? (1, now) : (f.Failures + 1, f.Since));
    }

    public void Succeeded(string key) => _failures.TryRemove(key, out _);

    public static string Key(HttpContext? http, CloudUser user) => $"{http?.Connection.RemoteIpAddress}|{user.Id}";
}

/// <summary>R11 B-Med2: Identity's e-mails (confirm, reset, change of address) to one address, at most <c>Auth:EmailsPerRecipientPerHour</c> (3)
/// an hour, whoever asks and from wherever. Above it nothing is sent and the endpoint answers as always (no enumeration).</summary>
/// <remarks>shortcut: in memory, one App Service instance (as <see cref="Nodes.NodeNonces"/> was).</remarks>
internal sealed class IdentityEmailCap(IConfiguration config) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(to =>
        RateLimitPartition.GetFixedWindowLimiter(to, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("Auth:EmailsPerRecipientPerHour", 3), Window = TimeSpan.FromHours(1),
        }));

    public bool TryTake(string to)
    {
        using var lease = _limiter.AttemptAcquire(to.Trim().ToUpperInvariant());
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>The confirmation link's values (the panel reads them after "#" of /app/confirmar), in the body: never in a URL that telemetry keeps (R11 B-Low1).</summary>
public sealed record ConfirmEmailRequest(string? UserId, string? Code, string? ChangedEmail);

/// <param name="FirstConfirmation">The account had never confirmed its e-mail: the panel warns to change the password if they did not create it.</param>
public sealed record ConfirmEmailResponse(bool FirstConfirmation);

internal static class AccountSecurityEndpoints
{
    /// <summary>Per signed-in user: <c>/api/auth/manage/*</c> (<c>Auth:ManagePerMinutePerUser</c>, 10). R11 B-Med2: info sends e-mails; 2fa takes codes.</summary>
    public const string ManageRateLimit = "auth-manage";

    public static void MapAccountSecurity(this IEndpointRouteBuilder app) =>
        // R11 B-Low1: Identity's GET confirmEmail takes the code in the query string, which request telemetry keeps. Same checks, from the body.
        app.MapPost("/api/auth/confirm", async (ConfirmEmailRequest r, UserManager<CloudUser> users) =>
        {
            if (r.UserId is not { Length: > 0 and <= 450 } || r.Code is not { Length: > 0 and <= 2000 } || await users.FindByIdAsync(r.UserId) is not { } user)
                return Results.Unauthorized();
            string code;
            try { code = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(r.Code)); }
            catch (FormatException) { return Results.Unauthorized(); }
            var first = !user.EmailConfirmed && string.IsNullOrEmpty(r.ChangedEmail);
            IdentityResult result;
            if (string.IsNullOrEmpty(r.ChangedEmail)) result = await users.ConfirmEmailAsync(user, code);
            else
            {
                // As Identity's endpoint: the e-mail is also the user name.
                result = await users.ChangeEmailAsync(user, r.ChangedEmail, code);
                if (result.Succeeded) result = await users.SetUserNameAsync(user, r.ChangedEmail);
            }
            return result.Succeeded ? Results.Ok(new ConfirmEmailResponse(first)) : Results.Unauthorized();
        }).AllowAnonymous().RequireRateLimiting(Quotas.QuotaRateLimits.Auth);

    /// <summary>R11 B-Low5: /register answers the same for an e-mail that already has an account (no enumeration); the person gets the confirmation
    /// e-mail only if the account is new, and signs in after confirming.</summary>
    public static async ValueTask<object?> SameAnswerForTakenEmail(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        return result is INestedHttpResult { Result: ValidationProblem problem }
               && problem.ProblemDetails.Errors.Count > 0
               && problem.ProblemDetails.Errors.Keys.All(k => k is "DuplicateUserName" or "DuplicateEmail")
            ? TypedResults.Ok()
            : result;
    }
}
