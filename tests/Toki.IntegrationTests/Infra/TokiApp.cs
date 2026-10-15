using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
    }

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

    public Task<long?> VerificarAuditoriaAsync() =>
        EnScopeAsync(sp => CadenaAuditoria.VerificarAsync(sp.GetRequiredService<InfraDbContext>(), CancellationToken.None));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _instalacion.Dispose();
        if (_archivoSqlite is not null) File.Delete(_archivoSqlite);
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
