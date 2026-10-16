using System.Globalization;
using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Security;

/// <summary>A command that the caller may run with its own permission or with a one-time supervisor approval.
/// Its endpoint must not require <see cref="Permission"/> itself (use RequireAuthorization or a lower permission), or the supervisor path is unreachable.</summary>
public interface IRequiresApproval
{
    string Permission { get; }
    Guid? SupervisorApprovalId { get; }
    Guid ResourceId { get; }

    /// <summary>R11 A1: what exactly is approved (amount, kind, lines), in words for the supervisor. The approval is bound to it: the same
    /// approval with another amount or other lines is refused. Empty when the resource alone says it all (the void of one sale).</summary>
    string ApprovalSummary => "";
}

/// <summary>Implemented by the Identity module: consumes a one-time supervisor approval.</summary>
public interface ISupervisorApprovals
{
    /// <param name="summary">What the command does (<see cref="IRequiresApproval.ApprovalSummary"/>): it must be what the supervisor approved.</param>
    /// <returns>The supervisor's user id, or an error if the approval is missing, used, expired, or for another action, content or requester.</returns>
    Task<Result<Guid>> ConsumeAsync(Guid approvalId, string permission, Guid resourceId, string summary, CancellationToken ct);
}

/// <summary>Implemented by the Settings module: whether this store asks a supervisor for an action the user's role lacks (TK-210).
/// A one-person shop can turn the supervisor off per action; an action outside the store's list always asks.</summary>
public interface IApprovalPolicy
{
    Task<bool> RequiresSupervisorAsync(string permission, CancellationToken ct);
}

public static class ApprovalErrors
{
    /// <summary>The permission also travels as <c>errors.permission</c>: the register opens the supervisor dialog from it, never from the text.
    /// R11 A1: <c>errors.summary</c> is what the supervisor approves, written by the server; the dialog shows it and the approval is bound to it.</summary>
    public static Error Required(string permission, string summary = "") =>
        Error.Forbidden("Approval.Required", $"Esta acción necesita la autorización de un supervisor ({permission}).") with
        {
            Details = new Dictionary<string, string[]> { ["permission"] = [permission], ["summary"] = [summary] },
        };

    /// <summary>Money as the supervisor reads it in a summary ("RD$ 1,250.00"), the same on every PC.</summary>
    public static string Money(decimal amount) => "RD$ " + amount.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>A quantity without trailing zeros ("1.5", "3").</summary>
    public static string Quantity(decimal quantity) => quantity.ToString("0.###", CultureInfo.InvariantCulture);
}
