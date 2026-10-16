using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Toki.CashRegister.Contracts;

namespace Toki.Host.Local;

/// <summary>TK-242, section "Updates". No FeedUrl (the default) = no checks, so dev and tests never look for updates.</summary>
internal sealed class UpdateSettings
{
    /// <summary>The release feed: the folder with releases.{channel}.json and TokiSetup-{version}.exe (GitHub Releases or Azure Blob).</summary>
    public string FeedUrl { get; set; } = "";
    /// <summary>Staged rollout: "pilot" stores get a release first, "stable" ones once it has run there.</summary>
    public string Channel { get; set; } = "stable";
    /// <summary>Store hours (local time). Outside them the server may restart to update even with a shift left open.</summary>
    public TimeOnly OpensAt { get; set; } = new(7, 0);
    public TimeOnly ClosesAt { get; set; } = new(22, 0);
    public int CheckEveryMinutes { get; set; } = 30;
    /// <summary>Read by update-service.ps1, not by the service: thumbprints of Toki's Authenticode certificate (R11 C3). Without one, no
    /// update is installed (docs/instalador-web.md, "Firma de código").</summary>
    public string[] SignerThumbprints { get; set; } = [];
    /// <summary>Read by update-service.ps1: pilot only, before the certificate exists. Then the feed's SHA-256 is enough, which does not
    /// protect against someone who controls the feed.</summary>
    public bool AllowUnsignedUpdates { get; set; }
}

internal static class UpdateWindow
{
    /// <summary>The server restarts to update only when no register has a shift open, or outside business hours.
    /// Hours may cross midnight (opens 18:00, closes 02:00).</summary>
    public static bool CanApply(int openShifts, TimeOnly now, TimeOnly opensAt, TimeOnly closesAt) =>
        openShifts == 0 || !now.IsBetween(opensAt, closesAt);

    /// <summary>The newest full release in the feed's releases.{channel}.json (build.ps1 writes it), or null if the feed has none.</summary>
    public static Version? Newest(JsonElement feed) =>
        feed.TryGetProperty("Assets", out var assets)
            ? assets.EnumerateArray()
                .Where(a => a.GetProperty("Type").GetString() == "Full")
                .Select(a => Version.TryParse(a.GetProperty("Version").GetString()?.Split('-', '+')[0], out var v) ? v : null)
                .Max()
            : null;
}

/// <summary>What the scripted update (deploy/installer/update-service.ps1) reads: whether there is a newer server, whether it may restart now,
/// and the setup's SHA-256 as the feed publishes it (fetched over HTTPS).</summary>
internal sealed record UpdateMarker(string Version, string SetupUrl, string Sha256, bool CanApplyNow, DateTime CheckedUtc)
{
    public static UpdateMarker Create(string feed, string version, string sha256File, bool canApplyNow, DateTime checkedUtc)
    {
        var hash = sha256File.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw new InvalidOperationException($"TokiSetup-{version}.exe.sha256 no trae un SHA-256.");
        return new(version, $"{feed}/TokiSetup-{version}.exe", hash.ToUpperInvariant(), canApplyNow, checkedUtc);
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>TK-242: the store server cannot update itself in place (it is a Windows service, with no rights over its own files).
/// It checks the feed, logs "Hay una actualización" and writes C:\ProgramData\Toki\update.json; a scheduled task applies it when allowed.</summary>
internal sealed class ServerUpdateCheck(IHttpClientFactory http, IServiceScopeFactory scopes, IOptions<UpdateSettings> options, TimeProvider clock,
    ILogger<ServerUpdateCheck> log) : BackgroundService
{
    public static string MarkerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "update.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var s = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(5, s.CheckEveryMinutes)), clock);
        do
        {
            try
            {
                await CheckAsync(s, stoppingToken);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or IOException or KeyNotFoundException or InvalidOperationException
                                      || (e is TaskCanceledException && !stoppingToken.IsCancellationRequested))
            {
                log.LogWarning("No se pudo buscar actualizaciones en {Feed}: {Error}", s.FeedUrl, e.Message);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAsync(UpdateSettings s, CancellationToken ct)
    {
        var feed = s.FeedUrl.TrimEnd('/');
        if (!feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Updates:FeedUrl tiene que ser https.");
        using var response = await http.CreateClient().GetAsync($"{feed}/releases.{s.Channel}.json", ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
        if (UpdateWindow.Newest(json.RootElement) is not { } newest || newest <= current)
        {
            File.Delete(MarkerPath); // up to date: nothing left for the scheduled task
            return;
        }

        int open;
        using (var scope = scopes.CreateScope())
            open = (await scope.ServiceProvider.GetRequiredService<ICashRegisterReports>().OpenRegistersAsync(ct)).Count;
        var now = TimeOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var canApply = UpdateWindow.CanApply(open, now, s.OpensAt, s.ClosesAt);
        var version = newest.ToString(3);
        log.LogWarning("Hay una actualización de Toki: {Version} (esta PC tiene {Current}). {When}", version, current.ToString(3),
            canApply ? "Se instala en la próxima revisión." : "Se instala cuando no haya turnos abiertos o fuera del horario.");
        var sha256 = await http.CreateClient().GetStringAsync($"{feed}/TokiSetup-{version}.exe.sha256", ct);
        await File.WriteAllTextAsync(MarkerPath, UpdateMarker.Create(feed, version, sha256, canApply, clock.GetUtcNow().UtcDateTime).ToJson(), ct);
    }
}
