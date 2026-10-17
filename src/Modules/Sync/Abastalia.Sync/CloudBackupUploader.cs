using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Settings.Contracts;

namespace Abastalia.Sync;

/// <summary>TK-238: copies the newest scheduled backup to the cloud, as the file on disk (already encrypted with the owner's passphrase, which never
/// leaves the PC), over the signed node channel. Only with the license feature <c>CloudBackup</c>, the store connected and the copy turned on in
/// Configuración → Respaldos. The cloud keeps the last ones (docs/despliegue-azure.md) and the owner downloads them from the panel.</summary>
internal sealed class CloudBackupUploader(CloudClient cloud, ICloudBackups backups, IFeatureService license)
{
    public const string PathPrefix = "/api/nodes/backups/";

    public enum Outcome { Nothing, Uploaded, Retry, Refused }

    public async Task<(Outcome Outcome, string? FileName)> RunAsync(CancellationToken ct)
    {
        if (!license.IsEnabled(Feature.CloudBackup) || await backups.PendingAsync(ct) is not { } file) return (Outcome.Nothing, null);
        if (await cloud.ConnectionAsync(ct) is not { } connection)
        {
            backups.Failed("Conecte la tienda a la nube (Configuración → Nube) para copiar los respaldos allí.");
            return (Outcome.Retry, file.FileName);
        }
        try
        {
            using var response = await cloud.SendFileAsync(connection, PathPrefix + Uri.EscapeDataString(file.FileName), file.Path, ct);
            if (response.IsSuccessStatusCode)
            {
                await backups.UploadedAsync(file.FileName, ct);
                return (Outcome.Uploaded, file.FileName);
            }
            // The cloud's own message when it gives one (quota, size, license); a 5xx, a 408 or a 429 with Retry-After (the hourly limit, not the
            // month's quota: R12 #9) is worth another try later.
            var detail = await DetailAsync(response, ct);
            backups.Failed(detail ?? $"La nube respondió {(int)response.StatusCode} al subir el respaldo.");
            var retry = (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout
                        || (response.StatusCode is HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is not null);
            return (retry ? Outcome.Retry : Outcome.Refused, file.FileName);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
                                       || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // IOException: the file was pruned meanwhile; InvalidOperationException: the node key cannot be read here (its own message).
            backups.Failed(e is InvalidOperationException ? e.Message : "No se pudo subir el respaldo a la nube. Se intenta de nuevo más tarde.");
            return (Outcome.Retry, file.FileName);
        }
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return (await response.Content.ReadFromJsonAsync<Problem>(ct))?.Detail; }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or HttpRequestException) { return null; }
    }

    private sealed record Problem(string? Detail);
}

/// <summary>Checks every <c>Backup:CloudCheckMinutes</c> (15) for a backup to copy to the cloud. After a failure it waits <c>Backup:CloudRetryMinutes</c>
/// (60) before sending hundreds of MB again; a backup the cloud refused (quota, size, license) waits for the next backup. Off when <c>Sync:Enabled</c> is false.</summary>
internal sealed class CloudBackupWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<CloudBackupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Sync:Enabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(config.GetValue("Backup:CloudCheckMinutes", 15)), clock);
        var retry = TimeSpan.FromMinutes(config.GetValue("Backup:CloudRetryMinutes", 60));
        (string? File, DateTimeOffset Until) wait = (null, DateTimeOffset.MinValue);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (clock.GetUtcNow() < wait.Until) continue;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var uploader = scope.ServiceProvider.GetRequiredService<CloudBackupUploader>();
                if (wait.File is { } refused && await scope.ServiceProvider.GetRequiredService<ICloudBackups>().PendingAsync(stoppingToken) is { } next
                    && next.FileName == refused) continue; // refused: only a new backup is tried
                var (outcome, file) = await uploader.RunAsync(stoppingToken);
                wait = outcome switch
                {
                    CloudBackupUploader.Outcome.Retry => (null, clock.GetUtcNow() + retry),
                    CloudBackupUploader.Outcome.Refused => (file, DateTimeOffset.MinValue),
                    _ => (null, DateTimeOffset.MinValue),
                };
                if (outcome == CloudBackupUploader.Outcome.Uploaded) log.LogInformation("Copied the newest backup to the cloud");
                else if (outcome != CloudBackupUploader.Outcome.Nothing) log.LogWarning("The backup was not copied to the cloud: {Outcome}", outcome);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "The cloud backup check failed");
                wait = (null, clock.GetUtcNow() + retry);
            }
        }
    }
}
