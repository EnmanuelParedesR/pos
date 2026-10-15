using Toki.Client.UI.Api;

namespace Toki.Client.UI.Estado;

/// <summary>A screen asks for a supervisor and waits; the dialog in the layout collects code + PIN and answers with the authorization id.</summary>
public sealed class Autorizador
{
    private TaskCompletionSource<Guid?>? _espera;

    internal SolicitudAutorizacion? Actual { get; private set; }
    internal event Action? Cambio;

    /// <summary>Null when the cashier cancels. The id is single use, valid 2 minutes, for this permiso, resource and device.</summary>
    public Task<Guid?> PedirAsync(string permiso, Guid recursoId, string motivo)
    {
        _espera?.TrySetResult(null);
        _espera = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Actual = new(permiso, recursoId, motivo);
        Cambio?.Invoke();
        return _espera.Task;
    }

    internal void Responder(Guid? autorizacionId)
    {
        Actual = null;
        Cambio?.Invoke();
        _espera?.TrySetResult(autorizacionId);
    }

    /// <summary>Runs a command; when the API answers that a supervisor must allow it, asks for one and runs it again with the id.</summary>
    public async Task<Respuesta<T>> ConSupervisorAsync<T>(Func<Guid?, Task<Respuesta<T>>> comando, Guid recursoId, string motivo)
    {
        var r = await comando(null);
        if (r.Problema?.PermisoRequerido is not { } permiso) return r;
        var id = await PedirAsync(permiso, recursoId, motivo);
        return id is null ? new(default, new Problema(0, "Cancelado", "Se canceló la autorización.")) : await comando(id);
    }

    public static string Explicar(string permiso) => permiso switch
    {
        "ventas.anular" => "Anular una venta",
        "ventas.devolver" => "Devolver mercancía",
        "ventas.descuento-mayor" => "Descuento mayor que el tope",
        "ventas.cambiar-precio" => "Cambiar el precio",
        "fiado.exceder-limite" => "Fiar por encima del límite",
        "caja.retirar" => "Sacar efectivo de la caja",
        "caja.aprobar-cierre" => "Cerrar la caja",
        _ => permiso,
    };
}

internal sealed record SolicitudAutorizacion(string Permiso, Guid RecursoId, string Motivo);
