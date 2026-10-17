using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(Abastalia.Lite.Tests.TestSqlServer))]

namespace Abastalia.Lite.Tests;

/// <summary>SQL Server (Row-Level Security on) when ABASTALIA_TEST_DB=sqlserver: ABASTALIA_TEST_SQLSERVER (a local SQL Express) or a container (CI).
/// Otherwise Sqlite, where only the EF filter separates the businesses.</summary>
public sealed class TestSqlServer : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public static bool IsEnabled => Environment.GetEnvironmentVariable("ABASTALIA_TEST_DB") == "sqlserver";
    public static string? BaseConnectionString { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (!IsEnabled) return;
        BaseConnectionString = Environment.GetEnvironmentVariable("ABASTALIA_TEST_SQLSERVER");
        if (BaseConnectionString is not null) return;
        _container = new MsSqlBuilder(Abastalia.Tests.SqlServerImage.Name).Build();
        await _container.StartAsync();
        BaseConnectionString = Abastalia.Tests.SqlServerImage.ConnectionString(_container);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

/// <summary>The Lite host in memory on a fresh database, its cloud faked. Background work is off unless a test turns it on (<see cref="Workers"/>).</summary>
public class LiteApp : WebApplicationFactory<Program>
{
    public const string Key = "test-provisioning-key-0123456789abcdef";
    public const string Pin = "1234";
    private readonly string _provider, _connectionString;
    private readonly string? _sqliteFile;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"abastalia_lite_{Guid.NewGuid():N}");

    public LiteApp()
    {
        if (TestSqlServer.IsEnabled)
        {
            _provider = "SqlServer";
            _connectionString = new SqlConnectionStringBuilder(TestSqlServer.BaseConnectionString) { InitialCatalog = $"abastalia_test_lite_{Guid.NewGuid():N}" }.ConnectionString;
        }
        else
        {
            _provider = "Sqlite";
            _sqliteFile = Path.Combine(Path.GetTempPath(), $"abastalia_test_lite_{Guid.NewGuid():N}.db");
            _connectionString = $"Data Source={_sqliteFile};Pooling=False";
        }
    }

    protected virtual bool Workers => false;

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public FakeCloud Cloud { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", _provider);
        builder.UseSetting("Database:ConnectionString", _connectionString);
        builder.UseSetting("Database:CreateOnStartup", "true");
        builder.UseSetting("Database:MultiTenant", "true");
        builder.UseSetting("Lite:ProvisioningKey", Key);
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Outbox:Process", Workers.ToString());
        builder.UseSetting("Sync:Enabled", Workers.ToString());
        builder.UseSetting("Cloud:Url", FakeCloud.Url);
        builder.UseSetting("Identity:IdleMinutes", "10000000");
        builder.UseSetting("Abastalia:KeysFolder", Path.Combine(_folder, "keys"));
        builder.UseSetting("License:File", Path.Combine(_folder, "license.jws"));
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            s.AddHttpClient("Abastalia.Cloud").ConfigurePrimaryHttpMessageHandler(() => Cloud); // Sync's CloudClient.HttpClientName (internal)
        });
    }

    /// <summary>Provisions a business as the cloud will (TK-325) and signs its owner in on its first device.</summary>
    public async Task<Owner> BusinessAsync(string ownerName, string? connectionCode = null)
    {
        var tenantId = Guid.CreateVersion7();
        var r = await Provision(new { TenantId = tenantId, OwnerName = ownerName, OwnerCode = "dueno", Pin, DeviceName = "Celular", ConnectionCode = connectionCode });
        r.StatusCode.ShouldBe(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var created = await r.Content.ReadFromJsonAsync<JsonElement>();
        var device = created.GetProperty("deviceSecret").GetString()!;
        var session = await Device(device).PostAsJsonAsync("/api/identity/sessions", new { Code = "dueno", Pin });
        session.EnsureSuccessStatusCode();
        var token = (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        return new Owner(tenantId, device, token, created.TryGetProperty("cloudError", out var e) ? e.GetString() : null);
    }

    public Task<HttpResponseMessage> Provision(object body, string? key = Key)
    {
        var c = CreateClient();
        if (key is not null) c.DefaultRequestHeaders.Add("X-Provisioning-Key", key);
        return c.PostAsJsonAsync("/internal/tenants", body);
    }

    public HttpClient Device(string? device)
    {
        var c = CreateClient();
        if (device is not null) c.DefaultRequestHeaders.Add("X-Device", device);
        return c;
    }

    public HttpClient Client(Owner o, string? token = null)
    {
        var c = Device(o.Device);
        c.DefaultRequestHeaders.Authorization = new("Bearer", token ?? o.Token);
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (_sqliteFile is not null) File.Delete(_sqliteFile);
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException) { } // best effort: temp files left behind are harmless
        if (TestSqlServer.IsEnabled) DropDatabase();
    }

    private void DropDatabase()
    {
        SqlConnection.ClearAllPools();
        var database = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
        using var c = new SqlConnection(new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" }.ConnectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
        cmd.ExecuteNonQuery();
    }
}

public sealed record Owner(Guid TenantId, string Device, string Token, string? CloudError);

/// <summary>The cloud of ADR-0008, in memory: every 8-digit code registers a new node, and each request is kept with the node that signed it
/// (<c>Authorization: Node nodeId.…</c>). Gzip bodies are inflated.</summary>
public sealed class FakeCloud : HttpMessageHandler
{
    public const string Url = "https://cloud.test";

    /// <summary>The node each code registered.</summary>
    public ConcurrentDictionary<string, Guid> Nodes { get; } = new();

    public ConcurrentQueue<(Guid? Node, string Path, JsonElement Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (request.Content?.Headers.ContentEncoding.Contains("gzip") == true)
        {
            using var inflated = new MemoryStream();
            using (var z = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)) await z.CopyToAsync(inflated, cancellationToken);
            bytes = inflated.ToArray();
        }
        var body = bytes.Length == 0 ? default : JsonDocument.Parse(bytes).RootElement.Clone();
        var path = request.RequestUri!.AbsolutePath;
        Guid? node = request.Headers.Authorization is { Scheme: "Node", Parameter: { } p } && Guid.TryParse(p.Split('.')[0], out var id) ? id : null;
        Requests.Enqueue((node, path, body));
        return path switch
        {
            "/api/nodes/register" => Json(new { tenantId = Guid.NewGuid(), storeId = Guid.NewGuid(), nodeId = Nodes.GetOrAdd(body.GetProperty("code").GetString()!, _ => Guid.NewGuid()) }),
            "/ingest/v1/events" => Json(new { accepted = body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList() }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
