using Microsoft.Playwright;
using Shouldly;

namespace Abastalia.UiTests;

/// <summary>TK-243: screenshots of the Escuadra gallery (/dev/escuadra, Development only) against committed baselines in
/// Baselines/ (one set per OS: font rendering differs). A missing baseline is written and the test passes; delete a baseline
/// to accept an intended change.</summary>
public sealed class EscuadraVisualTests
{
    /// <summary>A pixel differs when a channel moves more than this (anti-aliasing noise stays under it).</summary>
    private const int ChannelTolerance = 24;
    /// <summary>Share of differing pixels a screenshot may have.</summary>
    private const double PixelTolerance = 0.002;

    private static string Baselines => Path.Combine(WebProcess.RepoRoot, "tests", "Abastalia.UiTests", "Baselines");

    [Fact]
    public async Task The_gallery_looks_like_its_baselines()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"abastalia_visual_{Guid.NewGuid():N}")).FullName;
        await using var web = await WebProcess.HostAsync("Client", "Abastalia.Client.Web", new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E", // binds to the test port; Dev:Gallery serves the Development-only gallery
            ["Dev__Gallery"] = "true",
            ["Client__File"] = Path.Combine(folder, "client.json"),
            ["Hardware__PrinterFile"] = Path.Combine(folder, "receipts.txt"),
        }, readyPath: "/dev/escuadra");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync(new() { BaseURL = web.Base.ToString(), ViewportSize = new() { Width = 1366, Height = 768 }, Locale = "es-DO" });
        await page.GotoAsync("/dev/escuadra");
        await page.EvaluateAsync("() => document.fonts.ready");

        var shot = new LocatorScreenshotOptions { Animations = ScreenshotAnimations.Disabled, Caret = ScreenshotCaret.Hide };
        var failures = new List<string>();
        await CompareAsync(page, "register-light", await page.ScreenshotAsync(new() { Animations = ScreenshotAnimations.Disabled, Caret = ScreenshotCaret.Hide }), failures);
        await CompareAsync(page, "admin-light", await page.Locator(".esc-admin:not([data-theme])").ScreenshotAsync(shot), failures);
        await CompareAsync(page, "admin-dark", await page.Locator(".esc-admin[data-theme=dark]").ScreenshotAsync(shot), failures);
        failures.ShouldBeEmpty();
    }

    private static async Task CompareAsync(IPage page, string name, byte[] actual, List<string> failures)
    {
        Directory.CreateDirectory(Baselines);
        var file = Path.Combine(Baselines, $"{name}.{OperatingSystem()}.png");
        if (!File.Exists(file))
        {
            await File.WriteAllBytesAsync(file, actual);
            return;
        }
        // The browser decodes both PNGs and counts the pixels that differ, so the tests need no image library.
        var diff = await page.EvaluateAsync<double>("""
            async ([a, b, tolerance]) => {
                const load = async src => { const i = new Image(); i.src = 'data:image/png;base64,' + src; await i.decode(); return i; };
                const [x, y] = await Promise.all([load(a), load(b)]);
                if (x.width !== y.width || x.height !== y.height) return 1;
                const pixels = img => { const c = new OffscreenCanvas(img.width, img.height); const g = c.getContext('2d');
                    g.drawImage(img, 0, 0); return g.getImageData(0, 0, img.width, img.height).data; };
                const [p, q] = [pixels(x), pixels(y)];
                let differing = 0;
                for (let i = 0; i < p.length; i += 4)
                    if (Math.abs(p[i] - q[i]) > tolerance || Math.abs(p[i + 1] - q[i + 1]) > tolerance || Math.abs(p[i + 2] - q[i + 2]) > tolerance) differing++;
                return differing / (p.length / 4);
            }
            """, new object[] { Convert.ToBase64String(await File.ReadAllBytesAsync(file)), Convert.ToBase64String(actual), ChannelTolerance });
        if (diff > PixelTolerance)
        {
            var actualFile = Path.Combine(Path.GetTempPath(), $"{name}.actual.png");
            await File.WriteAllBytesAsync(actualFile, actual);
            failures.Add($"{name}: {diff:P2} of the pixels differ from {file} (actual: {actualFile})");
        }
    }

    private static string OperatingSystem() =>
        System.OperatingSystem.IsWindows() ? "windows" : System.OperatingSystem.IsMacOS() ? "macos" : "linux";
}
