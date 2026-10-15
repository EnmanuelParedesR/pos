using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Security;

/// <summary>A command that the caller may run with its own permission or with a one-time supervisor approval.
/// Its endpoint must not require <see cref="Permission"/> itself (use RequireAuthorization or a lower permission), or the supervisor path is unreachable.</summary>
public interface IRequiresApproval
{
    string Permission { get; }
    Guid? SupervisorApprovalId { get; }
    Guid ResourceId { get; }
}

/// <summary>Implemented by the Identity module: consumes a one-time supervisor approval.</summary>
public interface ISupervisorApprovals
{
    /// <returns>The supervisor's user id, or an error if the approval is missing, used, expired or for another action.</returns>
    Task<Result<Guid>> ConsumeAsync(Guid approvalId, string permission, Guid resourceId, CancellationToken ct);
}

public static class ApprovalErrors
{
    public static Error Required(string permission) =>
        Error.Forbidden("Approval.Required", $"Esta acción necesita la autorización de un supervisor ({permission}).");
}
