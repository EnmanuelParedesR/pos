using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Toki.BuildingBlocks.Persistence;
using Toki.Host.Cloud.Assistant;
using Toki.Host.Cloud.Ingest;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Cloud.Tests;

/// <summary>The cloud API in memory on its own Sqlite file. The inbox applier does not run by itself: tests call <see cref="ApplyAsync"/>.</summary>
public class CloudApp : WebApplicationFactory<Program>
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"toki_cloud_test_{Guid.NewGuid():N}.db");

    /// <summary>TK-207: the cold archive on disk (<c>Archive:Folder</c>); tests move documents with <c>ColdArchiver</c> themselves.</summary>
    public string ArchiveFolder { get; } = Path.Combine(Path.GetTempPath(), $"toki_cloud_archive_{Guid.NewGuid():N}");
    private readonly IChatModel? _chatModel;

    public CloudApp() : this(null) { }

    protected CloudApp(IChatModel? chatModel) => _chatModel = chatModel;

    /// <summary>Starts at a fixed store day: 2026-10-15 11:00 in the store (UTC−4).</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.Zero));

    public static readonly DateOnly Today = new(2026, 10, 15);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:ConnectionString", $"Data Source={_file};Pooling=False");
        builder.UseSetting("Ingest:ApplyInBackground", "false");
        builder.UseSetting("Archive:Folder", ArchiveFolder);
        builder.UseSetting("Archive:MoveInBackground", "false");
        builder.UseSetting("Nodes:RegisterPerMinute", "10000");
        builder.UseSetting("Catalog:RequestsPerMinute", "10000");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Platform:Admins:0", LegacyAdminEmail); // R8 #1: ignored now
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);
            if (_chatModel is not null) s.AddSingleton(_chatModel);
        });
    }

    public Task<int> ApplyAsync() => Services.GetRequiredService<InboxApplier>().ApplyPendingAsync(CancellationToken.None);

    public async Task<T> InDbAsync<T>(Func<CloudDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<CloudDbContext>());
    }

    /// <summary>A new panel account (and so a new tenant), signed in.</summary>
    public async Task<Owner> OwnerAsync(string? email = null)
    {
        email ??= $"owner{Guid.NewGuid():N}@example.com";
        (await CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = Password })).EnsureSuccessStatusCode();
        return await SignInAsync(email);
    }

    /// <summary>A new token for an existing account (claims are read at sign-in).</summary>
    public async Task<Owner> SignInAsync(string email)
    {
        var login = await CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Json()).GetProperty("accessToken").GetString());
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        return new Owner(client, me.GetProperty("tenantId").GetGuid(), email);
    }

    public const string Password = "Carniceria2026";

    /// <summary>Toki staff once the account exists and <c>grant-platform-admin</c> ran (TK-189).</summary>
    public const string StaffEmail = "staff@toki.example";

    /// <summary>In the old <c>Platform:Admins</c> setting, which no longer grants anything (R8 #1).</summary>
    public const string LegacyAdminEmail = "legacy-admin@toki.example";

    private Owner? _staff;

    /// <summary>The platform admin of this app (TK-189): registered, granted with the CLI command and signed in again, on first use.</summary>
    public async Task<Owner> StaffAsync()
    {
        if (_staff is not null) return _staff;
        await OwnerAsync(StaffEmail);
        if (await Toki.Host.Cloud.Platform.PlatformCli.RunAsync(Services, Toki.Host.Cloud.Platform.PlatformCli.Grant, StaffEmail) != 0)
            throw new InvalidOperationException("grant-platform-admin failed");
        return _staff = await SignInAsync(StaffEmail);
    }

    /// <summary>A store for the owner with its main PC registered as a node.</summary>
    public async Task<StoreNode> StoreWithNodeAsync(Owner owner, string name = "Carnicería La Esquina")
    {
        var store = (await (await owner.Client.PostAsJsonAsync("/api/stores", new { name })).Json()).GetProperty("id").GetGuid();
        var code = (await (await owner.Client.PostAsync($"/api/stores/{store}/connection-codes", null)).Json()).GetProperty("code").GetString();
        var r = await CreateClient().PostAsJsonAsync("/api/nodes/register", new { code, nodeName = "PC principal" });
        r.EnsureSuccessStatusCode();
        var json = await r.Json();
        var node = CreateClient();
        node.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Node", $"{json.GetProperty("nodeId").GetGuid()}.{json.GetProperty("nodeSecret").GetString()}");
        return new StoreNode(store, json.GetProperty("nodeId").GetGuid(), node);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_file); }
        catch (IOException) { } // best effort: a temp file left behind is harmless
        try { if (Directory.Exists(ArchiveFolder)) Directory.Delete(ArchiveFolder, recursive: true); }
        catch (IOException) { }
    }
}

public sealed record Owner(HttpClient Client, Guid TenantId, string Email);

public sealed record StoreNode(Guid StoreId, Guid NodeId, HttpClient Client);

public static class Events
{
    /// <summary>The event as the node's outbox stores it (CLR full name, web JSON with enums as text) and the uploader sends it.</summary>
    public static object Envelope(IIntegrationEvent e, DateTime occurredUtc)
    {
        e.OccurredUtc = occurredUtc;
        return new
        {
            id = e.EventId, type = e.GetType().FullName, aggregateId = e.AggregateId, occurredUtc = e.OccurredUtc,
            payload = JsonSerializer.SerializeToElement(e, e.GetType(), Json.Options),
        };
    }

    public static async Task<HttpResponseMessage> SendAsync(this StoreNode node, params object[] envelopes) =>
        await node.Client.PostAsJsonAsync("/ingest/v1/events", new { events = envelopes });
}

public static class HttpExtensions
{
    public static async Task<JsonElement> Json(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string?> ErrorCode(this HttpResponseMessage r) =>
        (await r.Json()).TryGetProperty("code", out var c) ? c.GetString() : null;
}
