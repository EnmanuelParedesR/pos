using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Security;

/// <summary>A command that the caller may run with its own permission or with a one-time supervisor authorization.</summary>
public interface IRequiereAutorizacion
{
    string Permiso { get; }
    Guid? AutorizacionSupervisorId { get; }
    Guid RecursoId { get; }
}

/// <summary>Implemented by the Identidad module: consumes a one-time supervisor authorization.</summary>
public interface IAutorizacionesSupervisor
{
    /// <returns>The supervisor's user id, or an error if the authorization is missing, used, expired or for another action.</returns>
    Task<Result<Guid>> ConsumirAsync(Guid autorizacionId, string permiso, Guid recursoId, CancellationToken ct);
}

public static class AutorizacionErrors
{
    public static Error Requerida(string permiso) =>
        Error.Forbidden("Autorizacion.Requerida", $"Esta acción necesita la autorización de un supervisor ({permiso}).");
}
