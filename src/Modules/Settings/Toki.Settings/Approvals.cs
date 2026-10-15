using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.Settings;

/// <summary>TK-210: which actions this store asks a supervisor for when the user's role lacks them. One-person shops turn them off.</summary>
internal sealed class ApprovalPolicy(SettingsDbContext db) : IApprovalPolicy
{
    /// <summary>The actions the owner may turn off, in screen order, and whether a store asks for each until the owner saves the policy.
    /// Any other approval (opening a shift, cash in, purchasing, credit over the limit) always asks. register.approve-close is "close a shift with a difference" (TK-212).</summary>
    public static readonly (string Permission, bool Default)[] Defaults =
    [
        (Permissions.SalesRemoveLine, false), (Permissions.SalesVoid, true), (Permissions.SalesReturn, true),
        (Permissions.SalesLargeDiscount, true), (Permissions.SalesPriceOverride, true), (Permissions.RegisterWithdraw, true),
        (Permissions.RegisterApproveClose, true), (Permissions.InventoryAdjust, true), (Permissions.CreditManage, true),
    ];

    public static HashSet<string> Required(string? saved) =>
        saved is null ? [.. Defaults.Where(d => d.Default).Select(d => d.Permission)] : [.. saved.Split(',', StringSplitOptions.RemoveEmptyEntries)];

    public async Task<bool> RequiresSupervisorAsync(string permission, CancellationToken ct) =>
        !Defaults.Any(d => d.Permission == permission)
        || Required(await db.Business.AsNoTracking().Select(b => b.SupervisorApprovals).SingleAsync(ct)).Contains(permission);
}

internal static class GetApprovalPolicy
{
    public sealed record Rule(string Permission, bool Required);

    internal sealed class Endpoint : IEndpoint
    {
        // Every user: the register reads it to know whether removing a line asks for a supervisor.
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/settings/approvals", async (SettingsDbContext db, CancellationToken ct) =>
                {
                    var required = ApprovalPolicy.Required(await db.Business.AsNoTracking().Select(b => b.SupervisorApprovals).SingleAsync(ct));
                    return Results.Ok(ApprovalPolicy.Defaults.Select(d => new Rule(d.Permission, required.Contains(d.Permission))).ToList());
                })
                .RequireAuthorization().WithTags("Settings");
    }
}

internal static class UpdateApprovalPolicy
{
    /// <param name="Required">The actions that ask for a supervisor; the others in the list the user does alone.</param>
    public sealed record Command(IReadOnlyList<string> Required) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() =>
            RuleForEach(c => c.Required).Must(p => ApprovalPolicy.Defaults.Any(d => d.Permission == p)).WithMessage("Esa acción no pide supervisor: {PropertyValue}.");
    }

    internal sealed class Handler(SettingsDbContext db, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var b = await db.Business.SingleAsync(ct);
            b.SupervisorApprovals = string.Join(',', ApprovalPolicy.Defaults.Select(d => d.Permission).Where(c.Required.Contains));
            await audit.RecordAsync("settings.approvals", "Business", Guid.Empty, new { Required = b.SupervisorApprovals }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/settings/approvals", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
    }
}
