using Microsoft.Playwright;
using Abastalia.E2ETests;

namespace Abastalia.UiTests;

/// <summary>The real local API (fresh database), the register's web host pointed at it, and one Chromium page. Shared by a test class.</summary>
public sealed class RegisterApp : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"abastalia_ui_{Guid.NewGuid():N}");
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly List<(WebProcess Web, IBrowserContext Context)> _pcs = [];

    /// <summary>TK-365: more settings for the API process (the fake DGII).</summary>
    public Dictionary<string, string>? ApiSettings { get; init; }
    /// <summary>The trace file's name in <see cref="Traces.Folder"/>.</summary>
    public string TraceName { get; init; } = "register-day";

    public AbastaliaServer Api { get; private set; } = null!;
    public IPage Page { get; private set; } = null!;
    /// <summary>Where the web host keeps this PC's device token (FileStore).</summary>
    public string ClientFile => Path.Combine(_folder, "client.json");

    public async ValueTask InitializeAsync()
    {
        Api = await AbastaliaServer.StartAsync(ApiSettings);
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
        Assertions.SetDefaultExpectTimeout(15_000); // a cold CI runner: the first circuit and the first queries take a few seconds
        Page = await PcAsync(ClientFile, "receipts.txt");
    }

    /// <summary>TK-365: another PC of the store: its own register web host (its own device file, not paired yet) on the same API, and a page on it.</summary>
    public Task<IPage> AnotherPcAsync(string name) => PcAsync(Path.Combine(_folder, $"{name}.json"), $"{name}-receipts.txt");

    private async Task<IPage> PcAsync(string clientFile, string receipts)
    {
        var web = await WebProcess.HostAsync("Client", "Abastalia.Client.Web", new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["Api__Url"] = Api.Base.ToString(),
            ["Client__File"] = clientFile,
            ["Hardware__PrinterFile"] = Path.Combine(_folder, receipts),
            ["Hardware__Pounds"] = "2.5",
        });
        var context = await _browser!.NewContextAsync(new() { BaseURL = web.Base.ToString(), ViewportSize = new() { Width = 1366, Height = 900 }, Locale = "es-DO" });
        _pcs.Add((web, context));
        context.SetDefaultTimeout(15_000);
        await Traces.StartAsync(context);
        return await context.NewPageAsync();
    }

    /// <summary>What both hosts wrote, for a failure message.</summary>
    public string Logs() => $"── API ──\n{Api.Output()}" + string.Concat(_pcs.Select((p, i) => $"\n── Register web host {i + 1} ──\n{p.Web.Output()}"));

    public async ValueTask DisposeAsync()
    {
        for (var i = 0; i < _pcs.Count; i++) await Traces.StopAsync(_pcs[i].Context, i == 0 ? TraceName : $"{TraceName}-{i + 1}");
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        foreach (var (web, _) in _pcs) await web.DisposeAsync();
        if (Api is not null) await Api.DisposeAsync();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { } // best effort: a temp folder left behind is harmless
        catch (UnauthorizedAccessException) { }
    }
}
