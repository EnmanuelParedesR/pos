using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Abastalia.BuildingBlocks;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.SharedKernel;
using Abastalia.Sync.Contracts;

namespace Abastalia.Sync;

/// <summary>Connects the store to the cloud and uploads the outbox (ADR-0008, arquitectura-v1-vsa §5).</summary>
public static class SyncModule
{
    public static IServiceCollection AddSyncModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<SyncDbContext>();
        // 60 s: reading a supplier invoice with the AI (TK-224) can take longer than an upload.
        services.AddHttpClient(CloudClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<CloudBackoff>();
        services.AddScoped<CloudClient>();
        services.AddScoped<ICloudClient>(sp => sp.GetRequiredService<CloudClient>());
        services.AddScoped<CloudUploader>();
        services.AddSingleton<LicenseSignal>();
        services.AddHostedService<CloudUploaderWorker>();
        services.AddHostedService<HeartbeatWorker>();
        services.AddScoped<CloudBackupUploader>();
        services.AddHostedService<CloudBackupWorker>(); // TK-238
        services.AddScoped<TaliaProposals>(); // TK-343
        services.AddScoped<IAssistantProposals>(sp => sp.GetRequiredService<TaliaProposals>());
        services.AddHealthChecks().AddCheck<SyncHealthCheck>("sync");
        return services;
    }
}

/// <summary>Single row (Id 1) while the store is connected; disconnecting deletes it.</summary>
internal sealed class CloudConnection
{
    public int Id { get; set; } = 1;
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid NodeId { get; set; }
    /// <summary>The node's credential, protected for this machine with ASP.NET Core Data Protection (the key ring Settings sets up): its private key
    /// (<c>ecdsa:</c> + PKCS#8 in Base64, TK-241; the cloud keeps the public key). The column keeps its first name.</summary>
    public string NodeSecret { get; set; } = "";
    public string CloudUrl { get; set; } = "";
    public DateTime ConnectedUtc { get; set; }
    public DateTime? LastUploadUtc { get; set; }
    public string? LastError { get; set; }
}

internal sealed class SyncDbContext(DbContextOptions<SyncDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "sync";
    public DbSet<CloudConnection> Connections => Set<CloudConnection>();
    public DbSet<TaliaProposal> TaliaProposals => Set<TaliaProposal>();
}

internal sealed class CloudConnectionConfiguration : IEntityTypeConfiguration<CloudConnection>
{
    public void Configure(EntityTypeBuilder<CloudConnection> b)
    {
        b.ToTable("CloudConnection");
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.NodeSecret).HasMaxLength(1000);
        b.Property(c => c.CloudUrl).HasMaxLength(500);
        b.Property(c => c.LastError).HasMaxLength(2000);
    }
}

internal static class SyncErrors
{
    public static readonly Error InvalidCode = Error.Validation("Sync.InvalidCode", "El código no es válido o ya venció. Pida uno nuevo en el panel de Abastalia.");
    public static readonly Error CloudUnreachable = Error.Conflict("Sync.CloudUnreachable", "No se pudo conectar con la nube. Revise el internet e intente de nuevo.");
    public static readonly Error AlreadyConnected = Error.Conflict("Sync.AlreadyConnected", "Esta tienda ya está conectada a la nube. Desconéctela antes de usar otro código.");
    public static readonly Error NotConfigured = Error.Conflict("Sync.NotConfigured", "Esta PC no tiene configurada la dirección de la nube.");
}

/// <summary>Calls the cloud as this node. Implements <see cref="ICloudClient"/> for other modules (the e-CF relay).</summary>
internal sealed class CloudClient(SyncDbContext db, IHttpClientFactory http, IDataProtectionProvider protection, CloudBackoff backoff, TimeProvider clock) : ICloudClient
{
    public const string HttpClientName = "Abastalia.Cloud";
    public const string KeyPrefix = "ecdsa:";
    public const string BodyHashHeader = "X-Body-Sha256"; // the cloud's NodeSignature.BodyHashHeader

    /// <summary>The cloud's clock minus this PC's, from the last answer's Date header: a shop PC whose clock is off still signs inside the window.</summary>
    private static TimeSpan _skew;

    private IDataProtector Protector => protection.CreateProtector("Abastalia.Sync.NodeSecret");

