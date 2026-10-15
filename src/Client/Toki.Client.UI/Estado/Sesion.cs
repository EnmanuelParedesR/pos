using Toki.Client.UI.Api;

namespace Toki.Client.UI.Estado;

/// <summary>Where each host keeps the device token between runs (WPF: encrypted per Windows user; the dev web host: a local file).</summary>
public interface IAlmacenLocal
{
    Task<string?> LeerAsync(string clave);
    Task GuardarAsync(string clave, string? valor);
}

/// <summary>Who is at this PC: the paired device, the logged-in user and their open shift.</summary>
public sealed class Sesion(IAlmacenLocal almacen)
{
    private const string ClaveDispositivo = "dispositivo";
    private HashSet<string> _permisos = [];
    private bool _cargada;

    public string? TokenDispositivo { get; private set; }
    public string? Token { get; private set; }
    public Guid UsuarioId { get; private set; }
    public string? Nombre { get; private set; }
    public TurnoActual? Turno { get; set; }
    /// <summary>The session ended on the server (12 h, PIN change, revoked device).</summary>
    public bool Expirada { get; private set; }

    public event Action? Cambio;

    public bool Emparejado => TokenDispositivo is not null;
    public bool Abierta => Token is not null;
    public bool Puede(string permiso) => _permisos.Contains(permiso);
    /// <summary>Owners and supervisors land on the back office; cashiers go straight to the register.</summary>
    public bool EsAdministracion => Puede("reportes.ver");

    public async Task CargarAsync()
    {
        if (_cargada) return;
        TokenDispositivo = await almacen.LeerAsync(ClaveDispositivo);
        _cargada = true;
    }

    public async Task EmparejarAsync(string token)
    {
        TokenDispositivo = token;
        await almacen.GuardarAsync(ClaveDispositivo, token);
        Cambio?.Invoke();
    }

    public async Task OlvidarDispositivoAsync()
    {
        TokenDispositivo = null;
        Cerrar();
        await almacen.GuardarAsync(ClaveDispositivo, null);
    }

    public void Abrir(SesionAbierta s, Yo yo)
    {
        (Token, UsuarioId, Nombre, _permisos, Expirada) = (s.Token, s.UsuarioId, s.Nombre, [.. yo.Permisos], false);
        Cambio?.Invoke();
    }

    public void Cerrar()
    {
        (Token, UsuarioId, Nombre, _permisos, Turno) = (null, Guid.Empty, null, [], null);
        Cambio?.Invoke();
    }

    internal void Expirar()
    {
        Cerrar();
        Expirada = true;
        Cambio?.Invoke();
    }
}
