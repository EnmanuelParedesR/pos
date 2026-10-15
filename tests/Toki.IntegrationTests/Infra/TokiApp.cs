using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MsSql;
using Toki.BuildingBlocks.Persistence;

[assembly: AssemblyFixture(typeof(Toki.IntegrationTests.Infra.SqlServerDePrueba))]

namespace Toki.IntegrationTests.Infra;

/// <summary>SQL Server in a container when TOKI_TEST_DB=sqlserver (CI); otherwise each app uses its own Sqlite file.</summary>
public sealed class SqlServerDePrueba : IAsyncLifetime
{
    private MsSqlContainer? _contenedor;

    public static bool Activo => Environment.GetEnvironmentVariable("TOKI_TEST_DB") == "sqlserver";
    public static string? ConnectionStringBase { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!Activo) return;
        _contenedor = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();
        await _contenedor.StartAsync();
        ConnectionStringBase = _contenedor.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_contenedor is not null) await _contenedor.DisposeAsync();
    }
}

/// <summary>The real API in memory, on a fresh database. One per test class.</summary>
public sealed class TokiApp : WebApplicationFactory<Program>
{
    public const string PinAdmin = "1234";
    private readonly string _provider;
    private readonly string _connectionString;
    private readonly string? _archivoSqlite;
    private readonly SemaphoreSlim _instalacion = new(1, 1);
    private Sesion? _admin;