    public string Protect(string secret) => Protector.Protect(secret);

    public Task<CloudConnection?> ConnectionAsync(CancellationToken ct) => db.Connections.AsNoTracking().SingleOrDefaultAsync(ct);

    public async Task<HttpResponseMessage> PostAsJsonAsync<T>(string path, T body, CancellationToken ct) =>
        await SendAsync(await ConnectionAsync(ct) ?? throw new InvalidOperationException("La tienda no está conectada a la nube."), path, JsonContent.Create(body), ct);

    public async Task<HttpResponseMessage> SendAsync(CloudConnection connection, string path, HttpContent content, CancellationToken ct)
    {
        await content.LoadIntoBufferAsync(ct);
        var body = await content.ReadAsByteArrayAsync(ct);
        // TK-251: the cloud decompresses a gzip batch before it checks the signature, so the signature covers the JSON, not its gzip.
        if (content.Headers.ContentEncoding.Contains("gzip"))
        {
            using var inflated = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(new MemoryStream(body), System.IO.Compression.CompressionMode.Decompress)) await gzip.CopyToAsync(inflated, ct);
            body = inflated.ToArray();
        }
        return await SendSignedAsync(connection, path, content, SHA256.HashData(body), http.CreateClient(HttpClientName), ct);
    }

    /// <summary>TK-238: streams a file (an encrypted backup of hundreds of MB) without loading it in memory: hashed first, then sent. No client
    /// timeout (a slow shop line takes long); <paramref name="ct"/> bounds it.</summary>
    public async Task<HttpResponseMessage> SendFileAsync(CloudConnection connection, string path, string file, CancellationToken ct)
    {
        byte[] hash;
        await using (var read = File.OpenRead(file)) hash = await SHA256.HashDataAsync(read, ct);
        await using var stream = File.OpenRead(file);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = stream.Length;
        var client = http.CreateClient(HttpClientName);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return await SendSignedAsync(connection, path, content, hash, client, ct);
    }

    private async Task<HttpResponseMessage> SendSignedAsync(CloudConnection connection, string path, HttpContent content, byte[] bodyHash, HttpClient client,
        CancellationToken ct)
    {
        if (backoff.Remaining(path) is { } wait) return CloudBackoff.TooManyRequests(wait);
        string secret;
        try { secret = Protector.Unprotect(connection.NodeSecret); }
        catch (CryptographicException e)
        {
            throw new InvalidOperationException("La clave de la nube no se puede leer en esta PC: desconecte la tienda y conéctela con un código nuevo.", e);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.CloudUrl + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Node", Sign(connection.NodeId, Convert.FromBase64String(secret[KeyPrefix.Length..]), "POST", path, bodyHash));
        // R12 #1: the signed hash in clear, so the cloud checks the signature before it reads the body (required for a backup).
        request.Headers.Add(BodyHashHeader, Base64Url(bodyHash));
        var response = await client.SendAsync(request, ct);
        if (response.Headers.Date is { } cloudNow) _skew = cloudNow - clock.GetUtcNow();
        backoff.Record(path, response);
        return response;
    }

    /// <summary>TK-241 (ADR-0008): <c>nodeId.unixSeconds.nonce.signature</c>, the signature over <c>nodeId.unixSeconds.nonce.METHOD /path bodyHash</c>
    /// (ECDSA P-256, SHA-256), bodyHash = Base64Url(SHA-256(body)) (R10 #7). Same text as the cloud's NodeSignature.</summary>
    internal string Sign(Guid nodeId, byte[] pkcs8, string method, string path, byte[] bodyHash)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        var seconds = (clock.GetUtcNow() + _skew).ToUnixTimeSeconds();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
        var signature = key.SignData(Encoding.UTF8.GetBytes($"{nodeId}.{seconds}.{nonce}.{method} {path} {Base64Url(bodyHash)}"), HashAlgorithmName.SHA256);
        return $"{nodeId}.{seconds}.{nonce}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>TK-248: a 429 with <c>Retry-After</c> from the cloud, per path. Until then <see cref="CloudClient"/> does not call that path again and answers
/// the caller a 429 itself, so no upload (outbox, e-CF archive), poll or retry hammers a cloud that asked to wait. A 429 without the header (a
/// month quota, <c>Quota.Exceeded</c>) is the caller's to read and is not remembered.</summary>
internal sealed class CloudBackoff(TimeProvider clock)
{
    /// <summary>The longest wait honored: a wrong header never parks the uploads for a day.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromHours(1);

    /// <summary>R12 #9: the shortest, so a Retry-After of 0 or a date in the past still waits a moment.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(1);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);

    public TimeSpan? Remaining(string path) =>
        _until.TryGetValue(path, out var until) && until - clock.GetUtcNow() is var left && left > TimeSpan.Zero ? left : null;

    public void Record(string path, HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests && RetryAfter(response, clock.GetUtcNow()) is { } wait) _until[path] = clock.GetUtcNow() + wait;
    }

    /// <summary>The <c>Retry-After</c> of the answer (seconds or a date), at most <see cref="Longest"/>; null without one.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now) =>
        (response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - now) is { } wait
            ? TimeSpan.FromTicks(Math.Clamp(wait.Ticks, Shortest.Ticks, Longest.Ticks)) : null;

    public static HttpResponseMessage TooManyRequests(TimeSpan wait)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new ByteArrayContent([]) };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }
}

