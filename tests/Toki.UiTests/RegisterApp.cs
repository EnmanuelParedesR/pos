using Microsoft.Playwright;
using Toki.E2ETests;

namespace Toki.UiTests;

/// <summary>The real local API (fresh database), the register's web host pointed at it, and one Chromium page. Shared by a test class.</summary>
public sealed class RegisterApp : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"toki_ui_{Guid.NewGuid():N}");
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private WebProcess? _web;

    public TokiServer Api { get; private set; } = null!;
    public IPage Page { get; private set; } = null!;
    /// <summary>Where the web host keeps this PC's device token (FileStore).</summary>
    public string ClientFile => Path.Combine(_folder, "client.json");

    public async ValueTask InitializeAsync()
    {
        Api = await TokiServer.StartAsync();
        _web = await WebProcess.HostAsync("Client", "Toki.Client.Web", new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["Api__Url"] = Api.Base.ToString(),
            ["Client__File"] = ClientFile,
            ["Hardware__PrinterFile"] = Path.Combine(_folder, "receipts.txt"),
            ["Hardware__Pounds"] = "2.5",
        });
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
        var context = await _browser.NewContextAsync(new() { BaseURL = _web.Base.ToString(), ViewportSize = new() { Width = 1366, Height = 900 }, Locale = "es-DO" });
        context.SetDefaultTimeout(15_000);
        Assertions.SetDefaultExpectTimeout(15_000); // a cold CI runner: the first circuit and the first queries take a few seconds
        Page = await context.NewPageAsync();
    }

    /// <summary>What both hosts wrote, for a failure message.</summary>
    public string Logs() => $"── API ──\n{Api.Output()}\n── Register web host ──\n{_web?.Output()}";

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        if (_web is not null) await _web.DisposeAsync();
        if (Api is not null) await Api.DisposeAsync();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { } // best effort: a temp folder left behind is harmless
        catch (UnauthorizedAccessException) { }
    }
}
