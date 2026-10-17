using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Host.Cloud.Assistant;
using Abastalia.Host.Cloud.Ingest;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Cloud.Tests;

/// <summary>The cloud API in memory on its own Sqlite file. The inbox applier does not run by itself: tests call <see cref="ApplyAsync"/>.</summary>
public class CloudApp : WebApplicationFactory<Program>
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"abastalia_cloud_test_{Guid.NewGuid():N}.db");

    /// <summary>TK-207: the cold archive on disk (<c>Archive:Folder</c>); tests move documents with <c>ColdArchiver</c> themselves.</summary>
    public string ArchiveFolder { get; } = Path.Combine(Path.GetTempPath(), $"abastalia_cloud_archive_{Guid.NewGuid():N}");

    /// <summary>TK-238: the stores' backups on disk (<c>CloudBackup:Folder</c>).</summary>
    public string BackupFolder { get; } = Path.Combine(Path.GetTempPath(), $"abastalia_cloud_backups_{Guid.NewGuid():N}");
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
        builder.UseSetting("CloudBackup:Folder", BackupFolder);
        builder.UseSetting("CloudBackup:UploadsPerHourPerNode", "10000");
        builder.UseSetting("Nodes:RegisterPerMinute", "10000");
        builder.UseSetting("Nodes:RequestsPerMinutePerIp", "10000");
        builder.UseSetting("Nodes:ConcurrentPerIp", "1000");
        builder.UseSetting("Catalog:RequestsPerMinute", "10000");
        builder.UseSetting("Fe:RequestsPerMinutePerIp", "10000");
        builder.UseSetting("Fe:RequestsPerMinutePerRnc", "10000");
        builder.UseSetting("Auth:ResendConfirmationPerMinute", "10000");
        builder.UseSetting("Auth:RequestsPerMinute", "10000"); // TK-248: every test client is the same (empty) IP
        builder.UseSetting("Panel:RequestsPerMinutePerUser", "10000");
        builder.UseSetting("Ai:AssistantQuestionsPerMinutePerUser", "10000");
        builder.UseSetting("Alerts:MinutesBetweenRuns", "0"); // R12 H: tests run the alerts on every apply
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.UseSetting("Platform:Admins:0", LegacyAdminEmail); // R8 #1: ignored now
        builder.UseSetting("Certification:StaffEmails:0", SupportEmail); // TK-233
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Clock);            if (_chatModel is not null) s.AddSingleton(_chatModel);
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

    /// <summary>R11 B-High1: only a confirmed e-mail signs in. Tests confirm it in the database; the e-mailed link is tested in PasswordResetTests.</summary>
    public Task ConfirmEmailAsync(string email) => ConfirmEmailAsync(this, email);

    public static async Task ConfirmEmailAsync(WebApplicationFactory<Program> factory, string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var normalized = email.ToUpperInvariant();
        await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Users.Where(u => u.NormalizedEmail == normalized)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
    }

    /// <summary>A new token for an existing account (claims are read at sign-in), its e-mail confirmed first. With <paramref name="twoFactorKey"/>,
    /// the sign-in passes the authenticator code too (R11 B-Med6).</summary>
    public async Task<Owner> SignInAsync(string email, string? twoFactorKey = null)
    {
        await ConfirmEmailAsync(email);
        var login = await CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = Password, twoFactorCode = twoFactorKey is null ? null : Totp.Code(twoFactorKey) });
        login.EnsureSuccessStatusCode();
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Json()).GetProperty("accessToken").GetString());
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        return new Owner(client, me.GetProperty("tenantId").GetGuid(), email);
    }

    public const string Password = "Carniceria2026";

    /// <summary>Abastalia staff once the account exists and <c>grant-platform-admin</c> ran (TK-189).</summary>
    public const string StaffEmail = "staff@abastalia.example";

    /// <summary>Abastalia staff who get the accompaniment requests (TK-233).</summary>
    public const string SupportEmail = "soporte@abastalia.example";

    /// <summary>In the old <c>Platform:Admins</c> setting, which no longer grants anything (R8 #1).</summary>
    public const string LegacyAdminEmail = "legacy-admin@abastalia.example";

    private Owner? _staff;

    /// <summary>The platform admin of this app (TK-189): registered, granted with the CLI command, with 2FA on and signed in again with the
    /// authenticator code (R11 B-Med6), on first use.</summary>
    public async Task<Owner> StaffAsync()
    {
        if (_staff is not null) return _staff;
        var account = await OwnerAsync(StaffEmail);
        if (await Abastalia.Host.Cloud.Platform.PlatformCli.RunAsync(Services, Abastalia.Host.Cloud.Platform.PlatformCli.Grant, StaffEmail) != 0)
            throw new InvalidOperationException("grant-platform-admin failed");
        return _staff = await SignInAsync(StaffEmail, await EnableTwoFactorAsync(account));
    }

    /// <summary>Turns on 2FA as the panel's Seguridad page does (Identity's /manage/2fa) and returns the authenticator key.</summary>
    public static async Task<string> EnableTwoFactorAsync(Owner who)
    {
        var key = (await (await who.Client.PostAsJsonAsync("/api/auth/manage/2fa", new { })).Json()).GetProperty("sharedKey").GetString()!;
        (await who.Client.PostAsJsonAsync("/api/auth/manage/2fa", new { enable = true, twoFactorCode = Totp.Code(key) })).EnsureSuccessStatusCode();
        return key;
    }

    /// <summary>A store for the owner with its main PC registered as a node.</summary>
    public async Task<StoreNode> StoreWithNodeAsync(Owner owner, string name = "Carnicería La Esquina")
    {
        var store = (await (await owner.Client.PostAsJsonAsync("/api/stores", new { name })).Json()).GetProperty("id").GetGuid();
        var code = (await (await owner.Client.PostAsync($"/api/stores/{store}/connection-codes", null)).Json()).GetProperty("code").GetString();
        var (nodeId, node) = await RegisterNodeAsync(this, Clock, code);
        return new StoreNode(store, nodeId, node);
    }

    /// <summary>Registers a node with its own key (TK-241) and returns a client that signs every call as the store's CloudClient does.</summary>
    public static async Task<(Guid NodeId, HttpClient Client)> RegisterNodeAsync(WebApplicationFactory<Program> factory, TimeProvider clock, string? code)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var r = await factory.CreateClient().PostAsJsonAsync("/api/nodes/register",
            new { code, nodeName = "PC principal", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        r.EnsureSuccessStatusCode();
        var nodeId = (await r.Json()).GetProperty("nodeId").GetGuid();
        return (nodeId, factory.CreateDefaultClient(new NodeSigner(nodeId, key, clock)));
    }

    /// <summary>Set on a request to sign it as an older store did: no body-hash header (R12 #1).</summary>
    public static readonly HttpRequestOptionsKey<bool> LegacySignature = new("LegacySignature");

    /// <summary>Set on a request to send another body than the one signed (R12 #1).</summary>
    public static readonly HttpRequestOptionsKey<byte[]> SwapBodyAfterSigning = new("SwapBodyAfterSigning");

    private sealed class NodeSigner(Guid nodeId, ECDsa key, TimeProvider clock) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = [];
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken);
                body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (request.Content.Headers.ContentEncoding.Contains("gzip")) // signed as the cloud reads it, like the store does
                {
                    using var inflated = new MemoryStream();
                    using (var z = new System.IO.Compression.GZipStream(new MemoryStream(body), System.IO.Compression.CompressionMode.Decompress))
                        await z.CopyToAsync(inflated, cancellationToken);
                    body = inflated.ToArray();
                }
            }
            var seconds = clock.GetUtcNow().ToUnixTimeSeconds();
            var nonce = NodeSignature.Base64Url(RandomNumberGenerator.GetBytes(16));
            var payload = NodeSignature.Payload(nodeId, seconds, nonce, request.Method.Method, request.RequestUri!.PathAndQuery, NodeSignature.BodyHash(body));
            var signature = NodeSignature.Base64Url(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));
            request.Headers.Authorization = new AuthenticationHeaderValue("Node", $"{nodeId}.{seconds}.{nonce}.{signature}");
            if (!request.Options.TryGetValue(LegacySignature, out var legacy) || !legacy)
                request.Headers.Add(NodeSignature.BodyHashHeader, NodeSignature.BodyHash(body)); // R12 #1: as the store sends it
            if (request.Options.TryGetValue(SwapBodyAfterSigning, out var swapped)) request.Content = new ByteArrayContent(swapped);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_file); }
        catch (IOException) { } // best effort: a temp file left behind is harmless
        try { if (Directory.Exists(BackupFolder)) Directory.Delete(BackupFolder, recursive: true); }
        catch (IOException) { }
        try { if (Directory.Exists(ArchiveFolder)) Directory.Delete(ArchiveFolder, recursive: true); }
        catch (IOException) { }
    }
}

