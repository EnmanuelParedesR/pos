using System.Collections.Concurrent;
using System.Security.Cryptography;
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

    /// <param name="max">Failures allowed in <paramref name="window"/> (default <c>Auth:FailuresPerIpAndAccount</c>, 5, in 15 minutes).</param>
    public bool IsBlocked(string key, int? max = null, TimeSpan? window = null) =>
        _failures.TryGetValue(key, out var f) && clock.GetUtcNow().UtcDateTime - f.Since < (window ?? Window) && f.Failures >= (max ?? MaxFailures);

    public void Fail(string key, TimeSpan? window = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var span = window ?? Window;
        if (_failures.Count > 10_000)
            foreach (var (k, v) in _failures)
                if (now - v.Since >= TimeSpan.FromDays(1)) _failures.TryRemove(k, out _);
        _failures.AddOrUpdate(key, (1, now), (_, f) => now - f.Since >= span ? (1, now) : (f.Failures + 1, f.Since));
    }

    public void Succeeded(string key) => _failures.TryRemove(key, out _);

    public static string Key(HttpContext? http, CloudUser user) => $"{http?.Connection.RemoteIpAddress}|{user.Id}";
}

/// <summary>R12 #4: Identity's authenticator check (RFC 6238, 30 s steps, two either side of now) that also refuses a code of a step already used
/// by this account, so a code seen over a shoulder or in a log cannot be used again inside its window. The last step is an Identity user token
/// (no migration); saving it uses the user's concurrency stamp, so two requests with the same code cannot both pass.</summary>
internal sealed class OneTimeAuthenticatorTokenProvider : AuthenticatorTokenProvider<CloudUser>
{
    internal const string Provider = "[Abastalia]";
    internal const string LastStep = "LastTotpStep";

    public override async Task<bool> ValidateAsync(string purpose, string token, UserManager<CloudUser> manager, CloudUser user)
    {
        if (await manager.GetAuthenticatorKeyAsync(user) is not { Length: > 0 } key || token.Length != 6 || !token.All(char.IsAsciiDigit)) return false;
        var secret = FromBase32(key);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30; // the real clock, as the phone's
        _ = long.TryParse(await manager.GetAuthenticationTokenAsync(user, Provider, LastStep), out var last);
        for (var step = now - 2; step <= now + 2; step++)
        {
            if (step <= last || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(token))) continue;
            return (await manager.SetAuthenticationTokenAsync(user, Provider, LastStep, step.ToString(System.Globalization.CultureInfo.InvariantCulture))).Succeeded;
        }
        return false;
    }

    internal static string Code(byte[] secret, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
#pragma warning disable CA5350 // RFC 6238 is HMAC-SHA1, as every authenticator app
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("000000", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] FromBase32(string key)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(key.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in key.ToUpperInvariant())
        {
            var v = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0) continue; // spaces and padding
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
            buffer &= (1 << bits) - 1;
        }
        return [.. bytes];
    }
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
/// <param name="AlreadyConfirmed">TK-297: the link was used before (a second tab, the mail app's preview, a link scanner): the address is
/// confirmed, nothing changed now.</param>
/// <param name="Email">TK-297: the confirmed address, only when this request's code was valid; the panel fills it in on Entrar.</param>
public sealed record ConfirmEmailResponse(bool FirstConfirmation, bool AlreadyConfirmed = false, string? Email = null);

internal static class AccountSecurityEndpoints
{
    /// <summary>Per signed-in user: <c>/api/auth/manage/*</c> (<c>Auth:ManagePerMinutePerUser</c>, 10). R11 B-Med2: info sends e-mails; 2fa takes codes.</summary>
    public const string ManageRateLimit = "auth-manage";

    public static void MapAccountSecurity(this IEndpointRouteBuilder app) =>
        // R11 B-Low1: Identity's GET confirmEmail takes the code in the query string, which request telemetry keeps. Same checks, from the body.
        app.MapPost("/api/auth/confirm", async (ConfirmEmailRequest r, UserManager<CloudUser> users, CloudDbContext db) =>
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
            if (result.Succeeded) return Results.Ok(new ConfirmEmailResponse(first, Email: user.Email));
            // TK-297: the first confirmation rotates the security stamp, so the same link opened again, or twice at once (one request then loses
            // Identity's concurrency check), failed although the address was confirmed. Read the database again, not the tracked user: the other
            // request may have just saved it. It tells only "confirmed", to whoever holds the account id from the link.
            var target = string.IsNullOrEmpty(r.ChangedEmail) ? null : users.NormalizeEmail(r.ChangedEmail);
            var confirmed = await db.Users.AsNoTracking().AnyAsync(u => u.Id == user.Id && u.EmailConfirmed && (target == null || u.NormalizedEmail == target));
            return confirmed ? Results.Ok(new ConfirmEmailResponse(false, AlreadyConfirmed: true)) : Results.Unauthorized();
        }).AllowAnonymous().RequireRateLimiting(Quotas.QuotaRateLimits.Auth);

    /// <summary>TK-298: with 2FA on, Identity's <c>/manage/2fa</c> turns it off, changes the key or makes new backup codes with the session alone.
    /// Those three now need the app's current code or one backup code (used up), so a stolen session cannot remove the second step. The same
    /// 400 as Identity's own wrong code. Abastalia staff reset a lost phone with <c>reset-2fa</c> (<see cref="Platform.PlatformCli"/>).</summary>
    public static async ValueTask<object?> CodeToWeakenTwoFactor(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<Microsoft.AspNetCore.Identity.Data.TwoFactorRequest>().SingleOrDefault();
        if (request is null || request is not { Enable: false } and not { ResetSharedKey: true } and not { ResetRecoveryCodes: true }) return await next(context);
        var services = context.HttpContext.RequestServices;
        var users = services.GetRequiredService<UserManager<CloudUser>>();
        if (await users.GetUserAsync(context.HttpContext.User) is not { TwoFactorEnabled: true } user) return await next(context);
        // R12 #2: wrong codes here count as at sign-in: 5 in 15 minutes per account, a daily cap (Auth:TwoFactorFailuresPerDay, 20), and
        // Identity's own lockout, so a stolen session cannot walk the 6-digit space.
        var throttle = services.GetRequiredService<LoginThrottle>();
        var (key, dayKey) = ($"2fa-manage|{user.Id}", $"2fa-manage-day|{user.Id}");
        var perDay = services.GetRequiredService<IConfiguration>().GetValue("Auth:TwoFactorFailuresPerDay", 20);
        if (throttle.IsBlocked(key) || throttle.IsBlocked(dayKey, perDay, TimeSpan.FromDays(1)) || await users.IsLockedOutAsync(user))
            return TypedResults.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Auth.TooManyCodes",
                detail: "Demasiados códigos equivocados. Espere un rato e intente de nuevo.", extensions: new Dictionary<string, object?> { ["code"] = "Auth.TooManyCodes" });
        var code = string.Concat((request.TwoFactorCode ?? "").Where(c => !char.IsWhiteSpace(c)));
        var valid = code.Length == 6 && code.All(char.IsAsciiDigit)
            ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code)
            : code.Length is > 0 and <= 20 && (await users.RedeemTwoFactorRecoveryCodeAsync(user, code.ToUpperInvariant())).Succeeded;
        if (!valid)
        {
            throttle.Fail(key);
            throttle.Fail(dayKey, TimeSpan.FromDays(1));
            await users.AccessFailedAsync(user);
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["InvalidTwoFactorCode"] = ["The current 2fa code or a recovery code is required."] });
        }
        throttle.Succeeded(key);
        return await next(context);
    }

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
