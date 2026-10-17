using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Assistant;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Quotas;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Backups;

/// <summary>TK-238: a copy of a store's backup, as the store encrypted it with the owner's passphrase (AES-256-GCM, the passphrase never leaves the
/// store): the cloud cannot read it. One row per file kept; the bytes are in <see cref="IStoreBackupStore"/>.</summary>
public sealed class StoreBackup
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid NodeId { get; set; }
    /// <summary>The store's own name for it ("abastalia-20261016-233000.abastalia-backup"), which says when it was made (store time).</summary>
    public string FileName { get; set; } = "";
    public string BlobKey { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UploadedUtc { get; set; }
    /// <summary>The last owner who downloaded it (user id, no e-mail) and when.</summary>
    public string? LastDownloadedBy { get; set; }
    public DateTime? LastDownloadedUtc { get; set; }
}

public sealed record StoreBackupResponse(Guid Id, string FileName, long Bytes, DateTime CreatedUtc, DateTime UploadedUtc);

/// <summary>Where the encrypted backups live: a private Blob container in Azure, a folder in development and tests.</summary>
public interface IStoreBackupStore
{
    Task PutAsync(string key, Stream content, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

internal sealed class FileSystemBackupStore(string folder) : IStoreBackupStore
{
    public async Task PutAsync(string key, Stream content, CancellationToken ct)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var file = File.Create(path + ".tmp")) await content.CopyToAsync(file, ct);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(File.OpenRead(PathOf(key)));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    private string PathOf(string key) => Path.Combine(folder, key.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Azure Blob Storage (deploy/azure/main.bicep, container <c>store-backups</c>): Hot, since most copies live a week (Cool charges 30 days
/// for each); the API's managed identity signs in, no keys, no public access. A lifecycle rule deletes anything older than 60 days as a safety net.</summary>
internal sealed class BlobBackupStore(BlobContainerClient container) : IStoreBackupStore
{
    public async Task PutAsync(string key, Stream content, CancellationToken ct) =>
        await container.GetBlobClient(key).UploadAsync(content, new BlobUploadOptions
        {
            AccessTier = AccessTier.Hot,
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
        }, ct);

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct) => await container.GetBlobClient(key).OpenReadAsync(cancellationToken: ct);

    public async Task DeleteAsync(string key, CancellationToken ct) => await container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);
}

/// <summary>TK-238: the store's nightly backup, copied here over the signed node channel (Sync's CloudBackupUploader), and the owner's way back to it
/// from the panel. Limits: <c>CloudBackup:MaxMegabytes</c> (1024) per file, <c>CloudBackup:UploadsPerHourPerNode</c> (4), the monthly meter
/// <see cref="Meter.CloudBackups"/> and the paid-extras check (<see cref="LicenseCheck"/>). Keeps per store the newest of each of the last
/// <c>CloudBackup:KeepDaily</c> (7) days with a backup and the newest of each of the <c>CloudBackup:KeepWeekly</c> (4) weeks before them.</summary>
internal static partial class StoreBackupEndpoints
{
    public const string NodeRateLimit = "backup-node";
    public const string FileMagic = "TOKIRSP2"; // Abastalia.Settings.BackupEncryption: anything else is not an encrypted backup

    public static long MaxBytes(IConfiguration config) => config.GetValue("CloudBackup:MaxMegabytes", 1024L) * 1024 * 1024;

    [GeneratedRegex(@"^abastalia-(\d{8}-\d{6})(-[0-9a-f]{4})?\.abastalia-backup$")]
    private static partial Regex Name();

    /// <summary>When the store made it, from its name (store time); null when the name is not one of the store's scheduled backups.</summary>
    public static DateTime? CreatedUtc(string fileName) =>
        Name().Match(fileName) is { Success: true } m
        && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local - BusinessDay.Offset, DateTimeKind.Utc) : null;

    /// <summary>The backups retention drops, newest first kept: the newest of each of the last <paramref name="daily"/> store days with a backup,
    /// then the newest of each of the <paramref name="weekly"/> weeks (Monday to Sunday) before those days.</summary>
    public static List<T> Expired<T>(IEnumerable<T> backups, Func<T, DateTime> createdUtc, int daily, int weekly)
    {
        var ordered = backups.OrderByDescending(createdUtc).ToList();
        var keep = ordered.GroupBy(b => BusinessDay.Of(createdUtc(b))).Take(daily).Select(g => g.First()).ToHashSet();
        var firstDaily = keep.Count == 0 ? DateOnly.MaxValue : keep.Min(b => BusinessDay.Of(createdUtc(b)));
        static DateOnly Monday(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        keep.UnionWith(ordered.Where(b => BusinessDay.Of(createdUtc(b)) < firstDaily).GroupBy(b => Monday(BusinessDay.Of(createdUtc(b))))
            .Take(weekly).Select(g => g.First()));
        return ordered.Where(b => !keep.Contains(b)).ToList();
    }

    public static void Configure(ModelBuilder builder) => builder.Entity<StoreBackup>(e =>
    {
        e.ToTable("StoreBackups");
        e.Property(b => b.FileName).HasMaxLength(100);
        e.Property(b => b.BlobKey).HasMaxLength(200);
        e.Property(b => b.LastDownloadedBy).HasMaxLength(450);
        e.HasIndex(b => new { b.TenantId, b.StoreId, b.FileName }).IsUnique();
    });

    /// <summary><c>CloudBackup:Blob:ServiceUri</c> (else the archive's <c>Archive:Blob:ServiceUri</c>) + <c>CloudBackup:Blob:Container</c>
    /// (<c>store-backups</c>) in Azure, else <c>CloudBackup:Folder</c>; with neither, the cloud does not offer backups (the upload answers 404).</summary>
    public static void AddStoreBackups(this IServiceCollection services, IConfiguration config)
    {
        if ((config["CloudBackup:Blob:ServiceUri"] ?? config["Archive:Blob:ServiceUri"]) is { Length: > 0 } uri)
            services.AddSingleton<IStoreBackupStore>(new BlobBackupStore(
                new BlobServiceClient(new Uri(uri), new DefaultAzureCredential()).GetBlobContainerClient(config["CloudBackup:Blob:Container"] ?? "store-backups")));
        else if (config["CloudBackup:Folder"] is { Length: > 0 } folder)
            services.AddSingleton<IStoreBackupStore>(new FileSystemBackupStore(folder));
    }

    public static void MapStoreBackups(this IEndpointRouteBuilder app)
    {
        var config = app.ServiceProvider.GetRequiredService<IConfiguration>();
        var max = MaxBytes(config);

        app.MapPost("/api/nodes/backups/{fileName}", async (string fileName, HttpRequest request, ClaimsPrincipal user, CloudDbContext db, IServiceProvider services,
            LicenseCheck license, IQuota quota, TimeProvider clock, ILoggerFactory logs, CancellationToken ct) =>
        {
            if (services.GetService<IStoreBackupStore>() is not { } store)
                return EndpointExtensions.Problem(Error.NotFound("Backup.CloudOff", "La nube no guarda respaldos todavía."));
            var node = NodeIdentity.From(user);
            if (CreatedUtc(fileName) is not { } created)
                return EndpointExtensions.Problem(Error.Validation("Backup.InvalidName", "Ese archivo no es un respaldo de Abastalia."));
            if (await db.StoreBackups.AnyAsync(b => b.TenantId == node.TenantId && b.StoreId == node.StoreId && b.FileName == fileName, ct))
                return Results.Ok(); // a retry after a lost answer: already kept

            // The node check buffered the whole body to hash it: its length and first bytes are known before anything is stored.
            var body = request.Body;
            if (!body.CanSeek) return EndpointExtensions.Problem(Error.Validation("Backup.InvalidFile", "El respaldo no llegó completo."));
            if (body.Length > max)
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Backup.TooLarge", extensions: new Dictionary<string, object?> { ["code"] = "Backup.TooLarge" },
                    detail: $"El respaldo pesa más de {max / 1024 / 1024:N0} MB, el máximo que guarda la nube. Sigue guardado en la PC y en la copia (USB o carpeta).");
            var magic = new byte[FileMagic.Length];
            if (await body.ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, ct) != magic.Length
                || !magic.AsSpan().SequenceEqual(System.Text.Encoding.ASCII.GetBytes(FileMagic)))
                return EndpointExtensions.Problem(Error.Validation("Backup.InvalidFile", "Ese archivo no es un respaldo cifrado de Abastalia."));
            body.Position = 0;

            if (!await license.AllowsPaidExtrasAsync(db, node.TenantId, ct))
                return EndpointExtensions.Problem(Error.Forbidden("Backup.NotLicensed",
                    "El respaldo en la nube necesita una licencia vigente. Los respaldos siguen en la PC y en la copia (USB o carpeta)."));
            if (await quota.TryConsumeAsync(node.TenantId, Meter.CloudBackups, 1, ct) is { Allowed: false } refused) return refused.Problem();

            var row = new StoreBackup
            {
                Id = Guid.CreateVersion7(), TenantId = node.TenantId, StoreId = node.StoreId, NodeId = node.NodeId, FileName = fileName,
                BlobKey = $"{node.TenantId:N}/{node.StoreId:N}/{fileName}", Bytes = body.Length, CreatedUtc = created, UploadedUtc = clock.GetUtcNow().UtcDateTime,
            };
            await store.PutAsync(row.BlobKey, body, ct);
            db.StoreBackups.Add(row);
            await db.SaveChangesAsync(ct);

            // Retention, per store, right after a new one is kept.
            var all = await db.StoreBackups.Where(b => b.TenantId == node.TenantId && b.StoreId == node.StoreId).ToListAsync(ct);
            foreach (var old in Expired(all, b => b.CreatedUtc, config.GetValue("CloudBackup:KeepDaily", 7), config.GetValue("CloudBackup:KeepWeekly", 4)))
            {
                await store.DeleteAsync(old.BlobKey, ct);
                db.StoreBackups.Remove(old);
            }
            await db.SaveChangesAsync(ct);
            logs.CreateLogger("Abastalia.Backups").LogInformation("Kept backup {BackupId} of store {StoreId} ({Bytes} bytes) from node {NodeId}",
                row.Id, row.StoreId, row.Bytes, row.NodeId);
            return Results.Ok();
        }).RequireAuthorization(CloudAuth.NodePolicy).RequireRateLimiting(NodeRateLimit).WithMetadata(new RequestSizeLimitAttribute(max + 1));

        // Owners only: a backup is the whole store, readable by whoever has the passphrase.
        var owner = app.MapGroup("/api/stores/{storeId:guid}/backups").RequireAuthorization(CloudAuth.PanelPolicy).RequireAuthorization(CloudAuth.OwnerPolicy)
            .RequireRateLimiting(QuotaRateLimits.PanelUser);
        owner.MapGet("", async (Guid storeId, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            if (!await db.Stores.AnyAsync(s => s.Id == storeId && s.TenantId == tenantId, ct))
                return EndpointExtensions.Problem(Error.NotFound("Stores.NotFound", "No encontramos esa tienda."));
            var rows = await db.StoreBackups.AsNoTracking().Where(b => b.TenantId == tenantId && b.StoreId == storeId).ToListAsync(ct);
            return Results.Ok(rows.OrderByDescending(b => b.CreatedUtc).Select(b => new StoreBackupResponse(b.Id, b.FileName, b.Bytes, b.CreatedUtc, b.UploadedUtc)));
        });
        owner.MapGet("/{id:guid}", async (Guid storeId, Guid id, ClaimsPrincipal user, CloudDbContext db, IServiceProvider services, TimeProvider clock,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var row = await db.StoreBackups.SingleOrDefaultAsync(b => b.Id == id && b.TenantId == tenantId && b.StoreId == storeId, ct);
            if (row is null || services.GetService<IStoreBackupStore>() is not { } store)
                return EndpointExtensions.Problem(Error.NotFound("Backup.NotFound", "Ese respaldo ya no está en la nube."));
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            (row.LastDownloadedBy, row.LastDownloadedUtc) = (userId, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            logs.CreateLogger("Abastalia.Backups").LogInformation("Backup {BackupId} of store {StoreId} downloaded by user {UserId}", row.Id, row.StoreId, userId);
            return Results.Stream(await store.OpenReadAsync(row.BlobKey, ct), "application/octet-stream", row.FileName);
        });
    }
}
