using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Toki.BuildingBlocks.Security;

public static class TokiClaims
{
    public const string Usuario = "sub";
    public const string Nombre = "name";
    public const string Dispositivo = "dispositivo";
    public const string Sesion = "sesion";
    public const string Permiso = "permiso";
}

/// <summary>Who is calling: the cashier's session on a paired device.</summary>
public interface IUserContext
{
    Guid UsuarioId { get; }
    string Nombre { get; }
    Guid? DispositivoId { get; }
    Guid? SesionId { get; }
    bool Tiene(string permiso);

    /// <summary>Supervisor who authorized the current command, if any. Recorded in the audit trail.</summary>
    Guid? SupervisorId { get; set; }
}

internal sealed class HttpUserContext(IHttpContextAccessor accessor) : IUserContext
{
    private ClaimsPrincipal User => accessor.HttpContext?.User ?? new ClaimsPrincipal();

    public Guid UsuarioId => Guid.TryParse(User.FindFirstValue(TokiClaims.Usuario), out var id) ? id : Guid.Empty;
    public string Nombre => User.FindFirstValue(TokiClaims.Nombre) ?? "sistema";
    public Guid? DispositivoId => Guid.TryParse(User.FindFirstValue(TokiClaims.Dispositivo), out var id) ? id : null;
    public Guid? SesionId => Guid.TryParse(User.FindFirstValue(TokiClaims.Sesion), out var id) ? id : null;
    public bool Tiene(string permiso) => User.HasClaim(TokiClaims.Permiso, permiso);
    public Guid? SupervisorId { get; set; }
}
