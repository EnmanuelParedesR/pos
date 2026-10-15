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
/// The result is a one-time approval valid 2 minutes, bound to the action, the resource and this device.</summary>
internal static class GrantSupervisorApproval
{
    public sealed record Command(string Code, string Pin, string Permission, Guid ResourceId) : ICommand<Response>;

    /// <summary>A wrong PIN is a successful command so the failed attempt is saved; <see cref="Failure"/> carries the error.</summary>
    public sealed record Response(Guid? ApprovalId, string? Supervisor, DateTime? ExpiresUtc, Error? Failure);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Code).NotEmpty().MaximumLength(20);
            RuleFor(c => c.Pin).NotEmpty().MaximumLength(8);
            RuleFor(c => c.Permission).Must(p => Permissions.All.Contains(p)).WithMessage("Permiso desconocido.");
        }
    }

    internal sealed class Handler(IdentityDbContext db, IUserContext me, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (me.DeviceId is not { } deviceId) return IdentityErrors.DeviceNotPaired;
            var supervisor = await db.Users.FirstOrDefaultAsync(u => u.Code == User.NormalizeCode(c.Code), ct);
            if (supervisor is null) return IdentityErrors.InvalidCredentials;

            var now = clock.GetUtcNow().UtcDateTime;
            var pin = supervisor.TryPin(c.Pin, now);
            if (pin != PinResult.Correct)
            {
                await audit.RecordAsync("identity.pin-failed", "User", supervisor.Id, new { supervisor.Code, c.Permission, Result = pin }, ct);
                return new Response(null, null, null, pin == PinResult.Locked ? IdentityErrors.UserLocked : IdentityErrors.InvalidCredentials);
            }

            var permissions = await db.Roles.Where(r => r.Id == supervisor.RoleId).Select(r => r.Permissions).SingleAsync(ct);
            if (!permissions.Contains(c.Permission)) return new Response(null, null, null, IdentityErrors.SupervisorLacksPermission);

            var approval = new SupervisorApproval
            {
                Id = Guid.CreateVersion7(), SupervisorId = supervisor.Id, RequesterId = me.UserId, DeviceId = deviceId,
                Permission = c.Permission, ResourceId = c.ResourceId, ExpiresUtc = now + SupervisorApproval.Duration,
            };
            db.Approvals.Add(approval);
            // The only server trace of actions that happen at the register alone (removing a line, opening the drawer).
            await audit.RecordAsync("approval.granted", "SupervisorApproval", approval.Id,
                new { c.Permission, c.ResourceId, Supervisor = supervisor.Id, Requester = me.UserId }, ct);
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
