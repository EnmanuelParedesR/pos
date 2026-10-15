using Toki.Client.UI.Api;

namespace Toki.Client.UI.State;

/// <summary>A screen asks for a supervisor and waits; the dialog in the layout collects code + PIN and answers with the approval id.</summary>
public sealed class Approver
{
    private TaskCompletionSource<Guid?>? _pending;

    internal ApprovalRequest? Current { get; private set; }
    internal event Action? Changed;

    /// <summary>Null when the cashier cancels. The id is single use, valid 2 minutes, for this permission, resource and device.</summary>
    public Task<Guid?> RequestAsync(string permission, Guid resourceId, string reason)
    {
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Current = new(permission, resourceId, reason);
        Changed?.Invoke();
        return _pending.Task;
    }

    internal void Respond(Guid? approvalId)
    {
        Current = null;
        Changed?.Invoke();
        _pending?.TrySetResult(approvalId);
    }

    /// <summary>Runs a command; when the API answers that a supervisor must allow it, asks for one and runs it again with the id.</summary>
    public async Task<ApiResult<T>> WithSupervisorAsync<T>(Func<Guid?, Task<ApiResult<T>>> command, Guid resourceId, string reason)
    {
        var r = await command(null);
        if (r.Problem?.PermissionRequired is not { } permission) return r;
        var id = await RequestAsync(permission, resourceId, reason);
        return id is null ? new(default, new Problem(0, "Cancelled", "Se canceló la autorización.")) : await command(id);
    }

    public static string Explain(string permission) => permission switch
    {
        "sales.void" => "Anular una venta",
        "sales.return" => "Devolver mercancía",
        "sales.large-discount" => "Descuento mayor que el tope",
        "sales.price-override" => "Cambiar el precio",
        "credit.exceed-limit" => "Fiar por encima del límite",
        "register.withdraw" => "Sacar efectivo de la caja",
        "register.approve-close" => "Cerrar la caja",
        "register.open-drawer" => "Abrir la gaveta",
        _ => permission,
    };
}

internal sealed record ApprovalRequest(string Permission, Guid ResourceId, string Reason);