    public TokiApp()
    {
        if (SqlServerDePrueba.Activo)
        {
            _provider = "SqlServer";
            _connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SqlServerDePrueba.ConnectionStringBase)
            { InitialCatalog = $"toki_{Guid.NewGuid():N}" }.ConnectionString;
        }
        else
        {
            _provider = "Sqlite";
            _archivoSqlite = Path.Combine(Path.GetTempPath(), $"toki_test_{Guid.NewGuid():N}.db");
            _connectionString = $"Data Source={_archivoSqlite};Pooling=False";
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", _provider);
        builder.UseSetting("Database:ConnectionString", _connectionString);
        builder.UseSetting("Database:CrearAlIniciar", "true");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        // Background work would race the tests when they move the fake clock; tests run it themselves.
        builder.UseSetting("Outbox:Procesar", "false");
        builder.UseSetting("Auditoria:VerificacionNocturna", "false");
        builder.UseSetting("Respaldo:Automatico", "false");
        builder.UseSetting("Licencia:ClavePublica", ClaveLicencias.ExportSubjectPublicKeyInfoPem());
        builder.UseSetting("Licencia:Archivo", Path.Combine(Path.GetTempPath(), $"toki_licencia_{Guid.NewGuid():N}.jws"));
        builder.UseSetting("Respaldo:Carpeta", Path.Combine(Path.GetTempPath(), $"toki_respaldos_{Guid.NewGuid():N}"));
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Reloj);
            ComandosDePrueba.Registrar(s);
        });
    }

    /// <summary>Signs this app's test licenses; the app trusts its public key instead of Toki's (allowed outside Production only).</summary>
    public System.Security.Cryptography.ECDsa ClaveLicencias { get; } = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

    /// <summary>The application clock. Starts at the real time and only moves when a test advances it.</summary>
    public FakeTimeProvider Reloj { get; } = new(DateTimeOffset.UtcNow);

    public HttpClient Anonimo(string? dispositivo = null)
    {
        var c = CreateClient();
        if (dispositivo is not null) c.DefaultRequestHeaders.Add("X-Dispositivo", dispositivo);
        return c;
    }

    public HttpClient Cliente(Sesion s)
    {
        var c = Anonimo(s.Dispositivo);
        c.DefaultRequestHeaders.Authorization = new("Bearer", s.Token);
        return c;
    }

    /// <summary>Installs the shop once per app and returns the administrator's session.</summary>
    public async Task<Sesion> AdminAsync()
    {
        await _instalacion.WaitAsync();
        try
        {
            if (_admin is not null) return _admin;
            var r = await Anonimo().PostAsJsonAsync("/api/identidad/instalacion",
                new { NombreAdministrador = "Dueña", CodigoAdministrador = "admin", Pin = PinAdmin, NombreDispositivo = "PC principal" });
            r.EnsureSuccessStatusCode();
            var dispositivo = (await r.Json()).GetProperty("tokenDispositivo").GetString()!;
            _admin = await IniciarSesionAsync("admin", PinAdmin, dispositivo);
            return _admin;
        }
        finally { _instalacion.Release(); }
    }

    public async Task<Sesion> IniciarSesionAsync(string codigo, string pin, string dispositivo)
    {
        var r = await Anonimo(dispositivo).PostAsJsonAsync("/api/identidad/sesiones", new { Codigo = codigo, Pin = pin });
        r.EnsureSuccessStatusCode();
        var json = await r.Json();
        return new Sesion(json.GetProperty("token").GetString()!, dispositivo, json.GetProperty("usuarioId").GetGuid());
    }

    /// <summary>Creates a user with the given role on the admin's device and logs them in.</summary>
    public async Task<Sesion> UsuarioAsync(Guid rolId, string pin = "4321")
    {
        var admin = await AdminAsync();
        var codigo = $"u{Guid.NewGuid():N}"[..12];
        var r = await Cliente(admin).PostAsJsonAsync("/api/identidad/usuarios", new { Nombre = "Usuario " + codigo, Codigo = codigo, Pin = pin, RolId = rolId });
        r.EnsureSuccessStatusCode();
        return await IniciarSesionAsync(codigo, pin, admin.Dispositivo);
    }

    public async Task<T> EnScopeAsync<T>(Func<IServiceProvider, Task<T>> accion)
    {
        await using var scope = Services.CreateAsyncScope();
        return await accion(scope.ServiceProvider);
    }

    /// <summary>Runs a test-only command with no user (system context).</summary>
    internal async Task<Toki.SharedKernel.Result<Toki.BuildingBlocks.Messaging.Ok>> ComandoAsync<TCommand>(TCommand comando)
        where TCommand : Toki.BuildingBlocks.Messaging.ICommand<Toki.BuildingBlocks.Messaging.Ok>
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Toki.BuildingBlocks.Messaging.ICommandHandler<TCommand, Toki.BuildingBlocks.Messaging.Ok>>()
            .Handle(comando, CancellationToken.None);
    }

    /// <summary>The kardex rule: for every product and warehouse, the movements add up to the stock and the last row's result equals it.</summary>
    public Task<IReadOnlyList<string>> DescuadresKardexAsync() => EnScopeAsync<IReadOnlyList<string>>(async sp =>
    {
        var db = sp.GetRequiredService<Toki.Inventario.Persistence.InventarioDbContext>();
        var existencias = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Existencias);
        var movimientos = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Movimientos);
        return existencias
            .Where(e =>
            {
                var propios = movimientos.Where(m => m.ProductoId == e.ProductoId && m.AlmacenId == e.AlmacenId).OrderBy(m => m.Secuencia).ToList();
                return propios.Sum(m => m.Cantidad) != e.Cantidad || (propios.Count > 0 && propios[^1].ExistenciaResultante != e.Cantidad);
            })
            .Select(e => $"{e.ProductoId}/{e.AlmacenId}: {e.Cantidad}").ToList();
    });

    public Task<long?> VerificarAuditoriaAsync() =>
        EnScopeAsync(sp => CadenaAuditoria.VerificarAsync(sp.GetRequiredService<InfraDbContext>(), CancellationToken.None));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _instalacion.Dispose();
        if (_archivoSqlite is null) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_archivoSqlite); }
        catch (IOException) { } // best effort: a temp file left behind is harmless
    }
}

public sealed record Sesion(string Token, string Dispositivo, Guid UsuarioId);

public static class Roles
{
    public static readonly Guid Administrador = new("0199e000-0000-7000-8000-000000000001");
    public static readonly Guid Supervisor = new("0199e000-0000-7000-8000-000000000002");
    public static readonly Guid Cajero = new("0199e000-0000-7000-8000-000000000003");
}

public static class HttpExtensions
{
    public static async Task<JsonElement> Json(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>The stable error code of a ProblemDetails response.</summary>
    public static async Task<string?> CodigoError(this HttpResponseMessage r) =>
        (await r.Json()).TryGetProperty("code", out var c) ? c.GetString() : null;
}
