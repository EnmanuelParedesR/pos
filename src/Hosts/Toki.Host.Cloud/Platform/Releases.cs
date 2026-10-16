using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Platform;

/// <summary>TK-254: a version Toki staff released on a channel, to a share of the stores (staged rollout). The files themselves stay in the
/// release feed (Updates:FeedUrl) with their signature and SHA-256; this only says which stores may install which version.</summary>
public sealed class AppRelease
{
    public Guid Id { get; set; }
    /// <summary>"pilot" or "stable".</summary>
    public string Channel { get; set; } = "";
    /// <summary>x.y.z, as in the feed (TokiSetup-x.y.z.exe).</summary>
    public string Version { get; set; } = "";
    /// <summary>0 to 100: the stores whose bucket (<see cref="Releases.Bucket"/>) is below it may install it. 0 pauses it.</summary>
    public int RolloutPercent { get; set; }
    public string? NotesUrl { get; set; }
    public DateTime PublishedUtc { get; set; }
    public DateTime ChangedUtc { get; set; }
    public string ChangedBy { get; set; } = "";
}

public sealed record ReleaseRequest(string? Channel, string? Version, int RolloutPercent, string? NotesUrl);
public sealed record ReleaseRow(Guid Id, string Channel, string Version, int RolloutPercent, string? NotesUrl, DateTime PublishedUtc, DateTime ChangedUtc, string ChangedBy);
/// <param name="Nodes">Store PCs whose last heartbeat reported this version.</param>
public sealed record FieldVersion(string Version, int Nodes, int Stores);
public sealed record ReleasesResponse(IReadOnlyList<ReleaseRow> Releases, IReadOnlyList<FieldVersion> Field);
public sealed record NodeUpdateRequest(string? Channel, string? Version);
/// <param name="Managed">The channel has releases in the console; false = the store follows its feed as before.</param>
/// <param name="AllowedVersion">The newest release this store is in; null = wait (none reaches it yet).</param>
public sealed record NodeUpdateResponse(bool Managed, string? AllowedVersion, string? NotesUrl);

internal static class Releases
{
    public static readonly string[] Channels = ["pilot", "stable"];

    public static void Configure(ModelBuilder builder) =>
        builder.Entity<AppRelease>(e =>
        {
            e.ToTable("AppReleases");
            e.HasKey(r => r.Id);
            e.Property(r => r.Channel).HasMaxLength(10);
            e.Property(r => r.Version).HasMaxLength(20);
            e.Property(r => r.NotesUrl).HasMaxLength(500);
            e.Property(r => r.ChangedBy).HasMaxLength(256);
            e.HasIndex(r => new { r.Channel, r.Version }).IsUnique();
        });

    /// <summary>The store's fixed place 0–99 in every rollout: the same stores go first each time (the pilot cohort stays the pilot cohort).</summary>
    public static int Bucket(Guid storeId) => (int)(BitConverter.ToUInt32(SHA256.HashData(storeId.ToByteArray()), 0) % 100);

    /// <summary>The newest release of the channel that reaches this store, or null.</summary>
    public static AppRelease? For(IEnumerable<AppRelease> channel, Guid storeId)
    {
        var bucket = Bucket(storeId);
        return channel.Where(r => bucket < r.RolloutPercent && System.Version.TryParse(r.Version, out _))
            .OrderByDescending(r => System.Version.Parse(r.Version)).FirstOrDefault();
    }

    private static bool IsVersion(string? v) => v is { Length: <= 20 } && System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{1,4}\.\d{1,4}\.\d{1,6}$");

    public static void MapNodeUpdates(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/nodes/updates", async (NodeUpdateRequest r, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            var channel = Channels.Contains(r.Channel) ? r.Channel! : "stable";
            var releases = await db.AppReleases.AsNoTracking().Where(x => x.Channel == channel).ToListAsync(ct);
            var release = For(releases, node.StoreId);
            return Results.Ok(new NodeUpdateResponse(releases.Count > 0, release?.Version, release?.NotesUrl));
        }).RequireAuthorization(CloudAuth.NodePolicy);

    /// <summary>Plataforma → Versiones. Each call is in the platform audit (who, path, when); the row also keeps who changed it last.</summary>
    public static void MapPlatformReleases(this RouteGroupBuilder group)
    {
        var platform = group.MapGroup("/releases");

        platform.MapGet("", async (CloudDbContext db, CancellationToken ct) =>
        {
            var releases = await db.AppReleases.AsNoTracking().OrderByDescending(r => r.PublishedUtc).Take(100)
                .Select(r => new ReleaseRow(r.Id, r.Channel, r.Version, r.RolloutPercent, r.NotesUrl, r.PublishedUtc, r.ChangedUtc, r.ChangedBy)).ToListAsync(ct);
            var field = (await db.NodeHeartbeats.AsNoTracking().Select(h => new { h.AppVersion, h.StoreId }).ToListAsync(ct))
                .GroupBy(h => string.IsNullOrEmpty(h.AppVersion) ? "?" : h.AppVersion)
                .Select(g => new FieldVersion(g.Key, g.Count(), g.Select(h => h.StoreId).Distinct().Count()))
                .OrderByDescending(v => System.Version.TryParse(v.Version, out var x) ? x : new System.Version()).ToList();
            return Results.Ok(new ReleasesResponse(releases, field));
        });

        // Publishes a version on a channel, or changes its rollout (0 pauses it; 100 reaches every store of the channel).
        platform.MapPut("", async (ReleaseRequest r, ClaimsPrincipal user, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var notes = string.IsNullOrWhiteSpace(r.NotesUrl) ? null : r.NotesUrl.Trim();
            if (!Channels.Contains(r.Channel) || !IsVersion(r.Version?.Trim()) || r.RolloutPercent is < 0 or > 100
                || notes is not null && (notes.Length > 500 || !Uri.TryCreate(notes, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
                return EndpointExtensions.Problem(Error.Validation("Platform.InvalidRelease",
                    "Indique el canal (piloto o estable), la versión como 1.4.0, el porcentaje de 0 a 100 y, si hay notas, un enlace https."));
            var version = r.Version!.Trim();
            var now = clock.GetUtcNow().UtcDateTime;
            var row = await db.AppReleases.SingleOrDefaultAsync(x => x.Channel == r.Channel && x.Version == version, ct);
            if (row is null) db.AppReleases.Add(row = new AppRelease { Id = Guid.CreateVersion7(), Channel = r.Channel!, Version = version, PublishedUtc = now });
            (row.RolloutPercent, row.NotesUrl, row.ChangedUtc) = (r.RolloutPercent, notes, now);
            row.ChangedBy = user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "";
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ReleaseRow(row.Id, row.Channel, row.Version, row.RolloutPercent, row.NotesUrl, row.PublishedUtc, row.ChangedUtc, row.ChangedBy));
        });
    }
}
