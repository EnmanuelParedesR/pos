using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.Configuracion;

/// <summary>The license as the vendor signed it. <see cref="Funciones"/> holds <see cref="Funcion"/> names; <see cref="Limites"/> by <see cref="Limite"/> name.</summary>
public sealed record DatosLicencia(string Tienda, string Plan, IReadOnlyList<string> Funciones, IReadOnlyDictionary<string, int> Limites, DateTime EmitidaUtc, DateTime VenceUtc);

/// <summary>Compact JWS with ES256 (ECDSA P-256, SHA-256). Only the vendor's private key signs; the store has only the public key,
/// so there is no license generator in the client (the old system shipped one).</summary>
public static class Jws
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);

    public static string Firmar(DatosLicencia datos, ECDsa clavePrivada)
    {
        var encabezado = B64("""{"alg":"ES256","typ":"JWT"}"""u8);
        var cuerpo = B64(JsonSerializer.SerializeToUtf8Bytes(datos, Opciones));
        var firma = clavePrivada.SignData(Encoding.ASCII.GetBytes($"{encabezado}.{cuerpo}"), HashAlgorithmName.SHA256);
        return $"{encabezado}.{cuerpo}.{B64(firma)}";
    }

    public static Result<DatosLicencia> Verificar(string jws, ECDsa clavePublica)
    {
        var partes = jws.Trim().Split('.');
        if (partes.Length != 3) return LicenciaErrors.Invalida;
        try
        {
            using var encabezado = JsonDocument.Parse(Bytes(partes[0]));
            if (encabezado.RootElement.GetProperty("alg").GetString() != "ES256") return LicenciaErrors.Invalida;
            if (!clavePublica.VerifyData(Encoding.ASCII.GetBytes($"{partes[0]}.{partes[1]}"), Bytes(partes[2]), HashAlgorithmName.SHA256)) return LicenciaErrors.Invalida;
            return JsonSerializer.Deserialize<DatosLicencia>(Bytes(partes[1]), Opciones) is { } datos ? datos : LicenciaErrors.Invalida;
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return LicenciaErrors.Invalida;
        }
    }

    private static string B64(ReadOnlySpan<byte> datos) => Convert.ToBase64String(datos).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Bytes(string b64url)
    {
        var s = b64url.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

internal static class LicenciaErrors
{
    public static readonly Error Invalida = Error.Validation("Licencia.Invalida", "La licencia no es válida o no la firmó Toki.");
}

/// <summary>The installed license, read from <c>Licencia:Archivo</c> (C:\ProgramData\Toki\licencia.jws). The state follows the clock:
/// Activa until it expires, then 15 days of Gracia, then Restringida. Without a license the store runs as Prueba.</summary>
internal sealed class ServicioLicencia : IFeatureService
{
    public static readonly TimeSpan Gracia = TimeSpan.FromDays(15);

    /// <summary>Registers a store without a valid license may pair (the main PC is not counted).</summary>
    public const int CajasDePrueba = 2;

    /// <summary>The vendor's public key. Its private key never ships with the product.</summary>
    private const string ClavePublicaToki = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9b9VXQE8pkSL0IgzQKC3xqvtAw+0
        YaalpiHS8yL+saMYiVCHRb/4/izVOJx1hJ7OfWYUTaP4loWuxZ2+QU17RQ==
        -----END PUBLIC KEY-----
        """;

    private readonly TimeProvider _reloj;
    private readonly ECDsa _clave = ECDsa.Create();

    public ServicioLicencia(IConfiguration config, IHostEnvironment entorno, TimeProvider reloj)
    {
        _reloj = reloj;
        // Only test and development hosts may trust another key; a store (Production) always checks against Toki's.
        _clave.ImportFromPem(!entorno.IsProduction() && config["Licencia:ClavePublica"] is { Length: > 0 } otra ? otra : ClavePublicaToki);
        Archivo = config["Licencia:Archivo"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "licencia.jws");
        if (File.Exists(Archivo) && Jws.Verificar(File.ReadAllText(Archivo), _clave) is { IsSuccess: true } guardada) Datos = guardada.Value;
    }

    public string Archivo { get; }
    public DatosLicencia? Datos { get; private set; }

    public EstadoLicencia Estado
    {
        get
        {
            if (Datos is null) return EstadoLicencia.Prueba;
            var ahora = _reloj.GetUtcNow().UtcDateTime;
            return ahora <= Datos.VenceUtc ? EstadoLicencia.Activa : ahora <= Datos.VenceUtc + Gracia ? EstadoLicencia.Gracia : EstadoLicencia.Restringida;
        }
    }

    private bool Vigente => Estado is EstadoLicencia.Activa or EstadoLicencia.Gracia;

    public bool EstaActiva(Funcion funcion) => Vigente && Datos!.Funciones.Contains(funcion.ToString());

    public int LimiteDe(Limite limite) => Vigente && Datos!.Limites.TryGetValue(limite.ToString(), out var n) ? n : limite switch { Limite.Cajas => CajasDePrueba, _ => 0 };

    public Result<DatosLicencia> Instalar(string jws)
    {
        var verificada = Jws.Verificar(jws, _clave);
        if (verificada.IsFailure) return verificada.Error!;
        Directory.CreateDirectory(Path.GetDirectoryName(Archivo)!);
        File.WriteAllText(Archivo, jws.Trim());
        Datos = verificada.Value;
        return verificada.Value;
    }
}

/// <summary>Installs the license the vendor sent (until Sync brings it down by itself, phase 2).</summary>
internal static class InstalarLicencia
{
    public sealed record Command(string Licencia) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Licencia).NotEmpty().MaximumLength(10_000);
    }

    internal sealed class Handler(ServicioLicencia licencia, IAuditoria auditoria) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var instalada = licencia.Instalar(c.Licencia);
            if (instalada.IsFailure) return instalada.Error!;
            await auditoria.RegistrarAsync("configuracion.licencia", "Licencia", Guid.Empty,
                new { instalada.Value.Plan, instalada.Value.VenceUtc, instalada.Value.Funciones, instalada.Value.Limites }, ct);
            return Ok.Valor;
        }
    }

    public sealed record Funciones(EstadoLicencia Estado, string? Plan, DateTime? VenceUtc, IReadOnlyList<string> Activas, int Cajas);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/configuracion/licencia", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) => (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Configuración");
            // The register shows or hides menus with this; the server still checks every gated action itself.
            app.MapGet("/api/configuracion/funciones", (ServicioLicencia licencia) => Results.Ok(new Funciones(licencia.Estado, licencia.Datos?.Plan, licencia.Datos?.VenceUtc,
                    Enum.GetValues<Funcion>().Where(licencia.EstaActiva).Select(f => f.ToString()).ToList(), licencia.LimiteDe(Limite.Cajas))))
                .RequireAuthorization().WithTags("Configuración");
        }
    }
}
