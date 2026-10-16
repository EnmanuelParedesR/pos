using System.Net.Http.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Abastalia.UiTests;

/// <summary>TK-123: the owner's panel as built for production (vite build + preview) signs in against the real cloud host (Sqlite)
/// and loads Resumen. Needs `npm ci` in src/Cloud/panel.</summary>
public sealed class PanelSmokeTests
{
    [Fact]
    public async Task The_owner_signs_in_and_sees_the_summary()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"abastalia_panel_{Guid.NewGuid():N}")).FullName;
        try
        {
            await SignInAsync(folder);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { } // best effort: a temp folder left behind is harmless
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task SignInAsync(string folder)
    {
        var previewPort = WebProcess.FreePort();
        var origin = $"http://127.0.0.1:{previewPort}";
        await using var cloud = await WebProcess.HostAsync("Hosts", "Abastalia.Host.Cloud", new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["Database__Provider"] = "Sqlite",
            ["Database__ConnectionString"] = $"Data Source={Path.Combine(folder, "cloud.db")};Pooling=False",
            ["Panel__Origins__0"] = origin,
        }, readyPath: "/health");
        try
        {
            using (var http = new HttpClient { BaseAddress = cloud.Base })
                (await http.PostAsJsonAsync("/api/auth/register", new { Email = "duena@esquina.do", Password = "Carniceria2026" })).EnsureSuccessStatusCode();

            // The API's address is baked in at build time (VITE_API_URL), as in production.
            var dist = Path.Combine(folder, "dist");
            await WebProcess.RunNodeAsync("node_modules/vite/bin/vite.js", $"build --outDir \"{dist}\" --emptyOutDir", new() { ["VITE_API_URL"] = cloud.Base.ToString() });
            await using var preview = await WebProcess.NodeAsync("node_modules/vite/bin/vite.js", $"preview --outDir \"{dist}\" --host 127.0.0.1 --port {previewPort} --strictPort", previewPort);

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var page = await browser.NewPageAsync(new() { BaseURL = origin });
            await page.GotoAsync("/app"); // TK-228: the site is at the root, the panel under /app
            await page.GetByLabel("Correo").FillAsync("duena@esquina.do");
            await page.GetByLabel("Contraseña").FillAsync("Carniceria2026");
            await page.GetByLabel("Contraseña").PressAsync("Enter");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Resumen" })).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException or HttpRequestException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{e.Message}\n── Cloud host ──\n{cloud.Output()}", e);
        }
    }
}