public sealed record SyncStatus(bool Connected, Guid? TenantId, Guid? StoreId, string? CloudUrl, DateTime? LastUploadUtc, int PendingEvents, string? LastError);

internal static class GetSyncStatus
{
    public static async Task<SyncStatus> RunAsync(SyncDbContext db, InfraDbContext infra, CancellationToken ct)
    {
        var c = await db.Connections.AsNoTracking().SingleOrDefaultAsync(ct);
        var pending = await infra.Outbox.CountAsync(m => m.UploadedUtc == null, ct);
        return new SyncStatus(c is not null, c?.TenantId, c?.StoreId, c?.CloudUrl, c?.LastUploadUtc, pending, c?.LastError);
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/sync/status", async (SyncDbContext db, InfraDbContext infra, CancellationToken ct) => Results.Ok(await RunAsync(db, infra, ct)))
                .RequirePermission(Permissions.SettingsManage).WithTags("Sync");
    }
}

/// <summary>The owner types the connection code from the panel; the cloud registers this PC as a node and answers its identity (ADR-0008).
/// The cloud call runs outside any transaction; only saving the answer is a command.</summary>
internal static class ConnectToCloud
{
    public sealed record Request(string? Code);

    internal sealed record Registration(Guid TenantId, Guid StoreId, Guid NodeId);

    /// <param name="Credential">What <see cref="CloudConnection.NodeSecret"/> keeps (protected by the handler).</param>
    internal sealed record Command(string CloudUrl, Registration Registration, string Credential) : ICommand<Ok>;

