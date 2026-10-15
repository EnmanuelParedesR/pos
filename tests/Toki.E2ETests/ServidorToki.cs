using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Testcontainers.MsSql;

namespace Toki.E2ETests;

/// <summary>The real API as a separate process (`dotnet Toki.Host.Local.dll`), on a fresh database: a Sqlite file,
/// or SQL Server in a container when TOKI_TEST_DB=sqlserver (CI). Every response with status 500 or above is recorded.</summary>
public sealed class ServidorToki : IAsyncDisposable
{
    private readonly string _dll;
    private readonly Dictionary<string, string> _entorno;
    private readonly StringBuilder _salida = new();
    private readonly MsSqlContainer? _contenedor;
    private readonly string? _archivoSqlite;
    private Process? _proceso;

    public Uri Base { get; }

    /// <summary>Signs the day's license; the E2E host trusts its public key instead of Toki's (allowed outside Production only).</summary>
    public System.Security.Cryptography.ECDsa ClaveLicencias { get; } = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    public ConcurrentQueue<string> Errores500 { get; } = new();

    private ServidorToki(string dll, Dictionary<string, string> entorno, Uri @base, MsSqlContainer? contenedor, string? archivoSqlite)
    {
        (_dll, _entorno, Base, _contenedor, _archivoSqlite) = (dll, entorno, @base, contenedor, archivoSqlite);
    }

    public static async Task<ServidorToki> IniciarAsync()
    {
        MsSqlContainer? contenedor = null;
        string? archivo = null;
        string proveedor, conexion;
        if (Environment.GetEnvironmentVariable("TOKI_TEST_DB") == "sqlserver")
        {
            contenedor = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();
            await contenedor.StartAsync();
            (proveedor, conexion) = ("SqlServer", contenedor.GetConnectionString().Replace("Database=master", "Database=toki_e2e", StringComparison.Ordinal));
        }
        else
        {
            archivo = Path.Combine(Path.GetTempPath(), $"toki_e2e_{Guid.NewGuid():N}.db");
            (proveedor, conexion) = ("Sqlite", $"Data Source={archivo}");
        }

        var puerto = PuertoLibre();
        var entorno = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["DOTNET_ENVIRONMENT"] = "E2E",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{puerto}",
            ["Database__Provider"] = proveedor,
            ["Database__ConnectionString"] = conexion,
            ["Database__CrearAlIniciar"] = "true",
            ["Serilog__MinimumLevel__Default"] = "Warning",
            ["Licencia__Archivo"] = Path.Combine(Path.GetTempPath(), $"toki_e2e_licencia_{Guid.NewGuid():N}.jws"),
            ["Respaldo__Carpeta"] = Path.Combine(Path.GetTempPath(), $"toki_e2e_respaldos_{Guid.NewGuid():N}"),
            ["Toki__CarpetaClaves"] = Path.Combine(Path.GetTempPath(), $"toki_e2e_claves_{Guid.NewGuid():N}"),
        };
        var servidor = new ServidorToki(BuscarDll(), entorno, new Uri($"http://127.0.0.1:{puerto}"), contenedor, archivo);
        entorno["Licencia__ClavePublica"] = servidor.ClaveLicencias.ExportSubjectPublicKeyInfoPem();
        await servidor.ArrancarAsync();
        return servidor;
    }

    /// <summary>Stops the process the hard way (as a power cut would) and starts it again on the same database.</summary>
    public async Task ReiniciarAsync()
    {
        await DetenerAsync();
        await ArrancarAsync();
    }

    public HttpClient Cliente(string? dispositivo = null, string? token = null)
    {
        var c = new HttpClient(new Vigia(Errores500) { InnerHandler = new HttpClientHandler() }) { BaseAddress = Base, Timeout = TimeSpan.FromSeconds(60) };
        if (dispositivo is not null) c.DefaultRequestHeaders.Add("X-Dispositivo", dispositivo);
        if (token is not null) c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    private async Task ArrancarAsync()
    {
        var info = new ProcessStartInfo("dotnet", $"\"{_dll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(_dll)!, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var (k, v) in _entorno) info.Environment[k] = v;
        _proceso = Process.Start(info)!;
        _proceso.OutputDataReceived += (_, e) => { lock (_salida) _salida.AppendLine(e.Data); };
        _proceso.ErrorDataReceived += (_, e) => { lock (_salida) _salida.AppendLine(e.Data); };
        _proceso.BeginOutputReadLine();
        _proceso.BeginErrorReadLine();

        using var http = new HttpClient { BaseAddress = Base, Timeout = TimeSpan.FromSeconds(5) };
        var limite = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < limite)
        {
            if (_proceso.HasExited) throw new InvalidOperationException($"La API terminó al arrancar:\n{Salida()}");
            try
            {
                if ((await http.GetAsync("/health")).StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        await DetenerAsync(); // never leave an orphan holding the port and the database file
        throw new TimeoutException($"La API no respondió /health en 90 s:\n{Salida()}");
    }

    private async Task DetenerAsync()
    {
        if (_proceso is null) return;
        if (!_proceso.HasExited) _proceso.Kill(entireProcessTree: true);
        await _proceso.WaitForExitAsync();
        _proceso.Dispose();
        _proceso = null;
    }

    public string Salida()
    {
        lock (_salida) return _salida.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await DetenerAsync();
        if (_contenedor is not null) await _contenedor.DisposeAsync();
        if (_archivoSqlite is not null)
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(_archivoSqlite)!, Path.GetFileName(_archivoSqlite) + "*"))
                try { File.Delete(f); }
                catch (IOException) { } // best effort: a temp file left behind is harmless
    }

    private static int PuertoLibre()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>The API built with the same configuration as these tests (Debug or Release).</summary>
    private static string BuscarDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Toki.slnx"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("No se encontró Toki.slnx");
        var configuracion = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var dll = Path.Combine(dir.FullName, "src", "Hosts", "Toki.Host.Local", "bin", configuracion, "net10.0", "Toki.Host.Local.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("Compile la solución antes de las pruebas e2e.", dll);
    }

    private sealed class Vigia(ConcurrentQueue<string> errores) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = await base.SendAsync(request, cancellationToken);
            if ((int)r.StatusCode >= 500) errores.Enqueue($"{request.Method} {request.RequestUri?.PathAndQuery} → {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(cancellationToken)}");
            return r;
        }
    }
}
