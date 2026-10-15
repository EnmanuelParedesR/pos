using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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
        public string DeviceToken { get; init; } = "";
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

    internal sealed class Handler(IdentityDbContext db, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var deviceHash = Token.Hash(c.DeviceToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.TokenHash == deviceHash && !d.IsRevoked, ct);
            if (device is null) return IdentityErrors.DeviceNotPaired;

            var user = await db.Users.FirstOrDefaultAsync(u => u.Code == User.NormalizeCode(c.Code), ct);
            if (user is null) return IdentityErrors.InvalidCredentials;

            var now = clock.GetUtcNow().UtcDateTime;
            var result = user.TryPin(c.Pin, now);
            if (result != PinResult.Correct)
            {
                await audit.RecordAsync("identity.pin-failed", "User", user.Id, new { user.Code, Device = device.Id, Result = result }, ct);
                return new Response(null, null, null, null, result == PinResult.Locked ? IdentityErrors.UserLocked : IdentityErrors.InvalidCredentials);
            }

            var token = Token.New();
            var session = new Session
            {
                Id = Guid.CreateVersion7(), UserId = user.Id, DeviceId = device.Id,
                TokenHash = Token.Hash(token), CreatedUtc = now, ExpiresUtc = now + Session.Duration,
            };
            db.Sessions.Add(session);
            return new Response(token, user.Id, user.Name, session.ExpiresUtc, null);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/sessions", async (Command c, HttpRequest req, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                {
                    var result = await h.Handle(c with { DeviceToken = req.Headers[IdentityModule.DeviceHeader].ToString() }, ct);
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

/// <summary>A user changes their own PIN.</summary>
internal static class ChangeMyPin
{
    public sealed record Command(string CurrentPin, string NewPin) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.CurrentPin).NotEmpty().MaximumLength(8);
            RuleFor(c => c.NewPin).Must(Domain.Pin.IsValid).WithMessage("El PIN debe tener de 4 a 8 dígitos.");
        }
    }

    internal sealed class Handler(IdentityDbContext db, IUserContext me, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var user = await db.Users.FirstAsync(u => u.Id == me.UserId, ct);
            if (!Domain.Pin.Verify(user.PinHash, c.CurrentPin)) return IdentityErrors.InvalidCredentials;
            user.ChangePin(c.NewPin);
            // A new PIN ends every other session of this user (someone else may have been using it).
            var now = clock.GetUtcNow().UtcDateTime;
            await db.Sessions.Where(s => s.UserId == user.Id && s.ClosedUtc == null && s.Id != me.SessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedUtc, now), ct);
            await audit.RecordAsync("identity.change-pin", "User", user.Id, new { }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/me/pin", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequireAuthorization().WithTags("Identity");
    }
}