    public static async Task<Result<SyncStatus>> RunAsync(string? code, string? cloudUrl, SyncDbContext db, InfraDbContext infra, IHttpClientFactory http,
        ICommandHandler<Command, Ok> save, CancellationToken ct)
    {
        code = code?.Trim();
        if (code is not { Length: 8 } || !code.All(char.IsAsciiDigit)) return SyncErrors.InvalidCode;
        if (string.IsNullOrWhiteSpace(cloudUrl)) return SyncErrors.NotConfigured;
        if (await db.Connections.AnyAsync(ct)) return SyncErrors.AlreadyConnected;

        cloudUrl = cloudUrl.Trim().TrimEnd('/');
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); // the node key (TK-241): the private half never leaves this PC
        Registration? registration;
        try
        {
            using var body = JsonContent.Create(new { code, nodeName = Environment.MachineName, publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
            await body.LoadIntoBufferAsync(ct); // TK-307: with its length, not chunked: the cloud's node gate answers 411 otherwise (R12 #1)
            using var response = await http.CreateClient(CloudClient.HttpClientName).PostAsync($"{cloudUrl}/api/nodes/register", body, ct);
            // 400/404 per ADR-0008; 409/410 too, in case the cloud tells a used or expired code apart.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone) return SyncErrors.InvalidCode;
            if (!response.IsSuccessStatusCode) return SyncErrors.CloudUnreachable;
            registration = await response.Content.ReadFromJsonAsync<Registration>(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException && !ct.IsCancellationRequested)
        {
            return SyncErrors.CloudUnreachable;
        }
        if (registration is null || registration.NodeId == Guid.Empty) return SyncErrors.CloudUnreachable;
        // TK-241: the cloud keeps our public key; we keep the private one.
        var credential = CloudClient.KeyPrefix + Convert.ToBase64String(key.ExportPkcs8PrivateKey());

        var saved = await save.Handle(new Command(cloudUrl, registration, credential), ct);
        if (saved.IsFailure) return saved.Error!;
        return await GetSyncStatus.RunAsync(db, infra, ct);
    }

    /// <remarks>The snapshot goes again on every connect (after a disconnect too): the cloud replaces names by id, so repeating it is harmless.</remarks>
    internal sealed class Handler(SyncDbContext db, CloudClient cloud, IEnumerable<ICloudSnapshot> snapshots, IAuditLog audit, TimeProvider clock)
        : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            if (await db.Connections.AnyAsync(ct)) return SyncErrors.AlreadyConnected;
            var r = c.Registration;
            db.Connections.Add(new CloudConnection
            {
                TenantId = r.TenantId, StoreId = r.StoreId, NodeId = r.NodeId, NodeSecret = cloud.Protect(c.Credential), CloudUrl = c.CloudUrl,
                ConnectedUtc = clock.GetUtcNow().UtcDateTime,
            });
            foreach (var snapshot in snapshots) await snapshot.EnqueueAsync(ct);
            await audit.RecordAsync("sync.connect", "CloudConnection", r.NodeId, new { r.TenantId, r.StoreId, r.NodeId, c.CloudUrl }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/sync/connect", async (Request r, IConfiguration config, SyncDbContext db, InfraDbContext infra, IHttpClientFactory http,
                    ICommandHandler<Command, Ok> save, CancellationToken ct) => (await RunAsync(r.Code, config["Cloud:Url"], db, infra, http, save, ct)).ToHttp())
                .RequirePermission(Permissions.SettingsManage).WithTags("Sync");
    }
}

/// <summary>Stops uploading. Pending events stay in the outbox and go up when the store connects again.</summary>
/// <remarks>TK-241: the cloud is told first (the node is revoked there), outside the transaction. Without internet the store disconnects anyway;
/// the node then stays listed in the panel's "Tiendas", where an Owner removes it.</remarks>
internal static class DisconnectFromCloud
{
    internal sealed record Command : ICommand<Ok>;

    public static async Task TellCloudAsync(CloudClient cloud, CancellationToken ct)
    {
        if (await cloud.ConnectionAsync(ct) is not { } connection) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var _ = await cloud.SendAsync(connection, "/api/nodes/revoke", new StringContent(""), timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException && !ct.IsCancellationRequested) { }
    }

    internal sealed class Handler(SyncDbContext db, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var connection = await db.Connections.SingleOrDefaultAsync(ct);
            if (connection is null) return Ok.Value;
            db.Connections.Remove(connection);
            await audit.RecordAsync("sync.disconnect", "CloudConnection", connection.NodeId, new { connection.TenantId, connection.StoreId, connection.NodeId }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/sync/disconnect", async (CloudClient cloud, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                {
                    await TellCloudAsync(cloud, ct);
                    return (await h.Handle(new Command(), ct)).ToHttp(_ => Results.NoContent());
                })
                .RequirePermission(Permissions.SettingsManage).WithTags("Sync");
    }
}

/// <summary>Degraded when the store is connected and an event has waited more than an hour to go up.</summary>
internal sealed class SyncHealthCheck(SyncDbContext db, InfraDbContext infra, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (connection is null) return HealthCheckResult.Healthy("La tienda no está conectada a la nube.");
        var limit = clock.GetUtcNow().UtcDateTime.AddHours(-1);
        if (!await infra.Outbox.AnyAsync(m => m.UploadedUtc == null && m.OccurredUtc < limit, cancellationToken)) return HealthCheckResult.Healthy();
        return HealthCheckResult.Degraded("Hay datos de la tienda sin subir a la nube desde hace más de una hora. Revise el internet."
            + (connection.LastError is { } error ? $" Último error: {error}" : ""));
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class SyncDesignTimeFactory : DesignTimeFactory<SyncDbContext>;
