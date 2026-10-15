using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MsSql;
using Toki.BuildingBlocks.Persistence;

[assembly: AssemblyFixture(typeof(Toki.IntegrationTests.Infra.TestSqlServer))]

namespace Toki.IntegrationTests.Infra;

/// <summary>SQL Server when TOKI_TEST_DB=sqlserver: the server in TOKI_TEST_SQLSERVER (e.g. a local SQL Express) or else a container (CI);
/// otherwise each app uses its own Sqlite file.</summary>
public sealed class TestSqlServer : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public static bool IsEnabled => Environment.GetEnvironmentVariable("TOKI_TEST_DB") == "sqlserver";
    public static string? BaseConnectionString { get; private set; }

    /// <summary>The account SQL Server runs as, when it runs on this PC (TOKI_TEST_SQLSERVER).</summary>
    public static string? ServiceAccount { get; private set; }

    /// <summary>Sqlite, or a SQL Server on this PC: BACKUP writes a .bak the tests can read. A container writes it inside itself.</summary>
    public static bool BacksUpOnThisMachine => !IsEnabled || ServiceAccount is not null;

    /// <summary>A new backup folder SQL Server can write to, as the installer grants in a store (PrepareService).</summary>
    public static string BackupFolder()
    {
        var folder = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"toki_backups_{Guid.NewGuid():N}"));
        if (ServiceAccount is null || !OperatingSystem.IsWindows()) return folder.FullName;
        folder.Create();
        var acl = folder.GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(ServiceAccount, System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        folder.SetAccessControl(acl);
        return folder.FullName;
    }

    public async ValueTask InitializeAsync()
    {
        if (!IsEnabled) return;
        BaseConnectionString = Environment.GetEnvironmentVariable("TOKI_TEST_SQLSERVER");
        if (BaseConnectionString is not null)
        {
            await using var c = new Microsoft.Data.SqlClient.SqlConnection(BaseConnectionString);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT service_account FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server (%'";
            ServiceAccount = (string?)await cmd.ExecuteScalarAsync();
            return;
        }
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest").Build();
        await _container.StartAsync();
        BaseConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        else if (BaseConnectionString is not null) await DropTestDatabasesAsync(BaseConnectionString, "toki[_]test[_]%");
    }

    /// <summary>A shared server outlives the run, so the throwaway databases are dropped here.</summary>
    public static async Task DropTestDatabasesAsync(string connectionString, string pattern)
    {
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(name) + N';'
            FROM sys.databases WHERE name LIKE @pattern;
            EXEC sp_executesql @sql;
            """;
        cmd.Parameters.AddWithValue("@pattern", pattern);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>The real API in memory, on a fresh database. One per test class.</summary>
public class TokiApp : WebApplicationFactory<Program>
{
    public const string AdminPin = "1234";
    private readonly string _provider;
    private readonly string _connectionString;
    private readonly string? _sqliteFile;
    private readonly SemaphoreSlim _setup = new(1, 1);
    private UserSession? _admin;

    public TokiApp()
    {
        if (TestSqlServer.IsEnabled)
        {
            _provider = "SqlServer";
            _connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(TestSqlServer.BaseConnectionString)
            { InitialCatalog = $"toki_test_{Guid.NewGuid():N}" }.ConnectionString;
        }
        else
        {
            _provider = "Sqlite";
            _sqliteFile = Path.Combine(Path.GetTempPath(), $"toki_test_{Guid.NewGuid():N}.db");
            _connectionString = $"Data Source={_sqliteFile};Pooling=False";
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", _provider);
        builder.UseSetting("Database:ConnectionString", _connectionString);
        builder.UseSetting("Database:CreateOnStartup", "true");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        // Background work would race the tests when they move the fake clock; tests run it themselves.
        builder.UseSetting("Outbox:Process", "false");
        builder.UseSetting("Audit:NightlyCheck", "false");
        builder.UseSetting("Sync:Enabled", "false");
        builder.UseSetting("Cloud:Url", FakeCloud.Url);
        builder.UseSetting("Backup:Automatic", "false");
        builder.UseSetting("Toki:KeysFolder", Path.Combine(Path.GetTempPath(), $"toki_keys_{Guid.NewGuid():N}"));
        builder.UseSetting("License:PublicKey", LicenseKey.ExportSubjectPublicKeyInfoPem());
        builder.UseSetting("License:File", Path.Combine(Path.GetTempPath(), $"toki_license_{Guid.NewGuid():N}.jws"));
        builder.UseSetting("Backup:Folder", TestSqlServer.BackupFolder());
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
        Dgii.Now = Clock.GetUtcNow;
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            TestCommands.Register(s);
            s.AddHttpClient(Toki.Sync.CloudClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Cloud);
            s.AddHttpClient(Toki.Fiscal.DgiiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Dgii);
        });
    }

    /// <summary>The cloud this app talks to (ADR-0008), in memory.</summary>
    public FakeCloud Cloud { get; } = new();

    /// <summary>The DGII web services this app talks to when <c>Fiscal:Provider</c> is <c>Dgii</c>, in memory.</summary>
    public FakeDgii Dgii { get; } = new();

    /// <summary>Extra configuration of a subclass (e.g. the real signer and the DGII provider).</summary>
    protected virtual IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();

    /// <summary>Signs this app's test licenses; the app trusts its public key instead of Toki's (allowed outside Production only).</summary>
    public System.Security.Cryptography.ECDsa LicenseKey { get; } = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

    /// <summary>The application clock. Starts at the real time and only moves when a test advances it.</summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public HttpClient Anonymous(string? device = null)
    {
        var c = CreateClient();
        if (device is not null) c.DefaultRequestHeaders.Add("X-Device", device);
        return c;
    }

    public HttpClient Client(UserSession s)
    {
        var c = Anonymous(s.Device);
        c.DefaultRequestHeaders.Authorization = new("Bearer", s.Token);
        return c;
    }

    /// <summary>Sets up the shop once per app and returns the administrator's session.</summary>
    public async Task<UserSession> AdminAsync()
    {
        await _setup.WaitAsync();
        try
        {
            if (_admin is not null) return _admin;
            var r = await Anonymous().PostAsJsonAsync("/api/identity/setup",
                new { AdministratorName = "Dueña", AdministratorCode = "admin", Pin = AdminPin, DeviceName = "PC principal" });
            r.EnsureSuccessStatusCode();
            var device = (await r.Json()).GetProperty("deviceToken").GetString()!;
            _admin = await SignInAsync("admin", AdminPin, device);
            return _admin;
        }
        finally { _setup.Release(); }
    }

    public async Task<UserSession> SignInAsync(string code, string pin, string device)
    {
        var r = await Anonymous(device).PostAsJsonAsync("/api/identity/sessions", new { Code = code, Pin = pin });
        r.EnsureSuccessStatusCode();
        var json = await r.Json();
        return new UserSession(json.GetProperty("token").GetString()!, device, json.GetProperty("userId").GetGuid());
    }

    /// <summary>Creates a user with the given role on the admin's device and logs them in.</summary>
    public async Task<UserSession> UserAsync(Guid roleId, string pin = "4321")
    {
        var admin = await AdminAsync();
        var code = $"u{Guid.NewGuid():N}"[..12];
        var r = await Client(admin).PostAsJsonAsync("/api/identity/users", new { Name = "Usuario " + code, Code = code, Pin = pin, RoleId = roleId });
        r.EnsureSuccessStatusCode();
        return await SignInAsync(code, pin, admin.Device);
    }

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Runs a test-only command with no user (system context).</summary>
    internal async Task<Toki.SharedKernel.Result<Toki.BuildingBlocks.Messaging.Ok>> CommandAsync<TCommand>(TCommand command)
        where TCommand : Toki.BuildingBlocks.Messaging.ICommand<Toki.BuildingBlocks.Messaging.Ok>
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Toki.BuildingBlocks.Messaging.ICommandHandler<TCommand, Toki.BuildingBlocks.Messaging.Ok>>()
            .Handle(command, CancellationToken.None);
    }

    /// <summary>The kardex rule: for every product and warehouse, the movements add up to the stock and the last row's result equals it.</summary>
    public Task<IReadOnlyList<string>> KardexMismatchesAsync() => InScopeAsync<IReadOnlyList<string>>(async sp =>
    {
        var db = sp.GetRequiredService<Toki.Inventory.Persistence.InventoryDbContext>();
        var stock = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Stock);
        var movements = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.StockMovements);
        return stock
            .Where(e =>
            {
                var own = movements.Where(m => m.ProductId == e.ProductId && m.WarehouseId == e.WarehouseId).OrderBy(m => m.Sequence).ToList();
                return own.Sum(m => m.Quantity) != e.Quantity || (own.Count > 0 && own[^1].ResultingStock != e.Quantity);
            })
            .Select(e => $"{e.ProductId}/{e.WarehouseId}: {e.Quantity}").ToList();
    });

    public Task<long?> VerifyAuditAsync() =>
        InScopeAsync(sp => AuditChain.VerifyAsync(sp.GetRequiredService<InfraDbContext>(), CancellationToken.None));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _setup.Dispose();
        if (_sqliteFile is null) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_sqliteFile); }
        catch (IOException) { } // best effort: a temp file left behind is harmless
    }
}

public sealed record UserSession(string Token, string Device, Guid UserId);

public static class Roles
{
    public static readonly Guid Administrator = new("0199e000-0000-7000-8000-000000000001");
    public static readonly Guid Supervisor = new("0199e000-0000-7000-8000-000000000002");
    public static readonly Guid Cashier = new("0199e000-0000-7000-8000-000000000003");
}

public static class HttpExtensions
{
    public static async Task<JsonElement> Json(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>The stable error code of a ProblemDetails response.</summary>
    public static async Task<string?> ErrorCode(this HttpResponseMessage r) =>
        (await r.Json()).TryGetProperty("code", out var c) ? c.GetString() : null;
}
