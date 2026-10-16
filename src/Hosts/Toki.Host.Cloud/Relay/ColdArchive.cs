using System.Security.Claims;
using System.Threading.Channels;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.Host.Cloud.Quotas;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Relay;

/// <summary>TK-207: write-once storage for archived e-CF XML (10 years, DGII). A key is written once and never changes.</summary>
public interface IColdArchive
{
    /// <summary>Stores <paramref name="content"/> under <paramref name="key"/>. A key that already exists is left as it is (a retry after a crash
    /// uploads the same bytes; Blob Storage's retention policy would refuse an overwrite anyway).</summary>
    Task PutAsync(string key, byte[] content, CancellationToken ct);

    /// <summary>The content, or null while it is being brought back from the archive tier (hours); the first call starts that.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken ct);
}

/// <summary>Development and tests: one file per key under a folder.</summary>
internal sealed class FileSystemColdArchive(string folder) : IColdArchive
{
    public async Task PutAsync(string key, byte[] content, CancellationToken ct)
    {
        var path = PathOf(key);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, content, ct);
        File.Move(temp, path, overwrite: false);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct) => await File.ReadAllBytesAsync(PathOf(key), ct);

    private string PathOf(string key) => Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Azure Blob Storage (deploy/azure/main.bicep): written as Cool; the lifecycle policy moves it to Archive after 180 days and deletes it
/// after 10 years; the container's retention policy keeps it from being changed or deleted before. Signs in with the managed identity.</summary>
internal sealed class BlobColdArchive(BlobContainerClient container) : IColdArchive
{
    public async Task PutAsync(string key, byte[] content, CancellationToken ct)
    {
        try
        {
            await container.GetBlobClient(key).UploadAsync(new BinaryData(content), new BlobUploadOptions
            {
                AccessTier = AccessTier.Cool,
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/gzip" },
            }, ct);
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412) { } // already there
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct)
    {
        var blob = container.GetBlobClient(key);
        var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
        if (properties.AccessTier == AccessTier.Archive.ToString())
        {
            // Back to Cool in up to 15 hours (standard priority, the cheap one); the lifecycle rule waits 7 days after a tier change.
            if (string.IsNullOrEmpty(properties.ArchiveStatus))
                await blob.SetAccessTierAsync(AccessTier.Cool, rehydratePriority: RehydratePriority.Standard, cancellationToken: ct);
            return null;
        }
        return (await blob.DownloadContentAsync(ct)).Value.Content.ToArray();
    }
}

/// <summary>Moves archived e-CF XML older than <c>Archive:HotDays</c> (90) out of SQL into <see cref="IColdArchive"/>. The row stays as the index
/// (e-NCF, status, DGII answer, <see cref="EcfRelayRecord.ColdKey"/>).</summary>
internal sealed class ColdArchiver(CloudDbContext db, IColdArchive store, TimeProvider clock, IConfiguration config)
{
    private const int Batch = 200;

    /// <returns>How many documents moved.</returns>
    public async Task<int> MoveAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now - TimeSpan.FromDays(config.GetValue("Archive:HotDays", 90));
        var moved = 0;
        while (true)
        {
            var rows = await db.EcfRelays.Where(r => r.ColdUtc == null && r.ReceivedUtc < cutoff).OrderBy(r => r.ReceivedUtc).Take(Batch).ToListAsync(ct);
            if (rows.Count == 0) return moved;
            foreach (var row in rows)
            {
                // Tenant first: one prefix per business, so a business's documents can be listed or handed over together. Then the environment
                // (R9 #4): the same e-NCF in TesteCF and eCF are two documents, and a key is written once.
                var key = $"{row.TenantId:N}/{row.Environment}/{row.ENcf}";
                await store.PutAsync(key + ".xml.gz", row.SignedXmlGzip, ct);
                if (row.RfceGzip is { } rfce) await store.PutAsync(key + ".rfce.xml.gz", rfce, ct);
                (row.ColdKey, row.ColdHasRfce, row.ColdUtc, row.SignedXmlGzip, row.RfceGzip) = (key, row.RfceGzip is not null, now, [], null);
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            moved += rows.Count;
        }
    }
}

/// <summary>Set by an archive upload: the database is awake then, so moving old documents costs no extra wake-up of serverless SQL.</summary>
public sealed class ColdArchiveSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}

