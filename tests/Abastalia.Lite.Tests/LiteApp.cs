using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Testcontainers.MsSql;
using Abastalia.BuildingBlocks.Security;

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
        builder.UseSetting("Lite:PairPerMinutePerIp", "1000"); // every test request comes from no address
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

    /// <summary>Provisions a business as the cloud will (TK-325), pairs its selling device with a code from the panel (TK-321) and signs its
    /// owner in there.</summary>
    public async Task<Owner> BusinessAsync(string ownerName, string? connectionCode = null)
    {
        var tenantId = Guid.CreateVersion7();
        var r = await Provision(new { TenantId = tenantId, OwnerName = ownerName, OwnerCode = "dueno", Pin, ConnectionCode = connectionCode });
        r.StatusCode.ShouldBe(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var created = await r.Content.ReadFromJsonAsync<JsonElement>();
        var device = await PairAsync(await CodeAsync(tenantId));
        var token = await SignInAsync(device, "dueno", Pin);
        return new Owner(tenantId, device, token, created.TryGetProperty("cloudError", out var e) ? e.GetString() : null);
    }

    public Task<HttpResponseMessage> Provision(object body, string? key = Key) => Panel(key).PostAsJsonAsync("/internal/tenants", body);

    /// <summary>What the cloud's panel calls (Host.Cloud LiteDevices), with the provisioning key.</summary>
    public HttpClient Panel(string? key = Key)
    {
        var c = CreateClient();
        if (key is not null) c.DefaultRequestHeaders.Add("X-Provisioning-Key", key);
        return c;
    }

    public async Task<string> CodeAsync(Guid tenantId)
    {
        var r = await Panel().PostAsync($"/internal/tenants/{tenantId}/device-codes", null);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    public Task<HttpResponseMessage> PairAsync(string code, TestDevice key) =>
        CreateClient().PostAsJsonAsync("/lite/devices/pair", new { code, publicKey = key.PublicKey, name = "Celular" });

    public async Task<TestDevice> PairAsync(string code)
    {
        var device = new TestDevice();
        var r = await PairAsync(code, device);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        device.Id = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deviceId").GetGuid();
        return device;
    }

    public async Task<string> SignInAsync(TestDevice device, string code, string pin)
    {
        var session = await Device(device).PostAsJsonAsync("/api/identity/sessions", new { Code = code, Pin = pin });
        session.StatusCode.ShouldBe(HttpStatusCode.OK, await session.Content.ReadAsStringAsync());
        return (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    /// <summary>Every request signed by <paramref name="device"/> (none: unsigned), as the browser's lite-signer does.</summary>
    public HttpClient Device(TestDevice? device) => device is null ? CreateClient() : CreateDefaultClient(new SigningHandler(device, Clock));

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

public sealed record Owner(Guid TenantId, TestDevice Device, string Token, string? CloudError);

/// <summary>A selling device's P-256 key, as the browser makes it (TK-321). <see cref="Id"/> once paired; an unpaired one is unknown to the host.</summary>
public sealed class TestDevice
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    /// <summary>The <c>X-Device-Signature</c> value over <c>id.seconds.nonce.METHOD /path?query bodyHash</c> (ADR-0008's node format).</summary>
    public string Sign(string method, string pathAndQuery, string bodyHash, long seconds, string nonce)
    {
        var payload = NodeSignature.Payload(Id, seconds, nonce, method, pathAndQuery, bodyHash);
        return $"{Id}.{seconds}.{nonce}.{NodeSignature.Base64Url(_key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256))}";
    }

    public static string NewNonce() => NodeSignature.Base64Url(RandomNumberGenerator.GetBytes(16));
}

/// <summary>Signs every request with the device's key at the host's (fake) time.</summary>
public sealed class SigningHandler(TestDevice device, TimeProvider clock) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var hash = NodeSignature.BodyHash(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
        request.Headers.Add("X-Device-Signature", device.Sign(request.Method.Method, request.RequestUri!.PathAndQuery, hash, clock.GetUtcNow().ToUnixTimeSeconds(), TestDevice.NewNonce()));
        request.Headers.Add(NodeSignature.BodyHashHeader, hash);
        return await base.SendAsync(request, cancellationToken);
    }
}

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
            "/fiscal/v1/archive" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
