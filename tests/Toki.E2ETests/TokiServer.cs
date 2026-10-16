using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Testcontainers.MsSql;

namespace Toki.E2ETests;

/// <summary>The real API as a separate process (`dotnet Toki.Host.Local.dll`), on a fresh database: a Sqlite file,
/// or SQL Server when TOKI_TEST_DB=sqlserver: the server in TOKI_TEST_SQLSERVER (a local SQL Express) or else a container (CI). Every response with status 500 or above is recorded.</summary>
public sealed class TokiServer : IAsyncDisposable
{
    private readonly string _dll;
    private readonly Dictionary<string, string> _environment;
    private readonly StringBuilder _output = new();
    private readonly MsSqlContainer? _container;
    private readonly string? _sqliteFile;
    private string? _externalDatabase;
    private Process? _process;

    public Uri Base { get; }

    /// <summary>Signs the day's license; the E2E host trusts its public key instead of Toki's (allowed outside Production only).</summary>
    public System.Security.Cryptography.ECDsa LicenseKey { get; } = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    public ConcurrentQueue<string> Errors500 { get; } = new();

    private TokiServer(string dll, Dictionary<string, string> environment, Uri @base, MsSqlContainer? container, string? sqliteFile)
    {
        (_dll, _environment, Base, _container, _sqliteFile) = (dll, environment, @base, container, sqliteFile);
    }

    public static async Task<TokiServer> StartAsync()
    {
        MsSqlContainer? container = null;
        string? file = null, externalDatabase = null;
        string provider, connectionString;
        if (Environment.GetEnvironmentVariable("TOKI_TEST_DB") == "sqlserver" && Environment.GetEnvironmentVariable("TOKI_TEST_SQLSERVER") is { } external)
        {
            externalDatabase = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(external) { InitialCatalog = $"toki_e2e_{Guid.NewGuid():N}" }.ConnectionString;
            (provider, connectionString) = ("SqlServer", externalDatabase);
        }
        else if (Environment.GetEnvironmentVariable("TOKI_TEST_DB") == "sqlserver")
        {
            container = new MsSqlBuilder(Toki.Tests.SqlServerImage.Name).Build();
            await container.StartAsync();
            (provider, connectionString) = ("SqlServer", Toki.Tests.SqlServerImage.ConnectionString(container).Replace("Database=master", "Database=toki_e2e", StringComparison.Ordinal));
        }
        else
        {
            file = Path.Combine(Path.GetTempPath(), $"toki_e2e_{Guid.NewGuid():N}.db");
            (provider, connectionString) = ("Sqlite", $"Data Source={file}");
        }

        var port = FreePort();
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["DOTNET_ENVIRONMENT"] = "E2E",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["Database__Provider"] = provider,
            ["Database__ConnectionString"] = connectionString,
            ["Database__CreateOnStartup"] = "true",
            ["Serilog__MinimumLevel__Default"] = "Warning",
            ["Fiscal__DgiiPublic__Enabled"] = "false", // TK-252: never the real DGII pages from a test
            ["License__File"] = Path.Combine(Path.GetTempPath(), $"toki_e2e_license_{Guid.NewGuid():N}.jws"),
            ["Backup__Folder"] = Path.Combine(Path.GetTempPath(), $"toki_e2e_backups_{Guid.NewGuid():N}"),
            ["Toki__KeysFolder"] = Path.Combine(Path.GetTempPath(), $"toki_e2e_keys_{Guid.NewGuid():N}"),
        };
        var server = new TokiServer(FindDll(), environment, new Uri($"http://127.0.0.1:{port}"), container, file) { _externalDatabase = externalDatabase };
        environment["License__PublicKey"] = server.LicenseKey.ExportSubjectPublicKeyInfoPem();
        await server.LaunchAsync();
        return server;
    }

    /// <summary>Stops the process the hard way (as a power cut would) and starts it again on the same database.</summary>
    public async Task RestartAsync()
    {
        await StopAsync();
        await LaunchAsync();
    }

    public HttpClient Client(string? device = null, string? token = null)
    {
        var c = new HttpClient(new Watcher(Errors500) { InnerHandler = new HttpClientHandler() }) { BaseAddress = Base, Timeout = TimeSpan.FromSeconds(60) };
        if (device is not null) c.DefaultRequestHeaders.Add("X-Device", device);
        if (token is not null) c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return c;
    }

    /// <summary>Starts the process (again, after <see cref="StopAsync"/>) on the same database and waits for /health.</summary>
    public async Task LaunchAsync()
    {
        var info = new ProcessStartInfo("dotnet", $"\"{_dll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(_dll)!, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var (k, v) in _environment) info.Environment[k] = v;
        _process = Process.Start(info)!;
        _process.OutputDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { lock (_output) _output.AppendLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var http = new HttpClient { BaseAddress = Base, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited) throw new InvalidOperationException($"The API exited while starting:\n{Output()}");
            try
            {
                if ((await http.GetAsync("/health")).StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        await StopAsync(); // never leave an orphan holding the port and the database file
        throw new TimeoutException($"The API did not answer /health within 90 s:\n{Output()}");
    }

    /// <summary>Kills the process (as a power cut would); the database stays.</summary>
    public async Task StopAsync()
    {
        if (_process is null) return;
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
        _process = null;
    }

    public string Output()
    {
        lock (_output) return _output.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_container is not null) await _container.DisposeAsync();
        if (_externalDatabase is not null) await DropDatabaseAsync(_externalDatabase);
        if (_sqliteFile is not null)
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(_sqliteFile)!, Path.GetFileName(_sqliteFile) + "*"))
                try { File.Delete(f); }
                catch (IOException) { } // best effort: a temp file left behind is harmless
    }

    /// <summary>A shared server (TOKI_TEST_SQLSERVER) outlives the run, so the throwaway database is dropped here.</summary>
    private static async Task DropDatabaseAsync(string connectionString)
    {
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        var name = b.InitialCatalog;
        b.InitialCatalog = "master";
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(b.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];";
        await cmd.ExecuteNonQueryAsync();
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>The API built with the same configuration as these tests (Debug or Release).</summary>
    private static string FindDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Toki.slnx"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Toki.slnx not found");
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var dll = Path.Combine(dir.FullName, "src", "Hosts", "Toki.Host.Local", "bin", configuration, "net10.0", "Toki.Host.Local.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("Build the solution before running the e2e tests.", dll);
    }

    private sealed class Watcher(ConcurrentQueue<string> errors) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = await base.SendAsync(request, cancellationToken);
            if ((int)r.StatusCode >= 500) errors.Enqueue($"{request.Method} {request.RequestUri?.PathAndQuery} → {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(cancellationToken)}");
            return r;
        }
    }
}
