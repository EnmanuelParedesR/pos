using System.Net.Http.Json;
using Microsoft.Playwright;
using Shouldly;
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
            ["Auth__RequireConfirmedEmail"] = "false", // R11 B-High1: the test does not read the confirmation e-mail
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
            await using var context = await browser.NewContextAsync(new() { BaseURL = origin });
            await Traces.StartAsync(context);
            try
            {
                var page = await context.NewPageAsync();
                await page.GotoAsync("/app"); // TK-228: the site is at the root, the panel under /app
                await page.GetByLabel("Correo").FillAsync("duena@esquina.do");
                await page.GetByLabel("Contraseña").FillAsync("Carniceria2026");
                await page.GetByLabel("Contraseña").PressAsync("Enter");
                await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Resumen" })).ToBeVisibleAsync(new() { Timeout = 15_000 });
                await NoHorizontalScrollOnPhonesAsync(page);
            }
            finally
            {
                await Traces.StopAsync(context, "panel-smoke");
            }
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException or HttpRequestException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{e.Message}\n── Cloud host ──\n{cloud.Output()}", e);
        }
    }

    /// <summary>The owner's sections (Resumen to Facturación).</summary>
    private static readonly string[] Pages = ["/app", "/app/sales", "/app/shifts", "/app/stores", "/app/assistant", "/app/received", "/app/notifications", "/app/security", "/app/members", "/app/billing"];

    /// <summary>TK-354: at 320, 375 and 390 px no owner page, nor Talia's popup over it, is wider than the screen (Ventas with its own dates
    /// open, the popup with a question typed so "Preguntar" is enabled). jsdom has no layout, so this lives here with a real browser.</summary>
    private static async Task NoHorizontalScrollOnPhonesAsync(IPage page)
    {
        // A busy store: the empty account has no numbers, and long names and big amounts are what push a phone screen wider.
        var today = DateTime.UtcNow.AddHours(-4).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var days = Enumerable.Range(0, 30).Select(i => new { date = DateTime.UtcNow.AddHours(-4).AddDays(-i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            count = 412, total = 1_234_567.89m, discount = 0m, itbis = 150_000m, returns = 12_345.67m, net = 1_222_222.22m, voidedCount = 3 }).ToArray();
        var stubs = new Dictionary<string, object>
        {
            ["/api/stores"] = new[] { "Comercial La Esquina — Sucursal Avenida Independencia", "Centro" }.Select((n, i) => new
                { id = $"0199e000-0000-7000-8000-00000000000{i}", name = n, createdUtc = "2026-01-01T00:00:00Z", lastEventUtc = DateTime.UtcNow.ToString("O"), lastSyncUtc = DateTime.UtcNow.ToString("O"), nodes = 3 }),
            ["/api/panel/summary"] = new
            {
                date = today, salesTotal = 1_234_567.89m, salesCount = 412, averageTicket = 2_996.52m, discountTotal = 0m, itbisTotal = 150_000m, returnsTotal = 12_345.67m,
                netTotal = 1_222_222.22m, voidedCount = 3, byHour = new[] { new { hour = 8, count = 200, total = 600_000m }, new { hour = 10, count = 212, total = 634_567.89m } },
                paymentMix = new[] { "Cash", "Card", "Transfer", "OnAccount", "CreditBalance" }.Select(m => new { method = m, count = 80, amount = 246_913.57m }),
                issues = new[] { new { kind = "ShiftDifference", storeId = "s1", storeName = "Comercial La Esquina — Sucursal Avenida Independencia", referenceId = "z1",
                    amount = -1_200.50m, occurredUtc = DateTime.UtcNow.ToString("O"), message = "Z #1234: faltante de RD$ 1,200.50", cashierName = "Yudelka Altagracia Martínez de los Santos", registerName = "Caja 12" } },
                lastEventUtc = DateTime.UtcNow.ToString("O"), ecfThisMonth = 123_456,
            },
            ["/api/panel/sales/by-day"] = days,
            ["/api/panel/sales/top-products"] = Enumerable.Range(1, 6).Select(i => new { productId = $"p{i}", description = $"Queso de hoja tipo Geo extra cremoso, empacado al vacío {i}", quantity = 1234.567m, total = 123_456.78m, profit = (decimal?)23_456.78m, unit = "lb" }),
            ["/api/panel/sales/by-cashier"] = new[] { new { cashierId = "c1", count = 412, total = 1_234_567.89m, voidedCount = 3, cashierName = "Yudelka Altagracia Martínez de los Santos" } },
            ["/api/panel/receivables"] = new { total = 987_654.32m, customerCount = 48, topDebtors = Enumerable.Range(1, 5).Select(i => new { customerId = $"k{i}", storeId = "s1", debt = 98_765.43m, customerName = $"Colmado y Repostería Hermanos Rodríguez Peña {i}" }) },
        };
        await page.RouteAsync("**/api/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (route.Request.Method == "GET" && stubs.TryGetValue(path, out var body)) await route.FulfillAsync(new() { Json = body });
            else await route.ContinueAsync();
        });
        await page.ReloadAsync(); // the stores list is read once, at sign-in
        await page.Locator("main h1").First.WaitForAsync();

        var problems = new List<string>();
        foreach (var width in new[] { 320, 375, 390 })
        {
            await page.SetViewportSizeAsync(width, 800);
            foreach (var path in Pages)
            {
                // In-app navigation, as a tap on the menu: a reload each time would spend the session's refresh token over and over.
                await page.EvaluateAsync("p => { history.pushState(null, '', p); dispatchEvent(new PopStateEvent('popstate')); }", path);
                await page.Locator("main h1").First.WaitForAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                if (path == "/app/sales") await page.GetByRole(AriaRole.Button, new() { Name = "Fechas" }).ClickAsync();
                problems.AddRange((await WiderThanScreenAsync(page)).Select(p => $"{width} px {path}: {p}"));
                if (path == "/app/assistant") continue; // the chat is the page there, no popup
                await page.GetByRole(AriaRole.Button, new() { Name = "Pregúntale a Talia" }).ClickAsync();
                var dialog = page.GetByRole(AriaRole.Dialog);
                await dialog.GetByLabel("Su pregunta").FillAsync("¿Cuánto vendí ayer en todas las tiendas?");
                problems.AddRange((await WiderThanScreenAsync(page)).Select(p => $"{width} px {path} + Talia: {p}"));
                await page.Keyboard.PressAsync("Escape");
            }
        }
        problems.ShouldBeEmpty();
    }

    /// <summary>The page scrolls sideways, or a visible element ends past the screen's right edge (a fixed popup does not make the page
    /// scroll, it gets cut). Elements inside a sideways-scrolling box (a wide table in .esc-table-wrap) are that box's business.</summary>
    private static Task<string[]> WiderThanScreenAsync(IPage page) => page.EvaluateAsync<string[]>("""
        () => {
          const w = document.documentElement.clientWidth, found = [];
          if (document.documentElement.scrollWidth > w) found.push(`page is ${document.documentElement.scrollWidth} px wide`);
          const scrolls = e => { for (let a = e.parentElement; a && a !== document.body; a = a.parentElement)
            if (/(auto|scroll)/.test(getComputedStyle(a).overflowX)) return true; return false; };
          for (const e of document.body.querySelectorAll('*')) {
            const r = e.getBoundingClientRect();
            if (r.width === 0 || r.height === 0 || getComputedStyle(e).visibility === 'hidden' || scrolls(e)) continue;
            // Text spilling out of its own box (a nowrap amount running into the next column or cut by its card).
            if (e.clientWidth > 0 && getComputedStyle(e).overflowX === 'visible' && e.scrollWidth > e.clientWidth + 1 && !(e instanceof SVGElement))
              found.push(`${e.tagName.toLowerCase()}.${String(e.className).trim().split(/\s+/).join('.')} "${(e.textContent || '').trim().slice(0, 30)}" spills ${e.scrollWidth - e.clientWidth} px out of its box`);
            if (r.right <= w + 1) continue;
            found.push(`${e.tagName.toLowerCase()}${e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/).join('.') : ''} "${(e.textContent || '').trim().slice(0, 30)}" ends at ${Math.round(r.right)} px`);
          }
          return found.slice(0, 8);
        }
        """);
}
