using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks.Persistence;

namespace Abastalia.Sync;

/// <param name="Accepted">Events the cloud confirmed in this pass.</param>
/// <param name="Error">Why the pass stopped early, or null.</param>
internal sealed record UploadPass(int Accepted, string? Error);

/// <summary>Uploads every outbox event not yet confirmed by the cloud (<c>UploadedUtc</c> empty), in <c>Sequence</c> order, in gzip batches of 200,
/// and marks <c>UploadedUtc</c> only for the ids the cloud answers in <c>accepted</c>. Nothing is ever dropped: what is not accepted goes again next pass.
/// It never touches <c>ProcessedUtc</c> (the in-process handlers), and nothing deletes outbox rows.</summary>
internal sealed class CloudUploader(SyncDbContext db, InfraDbContext infra, CloudClient cloud, TimeProvider clock, ILogger<CloudUploader> log)
{
    public const int BatchSize = 200;
    public const string IngestPath = "/ingest/v1/events";

    /// <summary>One pass over everything pending, each event at most once (a watermark), so one the cloud keeps refusing cannot spin the pass.
    /// Does nothing when the store is not connected. A failure is saved as <c>LastError</c>, not thrown.</summary>
    public async Task<UploadPass> UploadAsync(CancellationToken ct)
    {
        var connection = await cloud.ConnectionAsync(ct);
        if (connection is null) return new UploadPass(0, null);

        long after = 0;
        var accepted = 0;
        try
        {
            while (true)
            {
                var batch = await infra.Outbox.AsNoTracking().Where(m => m.UploadedUtc == null && m.Sequence > after)
                    .OrderBy(m => m.Sequence).Take(BatchSize).ToListAsync(ct);
                if (batch.Count == 0) break;

                using var response = await cloud.SendAsync(connection, IngestPath, Gzip(batch), ct);
                if (!response.IsSuccessStatusCode) throw new CloudRefused($"{RefusedPrefix}{(int)response.StatusCode} al subir los datos.");
                var confirmed = (await response.Content.ReadFromJsonAsync<IngestResponse>(ct))?.Accepted ?? [];
                var ids = batch.Select(m => m.Id).Intersect(confirmed).ToList();
                var now = clock.GetUtcNow().UtcDateTime;
                if (ids.Count > 0)
                    await infra.Outbox.Where(m => ids.Contains(m.Id) && m.UploadedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(m => m.UploadedUtc, now), ct);
                // Not tracked: if a disconnect deleted the row in the meantime, this updates nothing.
                await db.Connections.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUploadUtc, now).SetProperty(c => c.LastError, (string?)null), ct);
                accepted += ids.Count;

                if (batch.Count < BatchSize) break;
                after = batch[^1].Sequence;
            }
            return new UploadPass(accepted, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var error = ex is CloudRefused or InvalidOperationException ? ex.Message : $"{UnreachablePrefix}: {ex.Message}";
            if (error.Length > 2000) error = error[..2000];
            log.LogWarning(ex, "Could not upload events to the cloud");
            await db.Connections.ExecuteUpdateAsync(s => s.SetProperty(c => c.LastError, error), ct);
            return new UploadPass(accepted, error);
        }
    }

    /// <summary>1, 2, 4… seconds up to 15 minutes, each between half and all of it (jitter), so many stores coming back online do not hit the cloud at once.</summary>
    public static TimeSpan RetryDelay(int failures)
    {
        var seconds = Math.Min(900, Math.Pow(2, Math.Clamp(failures, 1, 11) - 1));
        return TimeSpan.FromSeconds(seconds * (500 + RandomNumberGenerator.GetInt32(501)) / 1000);
    }

    /// <summary>The kind of a saved <c>LastError</c> for the heartbeat ("http_503", "unreachable", "other"): never its text, which can carry a URL.</summary>
    public static string? ErrorCode(string? error) => error switch
    {
        null => null,
        _ when error.StartsWith(RefusedPrefix, StringComparison.Ordinal) => "http_" + new string([.. error[RefusedPrefix.Length..].TakeWhile(char.IsAsciiDigit)]),
        _ when error.StartsWith(UnreachablePrefix, StringComparison.Ordinal) => "unreachable",
        _ => "other",
    };

    private const string RefusedPrefix = "La nube respondió ";
    private const string UnreachablePrefix = "No se pudo conectar con la nube";

    // { events: [{ id, type, aggregateId, occurredUtc, payload }] }; the payload goes as the event's own JSON, not as a string.
    private static ByteArrayContent Gzip(List<OutboxMessage> batch)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest))
        using (var json = new Utf8JsonWriter(gzip))
        {
            json.WriteStartObject();
            json.WriteStartArray("events");
            foreach (var m in batch)
            {
                json.WriteStartObject();
                json.WriteString("id", m.Id);
                json.WriteString("type", m.Type);
                json.WriteString("aggregateId", m.AggregateId);
                json.WriteString("occurredUtc", m.OccurredUtc);
                json.WritePropertyName("payload");
                json.WriteRawValue(m.Payload);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    private sealed record IngestResponse(List<Guid>? Accepted);

    private sealed class CloudRefused(string message) : Exception(message);
}

/// <summary>Runs <see cref="CloudUploader"/> every <c>Sync:IntervalSeconds</c> (5) unless <c>Sync:Enabled</c> is false (the in-memory test host,
/// whose tests run it themselves). Idle while the store is not connected. After a failure it waits <see cref="CloudUploader.RetryDelay"/> instead.</summary>
internal sealed class CloudUploaderWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<CloudUploaderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Sync:Enabled", true)) return;
        var interval = TimeSpan.FromSeconds(config.GetValue("Sync:IntervalSeconds", 5));
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var pass = await scope.ServiceProvider.GetRequiredService<CloudUploader>().UploadAsync(stoppingToken);
                failures = pass.Error is null ? 0 : failures + 1;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                failures++;
                log.LogError(ex, "The cloud uploader failed");
            }
            await Task.Delay(failures == 0 ? interval : CloudUploader.RetryDelay(failures), clock, stoppingToken);
        }
    }
}