/// <summary>Runs <see cref="ColdArchiver"/> at most once a day, after an archive upload.</summary>
internal sealed class ColdArchiveWorker(IServiceScopeFactory scopes, ColdArchiveSignal signal, TimeProvider clock, ILogger<ColdArchiveWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            await signal.WaitAsync(stoppingToken);
            if (clock.GetUtcNow() - last < TimeSpan.FromDays(1)) continue;
            last = clock.GetUtcNow();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var moved = await scope.ServiceProvider.GetRequiredService<ColdArchiver>().MoveAsync(stoppingToken);
                if (moved > 0) log.LogInformation("Moved {Count} archived e-CF to cold storage", moved);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Cold archive run failed"); }
        }
    }
}

/// <param name="Ready">False while a document in the archive tier is coming back (hours); the XML fields are then null.</param>
public sealed record ArchivedEcfResponse(string ENcf, EcfRelayStatus Status, string Environment, DateTime? SignedUtc, DateTime ReceivedUtc, string ReplacedByENcf,
    bool Cold, bool Ready, string? SignedXml, string? RfceXml);

internal static class ColdArchiveSetup
{
    /// <summary><c>Archive:Blob:ServiceUri</c> (+ <c>Archive:Blob:Container</c>) in Azure, else <c>Archive:Folder</c>; with neither, documents stay in SQL.</summary>
    public static void AddColdArchive(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ColdArchiveSignal>();
        if (config["Archive:Blob:ServiceUri"] is { Length: > 0 } uri)
            services.AddSingleton<IColdArchive>(new BlobColdArchive(
                new BlobServiceClient(new Uri(uri), new DefaultAzureCredential()).GetBlobContainerClient(config["Archive:Blob:Container"] ?? "ecf-archive")));
        else if (config["Archive:Folder"] is { Length: > 0 } folder)
            services.AddSingleton<IColdArchive>(new FileSystemColdArchive(folder));
        else
            return;
        services.AddScoped<ColdArchiver>();
        if (config.GetValue("Archive:MoveInBackground", true)) services.AddHostedService<ColdArchiveWorker>();
    }

    /// <summary>The owner or the accountant gets a document back by its e-NCF: from SQL, or from cold storage. The production one (eCF) unless
    /// <c>?environment=</c> asks for TesteCF or CerteCF; with no production one, the newest.</summary>
    public static void MapColdArchive(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/fiscal/archive/{encf}", async (string encf, string? environment, ClaimsPrincipal user, CloudDbContext db, IServiceProvider services,
            CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var key = encf.Trim().ToUpperInvariant();
            var wanted = EcfArchiveEndpoint.EnvironmentOf(environment);
            var rows = await db.EcfRelays.AsNoTracking().Where(r => r.TenantId == tenantId && r.ENcf == key && (wanted == null || r.Environment == wanted))
                .ToListAsync(ct);
            var row = rows.FirstOrDefault(r => r.Environment == "eCF") ?? rows.MaxBy(r => r.ReceivedUtc);
            if (row is null) return EndpointExtensions.Problem(Error.NotFound("Archive.NotFound", "No encontramos ese e-NCF en el archivo de la nube."));

            byte[]? xml = row.SignedXmlGzip, rfce = row.RfceGzip;
            if (row.ColdUtc is not null)
            {
                // TK-248: a read from cold storage costs (and from the archive tier, a rehydration); each try counts, also while it comes back.
                if (await services.GetRequiredService<IQuota>().TryConsumeAsync(tenantId, Meter.ArchiveRetrievals, 1, ct) is { Allowed: false } refused)
                    return refused.Problem();
                var store = services.GetRequiredService<IColdArchive>();
                xml = await store.ReadAsync(row.ColdKey + ".xml.gz", ct);
                rfce = row.ColdHasRfce && xml is not null ? await store.ReadAsync(row.ColdKey + ".rfce.xml.gz", ct) : null;
            }
            var ready = xml is not null && (rfce is not null || !row.ColdHasRfce);
            return Results.Ok(new ArchivedEcfResponse(row.ENcf, row.Status, row.Environment, row.SignedUtc, row.ReceivedUtc, row.ReplacedByENcf, row.ColdUtc is not null,
                ready, ready && xml is not null ? Fe.FeXml.Unzip(xml) : null, ready && rfce is not null ? Fe.FeXml.Unzip(rfce) : null));
        }).RequireAuthorization(CloudAuth.PanelPolicy).RequireRateLimiting(QuotaRateLimits.PanelUser);
}
