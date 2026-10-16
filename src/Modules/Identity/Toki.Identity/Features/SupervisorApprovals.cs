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

/// <summary>A supervisor types code + PIN at the register to allow one action (void, big discount, cash out…).
/// The result is a one-time approval valid 2 minutes, bound to the action, the resource, this device, the cashier who asked and (R11 A1)
/// the summary the server wrote for it (amount, kind, lines): the same approval with another amount is refused.</summary>
internal static class GrantSupervisorApproval
{
    /// <param name="Summary">What is approved, exactly as the server wrote it in <c>Approval.Required</c> (<c>errors.summary</c>); the dialog shows it.</param>
    public sealed record Command(string Code, string Pin, string Permission, Guid ResourceId, string? Summary = null) : ICommand<Response>;

    /// <summary>A wrong PIN is a successful command so the failed attempt is saved; <see cref="Failure"/> carries the error.</summary>
    public sealed record Response(Guid? ApprovalId, string? Supervisor, DateTime? ExpiresUtc, Error? Failure);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Code).NotEmpty().MaximumLength(20);
            RuleFor(c => c.Pin).NotEmpty().MaximumLength(8);
            RuleFor(c => c.Permission).Must(p => Permissions.All.Contains(p)).WithMessage("Permiso desconocido.");
            RuleFor(c => c.Summary).MaximumLength(2000);
        }
    }

    internal sealed class Handler(IdentityDbContext db, IUserContext me, IAuditLog audit, PinPepper pepper, AttemptLimiter limiter, TimeProvider clock)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (me.DeviceId is not { } deviceId) return IdentityErrors.DeviceNotPaired;
            if (limiter.IsBlocked(AttemptLimiter.PinKey(deviceId), AttemptLimiter.PinsPerDevice)) return IdentityErrors.DeviceLocked;
            var now = clock.GetUtcNow().UtcDateTime;
            // R11 A14: a cashier trying PINs runs out here, before the supervisor's own count: nobody locks the owner out from a register.
            var key = AttemptLimiter.UserPinKey(deviceId, User.NormalizeCode(c.Code), me.UserId);
            if (limiter.IsBlocked(key, AttemptLimiter.PinsPerUser)) return new Response(null, null, null, IdentityErrors.TooManyAttempts);

            var supervisor = await db.Users.FirstOrDefaultAsync(u => u.Code == User.NormalizeCode(c.Code), ct);
            if (supervisor is null)
            {
                limiter.Fail(AttemptLimiter.PinKey(deviceId));
                return new Response(null, null, null, IdentityErrors.CannotApprove);
            }
            // R11 A14: whether the person may approve is checked before the PIN, with the same answer as a wrong PIN, so it never tells a right PIN apart.
            var permissions = (await db.Roles.AsNoTracking().SingleAsync(r => r.Id == supervisor.RoleId, ct)).Effective;
            var pin = permissions.Contains(c.Permission) ? supervisor.TryPin(c.Pin, now, pepper.Value) : PinResult.Incorrect;
            if (pin != PinResult.Correct)
            {
                limiter.Fail(AttemptLimiter.PinKey(deviceId));
                limiter.Fail(key);
                await audit.RecordAsync("identity.pin-failed", "User", supervisor.Id,
                    new { supervisor.Code, c.Permission, Result = pin, Requester = me.UserId, Device = deviceId }, ct);
                return new Response(null, null, null, pin == PinResult.Locked ? IdentityErrors.UserLocked : IdentityErrors.CannotApprove);
            }

            var summary = c.Summary ?? "";
            var approval = new SupervisorApproval
            {
                Id = Guid.CreateVersion7(), SupervisorId = supervisor.Id, RequesterId = me.UserId, DeviceId = deviceId,
                Permission = c.Permission, ResourceId = c.ResourceId, PayloadHash = SupervisorApproval.HashOf(summary), ExpiresUtc = now + SupervisorApproval.Duration,
            };
            db.Approvals.Add(approval);
            // The only server trace of actions that happen at the register alone (opening the drawer), and what the supervisor saw.
            await audit.RecordAsync("approval.granted", "SupervisorApproval", approval.Id,
                new { c.Permission, c.ResourceId, Supervisor = supervisor.Id, Requester = me.UserId, Summary = summary }, ct);
            return new Response(approval.Id, supervisor.Name, approval.ExpiresUtc, null);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/approvals", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                {
                    var result = await h.Handle(c, ct);
                    if (result.IsFailure) return EndpointExtensions.Problem(result.Error!);
                    return result.Value.Failure is { } failure ? EndpointExtensions.Problem(failure) : Results.Ok(result.Value);
                })
                .RequireAuthorization().WithTags("Identity");
    }
}
