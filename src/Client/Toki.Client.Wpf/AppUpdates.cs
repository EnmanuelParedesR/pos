using System.IO;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace Toki.Client.Wpf;

/// <summary>TK-242: the register app downloads a new version in the background; Velopack applies it the next time Toki opens,
/// never in the middle of a sale. Only with "Updates": { "FeedUrl" } in appsettings and only when installed (not from a build folder).</summary>
internal static class AppUpdates
{
    public static async Task DownloadAsync(JsonElement config, string log)
    {
        if (!config.TryGetProperty("Updates", out var updates) || !updates.TryGetProperty("FeedUrl", out var url) || url.GetString() is not { Length: > 0 } feed) return;
        // Staged rollout: by default the channel the app was packed for (pilot or stable); the setting moves a store to another one.
        var channel = updates.TryGetProperty("Channel", out var c) && c.GetString() is { Length: > 0 } name ? name : null;
        try
        {
            var manager = new UpdateManager(new SimpleWebSource(feed), new UpdateOptions { ExplicitChannel = channel });
            if (!manager.IsInstalled) return;
            if (await manager.CheckForUpdatesAsync() is { } update) await manager.DownloadUpdatesAsync(update);
        }
        catch (Exception e) // no internet or a bad feed: the register keeps working on its version and tries again next time
        {
            try { File.AppendAllText(log, $"{DateTime.UtcNow:O} Actualización: {e.Message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}
