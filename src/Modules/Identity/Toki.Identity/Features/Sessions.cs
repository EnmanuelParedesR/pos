using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Identity.Domain;
using Toki.Identity.Persistence;
using Toki.SharedKernel;

namespace Toki.Identity.Features;

/// <summary>Cashier logs in on a paired device with code + PIN.</summary>
internal static class OpenSession
{
    public sealed record Command(string Code, string Pin) : ICommand<Response>
    {
        /// <summary>The paired device it comes from (client certificate or secret), resolved by the endpoint.</summary>
        public Guid? DeviceId { get; init; }
    }

    /// <summary>A wrong PIN is a successful command (the failed attempt must be saved); <see cref="Failure"/> carries the error.</summary>
    public sealed record Response(string? Token, Guid? UserId, string? Name, DateTime? ExpiresUtc, Error? Failure);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Code).NotEmpty().MaximumLength(20);
            RuleFor(c => c.Pin).NotEmpty().MaximumLength(8);
        }
    }

    internal sealed class Handler(IdentityDbContext db, IAuditLog audit, IFeatureService license, PinPepper pepper, AttemptLimiter limiter, TimeProvider clock)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (c.DeviceId is not { } deviceId) return IdentityErrors.DeviceNotPaired;
            if (limiter.IsBlocked(AttemptLimiter.PinKey(deviceId), AttemptLimiter.PinsPerDevice)) return IdentityErrors.DeviceLocked;
            var now = clock.GetUtcNow().UtcDateTime;
            // R11 A14: a register that keeps typing wrong PINs for one person stops before that person's own count locks them out.
            var key = AttemptLimiter.UserPinKey(deviceId, User.NormalizeCode(c.Code));
            if (limiter.IsBlocked(key, AttemptLimiter.PinsPerUser)) return new Response(null, null, null, null, IdentityErrors.TooManyAttempts);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Code == User.NormalizeCode(c.Code), ct);
            if (user is null)
            {
                limiter.Fail(AttemptLimiter.PinKey(deviceId));
                return IdentityErrors.InvalidCredentials;
            }

            var result = user.TryPin(c.Pin, now, pepper.Value);
            if (result != PinResult.Correct)
            {
                limiter.Fail(AttemptLimiter.PinKey(deviceId));
                if (result == PinResult.Incorrect) limiter.Fail(key);
                await audit.RecordAsync("identity.pin-failed", "User", user.Id, new { user.Code, Device = deviceId, Result = result }, ct);
                return new Response(null, null, null, null, result == PinResult.Locked ? IdentityErrors.UserLocked : IdentityErrors.InvalidCredentials);
            }
            // TK-220: the main PC and the oldest registers the license allows sign in; the rest wait for a license (nothing on them is lost).
            var licensed = db.Devices.Where(d => !d.IsRevoked).OrderBy(d => d.CreatedUtc).ThenBy(d => d.Id).Take(license.LimitOf(Limit.Registers) + 1);
            if (!await licensed.AnyAsync(d => d.Id == deviceId, ct)) return IdentityErrors.RegisterNotLicensed;

            var token = Token.New();
            var session = new Session
            {
                Id = Guid.CreateVersion7(), UserId = user.Id, DeviceId = deviceId,
                TokenHash = Token.Hash(token), CreatedUtc = now, ExpiresUtc = now + Session.Duration,
            };
            db.Sessions.Add(session);
            return new Response(token, user.Id, user.Name, session.ExpiresUtc, null);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/sessions", async (Command c, HttpContext http, IdentityDbContext db, IConfiguration config, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                {
                    var result = await h.Handle(c with { DeviceId = await db.IdOfAsync(http, config, ct) }, ct);
                    if (result.IsFailure) return EndpointExtensions.Problem(result.Error!);
                    return result.Value.Failure is { } failure ? EndpointExtensions.Problem(failure) : Results.Ok(result.Value);
                })
                .AllowAnonymous().WithTags("Identity");
    }
}

internal static class CloseSession
{
    public sealed record Command(Guid SessionId) : ICommand<Ok>;

    internal sealed class Handler(IdentityDbContext db, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            await db.Sessions.Where(s => s.Id == c.SessionId && s.ClosedUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedUtc, now), ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapDelete("/api/identity/sessions/current", async (ClaimsPrincipal user, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(Guid.Parse(user.FindFirstValue(TokiClaims.Session)!)), ct)).ToHttp(_ => Results.NoContent()))
                .RequireAuthorization().WithTags("Identity");
    }
}

/// <summary>Who am I and what can I do. The UI shows or hides menus with this; the server still checks every call.</summary>
internal static class Me
{
    public sealed record Response(Guid UserId, string Name, Guid? DeviceId, IReadOnlyList<string> Permissions);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identity/me", (ClaimsPrincipal user, IUserContext u) =>
                    Results.Ok(new Response(u.UserId, u.Name, u.DeviceId, user.FindAll(TokiClaims.Permission).Select(c => c.Value).ToList())))
                .RequireAuthorization().WithTags("Identity");
    }
}