public sealed record Owner(HttpClient Client, Guid TenantId, string Email);

/// <summary>The code an authenticator app shows (RFC 6238: HMAC-SHA1, 30 s, 6 digits) for Identity's Base32 key. Identity checks it against the real clock.</summary>
public static class Totp
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Used = new();

    /// <summary>R12 #4: the cloud takes each step's code once per account, so a second code inside the same 30 s is the next step's (the cloud
    /// accepts up to two steps ahead), as an authenticator app shows after the wait.</summary>
    public static string Code(string base32Key)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        return CodeAt(base32Key, Used.AddOrUpdate(base32Key, now, (_, last) => Math.Max(now, last + 1)));
    }

    /// <summary>The code given last for this key again (a replay).</summary>
    public static string Again(string base32Key) => CodeAt(base32Key, Used[base32Key]);

    private static string CodeAt(string base32Key, long counter)
    {
        var bits = string.Concat(base32Key.ToUpperInvariant().Where(c => c != ' ' && c != '=')
            .Select(c => Convert.ToString("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var step = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(step);
#pragma warning disable CA5350 // RFC 6238 is HMAC-SHA1, as every authenticator app
        var hash = HMACSHA1.HashData(key, step);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("000000", System.Globalization.CultureInfo.InvariantCulture);
    }
}

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
