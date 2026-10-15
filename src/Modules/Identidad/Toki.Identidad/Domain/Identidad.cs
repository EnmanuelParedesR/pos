using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using P = Toki.BuildingBlocks.Security.Permisos;
using Toki.SharedKernel;

namespace Toki.Identidad.Domain;

internal sealed class Rol
{
    public static readonly Guid AdministradorId = new("0199e000-0000-7000-8000-000000000001");
    public static readonly Guid SupervisorId = new("0199e000-0000-7000-8000-000000000002");
    public static readonly Guid CajeroId = new("0199e000-0000-7000-8000-000000000003");

    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    public List<string> Permisos { get; set; } = [];

    public static IEnumerable<Rol> Predeterminados()
    {
        string[] soloAdmin = [P.UsuariosGestionar, P.DispositivosGestionar,
            P.ConfiguracionGestionar, P.FiscalGestionar];
        yield return new Rol { Id = AdministradorId, Nombre = "Administrador", Permisos = [.. P.Todos] };
        yield return new Rol { Id = SupervisorId, Nombre = "Supervisor", Permisos = [.. P.Todos.Except(soloAdmin)] };
        yield return new Rol
        {
            Id = CajeroId, Nombre = "Cajero",
            Permisos = [P.VentasRegistrar, P.CajaOperar, P.InventarioVer, P.ClientesGestionar],
        };
    }
}

internal enum ResultadoPin { Correcto, Incorrecto, Bloqueado, Inactivo }

internal sealed class Usuario
{
    public const int IntentosAntesDeBloquear = 5;

    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    /// <summary>Short code typed or picked at the register (for example "01"). Stored upper-case so both databases compare it the same way.</summary>
    public string Codigo { get; set; } = "";

    public static string NormalizarCodigo(string codigo) => codigo.Trim().ToUpperInvariant();
    public string PinHash { get; set; } = "";
    public Guid RolId { get; set; }
    public bool Activo { get; set; } = true;
    public int IntentosFallidos { get; set; }
    public DateTime? BloqueadoHastaUtc { get; set; }

    public void CambiarPin(string pin)
    {
        PinHash = Pin.Hash(pin);
        IntentosFallidos = 0;
        BloqueadoHastaUtc = null;
    }

    /// <summary>Checks the PIN with progressive lockout: 5 failures lock 5 minutes, each further failure doubles it (max 1 hour).</summary>
    public ResultadoPin IntentarPin(string pin, DateTime ahoraUtc)
    {
        if (!Activo) return ResultadoPin.Inactivo;
        if (BloqueadoHastaUtc > ahoraUtc) return ResultadoPin.Bloqueado;
        if (Pin.Verificar(PinHash, pin))
        {
            IntentosFallidos = 0;
            BloqueadoHastaUtc = null;
            return ResultadoPin.Correcto;
        }
        IntentosFallidos++;
        if (IntentosFallidos >= IntentosAntesDeBloquear)
        {
            var minutos = Math.Min(5 * Math.Pow(2, IntentosFallidos - IntentosAntesDeBloquear), 60);
            BloqueadoHastaUtc = ahoraUtc.AddMinutes(minutos);
        }
        return ResultadoPin.Incorrecto;
    }
}

internal sealed class Dispositivo
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    public byte[] TokenHash { get; set; } = [];
    public bool Revocado { get; set; }
    public DateTime CreadoUtc { get; set; }
}

internal sealed class CodigoEmparejamiento
{
    public Guid Id { get; set; }
    public string NombreDispositivo { get; set; } = "";
    public byte[] CodigoHash { get; set; } = [];
    public DateTime ExpiraUtc { get; set; }
    public DateTime? UsadoUtc { get; set; }
}

internal sealed class Sesion
{
    public static readonly TimeSpan Duracion = TimeSpan.FromHours(12);

    public Guid Id { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid DispositivoId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTime CreadaUtc { get; set; }
    public DateTime ExpiraUtc { get; set; }
    public DateTime? CerradaUtc { get; set; }
}

internal sealed class AutorizacionSupervisor
{
    public static readonly TimeSpan Duracion = TimeSpan.FromMinutes(2);

    public Guid Id { get; set; }
    public Guid SupervisorId { get; set; }
    public Guid SolicitanteId { get; set; }
    public Guid DispositivoId { get; set; }
    public string Permiso { get; set; } = "";
    public Guid RecursoId { get; set; }
    public DateTime ExpiraUtc { get; set; }
    public DateTime? UsadaUtc { get; set; }
}

internal static class Pin
{
    private static readonly PasswordHasher<object> Hasher = new();

    public static bool EsValido(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);
    public static string Hash(string pin) => Hasher.HashPassword(null!, pin);
    public static bool Verificar(string hash, string pin) =>
        Hasher.VerifyHashedPassword(null!, hash, pin) != PasswordVerificationResult.Failed;
}

/// <summary>Random bearer secrets. Only their SHA-256 is stored.</summary>
internal static class Token
{
    public static string Nuevo() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string CodigoCorto() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class IdentidadErrors
{
    public static readonly Error CredencialesInvalidas = Error.Unauthorized("Identidad.CredencialesInvalidas", "Código o PIN incorrecto.");
    public static readonly Error UsuarioBloqueado = Error.Unauthorized("Identidad.UsuarioBloqueado", "Demasiados intentos. Espere unos minutos o pida a un supervisor que cambie el PIN.");
    public static readonly Error DispositivoNoEmparejado = Error.Unauthorized("Identidad.DispositivoNoEmparejado", "Este equipo no está emparejado con la tienda.");
    public static readonly Error YaInstalado = Error.Conflict("Identidad.YaInstalado", "La tienda ya fue instalada.");
    public static readonly Error CodigoDuplicado = Error.Conflict("Identidad.CodigoDuplicado", "Ya existe un usuario con ese código.");
    public static readonly Error UsuarioNoEncontrado = Error.NotFound("Identidad.UsuarioNoEncontrado", "El usuario no existe.");
    public static readonly Error RolNoEncontrado = Error.NotFound("Identidad.RolNoEncontrado", "El rol no existe.");
    public static readonly Error DispositivoNoEncontrado = Error.NotFound("Identidad.DispositivoNoEncontrado", "El dispositivo no existe.");
    public static readonly Error CodigoEmparejamientoInvalido = Error.Unauthorized("Identidad.CodigoEmparejamientoInvalido", "El código de emparejamiento no es válido o ya venció.");
    public static readonly Error SupervisorSinPermiso = Error.Forbidden("Identidad.SupervisorSinPermiso", "Ese usuario no puede autorizar esta acción.");
    public static readonly Error AutorizacionInvalida = Error.Forbidden("Identidad.AutorizacionInvalida", "La autorización del supervisor no es válida, ya se usó o venció.");
    public static readonly Error PermisoDesconocido = Error.Validation("Identidad.PermisoDesconocido", "Uno de los permisos no existe.");
    public static readonly Error SoloAdministrador = Error.Forbidden("Identidad.SoloAdministrador", "Solo un administrador puede dar o quitar el rol Administrador.");
}
