using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Shouldly;
using Abastalia.E2ETests;
using static Microsoft.Playwright.Assertions;

namespace Abastalia.UiTests;

/// <summary>TK-296: the frames of the site's product video, from the real register and the real panel on a demo store with made-up data
/// ("Comercial La Esquina"). Only with ABASTALIA_VIDEO=1; design/video/README.md has the two commands. Writes design/video/capturas
/// (git-ignored): one folder of JPEG frames per take, named by milliseconds since the take started, with marks.json (where each shot of
/// productVideo.json starts), and overlays/ (the captions and frames as PNG). montaje.py turns them into the video.</summary>
public sealed class ProductVideoTests
{
    public static bool Enabled => Environment.GetEnvironmentVariable("ABASTALIA_VIDEO") == "1";

    private const string Skip = "Only to render the product video: set ABASTALIA_VIDEO=1 (design/video/README.md).";
    private const string OwnerPin = "1234", CashierPin = "4321", Email = "ana.perez@example.com", Password = "Esquina2026";
    private const string Rice = "7460000000016", Soda = "7461234567890";
    private static readonly Guid CashierRole = new("0199e000-0000-7000-8000-000000000004"); // Cajero con apertura

    private static string Captures => Path.Combine(WebProcess.RepoRoot, "design", "video", "capturas");
    private static JsonElement Script => JsonDocument.Parse(File.ReadAllText(Path.Combine(WebProcess.RepoRoot, "src", "Cloud", "panel", "src", "pages", "site", "productVideo.json"))).RootElement;

    /// <summary>The text and frames over the captures. Quick: after a text change in productVideo.json, run only this one and montaje.py.</summary>
    [Fact(Skip = Skip, SkipUnless = nameof(Enabled))]
    public async Task ProductVideo_overlays()
    {
        var folder = Fresh(Path.Combine(Captures, "overlays"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 720 } });
        await page.GotoAsync(new Uri(Path.Combine(WebProcess.RepoRoot, "design", "video", "overlays.html")).AbsoluteUri);

        async Task Render(string name, string kind, object data)
        {
            await page.EvaluateAsync("([k, d]) => render(k, d)", new object[] { kind, data });
            await page.ScreenshotAsync(new() { Path = Path.Combine(folder, name + ".png"), OmitBackground = true });
        }

        var script = Script;
        var screen = script.GetProperty("phoneScreen");
        await Render("sample", "sample", new { });
        await Render("phone-bg", "phone-bg", new { });
        await Render("phone", "phone", new { x = screen.GetProperty("x").GetInt32(), y = screen.GetProperty("y").GetInt32(), w = screen.GetProperty("w").GetInt32(), h = screen.GetProperty("h").GetInt32() });
        foreach (var shot in script.GetProperty("shots").EnumerateArray())
        {
            var id = shot.GetProperty("id").GetString()!;
            var text = shot.GetProperty("caption").GetString()!;
            var title = shot.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (shot.TryGetProperty("card", out _)) await Render(id, "end", new { title, text });
            else if (title is not null) await Render(id, "title", new { title, text });
            else await Render(id, "caption", new { text, phone = shot.TryGetProperty("phone", out _) });
        }
    }

    /// <summary>The takes. Starts the cloud host and the panel (vite build + preview), the store's API connected to that cloud, and the
    /// register's web host with a fake scale (1.250 lb). Everything runs on 127.0.0.1 and throwaway databases in %TEMP%.</summary>
    [Fact(Skip = Skip, SkipUnless = nameof(Enabled))]
    public async Task ProductVideo_capture()
    {
        var temp = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"abastalia_video_{Guid.NewGuid():N}")).FullName;
        try
        {
            await CaptureAsync(temp);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { } // best effort: a temp folder left behind is harmless
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task CaptureAsync(string temp)
    {
        // ── The cloud and the owner's account, with the store and two connection codes (the store's PC and the history below) ──
        var previewPort = WebProcess.FreePort();
        var origin = $"http://127.0.0.1:{previewPort}";
        await using var cloud = await WebProcess.HostAsync("Hosts", "Abastalia.Host.Cloud", new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["Database__Provider"] = "Sqlite",
            ["Database__ConnectionString"] = $"Data Source={Path.Combine(temp, "cloud.db")};Pooling=False",
            ["Panel__Origins__0"] = origin,
            // No e-mail provider: the confirmation goes to the log, where the owner's link is read (no banner in the panel then).
            ["Serilog__MinimumLevel__Override__Abastalia.Host.Cloud.Accounts.LoggingEmailSender"] = "Debug",
        }, readyPath: "/health");
        using var owner = new HttpClient { BaseAddress = cloud.Base };
        await Ok(await owner.PostAsJsonAsync("/api/auth/register", new { Email, Password }));
        for (var i = 0; i < 50 && !cloud.Output().Contains("/app/confirmar#", StringComparison.Ordinal); i++) await Pause(200);
        var link = System.Text.RegularExpressions.Regex.Match(cloud.Output(), @"/app/confirmar#(\S+)").Groups[1].Value;
        var values = link.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        await Ok(await owner.PostAsJsonAsync("/api/auth/confirm", new { userId = values["userId"], code = values["code"] }));
        var login = await Ok(await owner.PostAsJsonAsync("/api/auth/login", new { Email, Password }));
        owner.DefaultRequestHeaders.Authorization = new("Bearer", login.GetProperty("accessToken").GetString());
        var storeId = (await Ok(await owner.PostAsJsonAsync("/api/stores", new { Name = "Comercial La Esquina" }))).GetProperty("id").GetGuid();
        async Task<string> Code() => (await Ok(await owner.PostAsync($"/api/stores/{storeId}/connection-codes", null))).GetProperty("code").GetString()!;

        var dist = Path.Combine(temp, "dist");
        await WebProcess.RunNodeAsync("node_modules/vite/bin/vite.js", $"build --outDir \"{dist}\" --emptyOutDir", new() { ["VITE_API_URL"] = cloud.Base.ToString() });
        await using var preview = await WebProcess.NodeAsync("node_modules/vite/bin/vite.js", $"preview --outDir \"{dist}\" --host 127.0.0.1 --port {previewPort} --strictPort", previewPort);

        // ── The store: its API (connected to the cloud) and the register's web host ──
        await using var api = await AbastaliaServer.StartAsync(new() { ["Cloud__Url"] = cloud.Base.ToString() });
        var clientFile = Path.Combine(temp, "client.json");
        await using var web = await WebProcess.HostAsync("Client", "Abastalia.Client.Web", new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["Api__Url"] = api.Base.ToString(),
            ["Client__File"] = clientFile,
            ["Hardware__PrinterFile"] = Path.Combine(temp, "receipts.txt"),
            ["Hardware__Pounds"] = "1.25",
        });

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        Assertions.SetDefaultExpectTimeout(20_000);
        var desk = await browser.NewContextAsync(new()
        {
            BaseURL = web.Base.ToString(), ViewportSize = new() { Width = 1280, Height = 720 }, DeviceScaleFactor = 1.5f, Locale = "es-DO",
        });
        desk.SetDefaultTimeout(20_000);
        var page = await desk.NewPageAsync();
        var keys = page.Keyboard;

        // First install on this PC: the owner.
        await page.GotoAsync("/");
        await page.GetByRole(AriaRole.Tab, new() { Name = "Primera instalación" }).ClickAsync();
        await page.GetByLabel("Su nombre").FillAsync("Ana Pérez");
        await keys.PressAsync("Tab");
        await keys.TypeAsync("ana");
        await keys.PressAsync("Tab");
        await keys.TypeAsync(OwnerPin);
        await keys.PressAsync("Tab");
        await keys.PressAsync("Enter");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "¿Quién entra?" })).ToBeVisibleAsync();

        // The back office over the API, as RegisterDayTests does.
        var device = JsonDocument.Parse(await File.ReadAllTextAsync(clientFile)).RootElement.GetProperty("device").GetString()!;
        using var admin = api.Client(device, (await Ok(await api.Client(device).PostAsJsonAsync("/api/identity/sessions", new { Code = "ana", Pin = OwnerPin })))
            .GetProperty("token").GetString());
        await Ok(await admin.PostAsJsonAsync("/api/identity/users", new { Name = "María", Code = "maria", Pin = CashierPin, RoleId = CashierRole }));
        await Ok(await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Comercial La Esquina", Rnc = "131245678", DiscountCapPercent = 10m }));
        foreach (var type in new[] { 31, 32, 34 })
            await Ok(await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = type, From = 1, To = 1000, ExpiresOn = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1) }));
        await Ok(await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja 1" }));
        async Task<Guid> Category(string name, string color) =>
            (await Ok(await admin.PostAsJsonAsync("/api/inventory/categories", new { Name = name, Color = color, Notes = Array.Empty<string>() }))).GetProperty("id").GetGuid();
        var grocery = await Category("Colmado", "#0B5CAD");
        var drinks = await Category("Bebidas", "#176B35");
        var deli = await Category("Quesos y embutidos", "#B42318");
        var catalog = new List<Item>();
        async Task Product(string name, Guid category, decimal price, decimal cost, string unit, string rate, string? plu, params string[] barcodes)
        {
            var id = (await Ok(await admin.PostAsJsonAsync("/api/inventory/products", new
            {
                Name = name, CategoryId = category, Unit = unit, Plu = plu, ItbisRate = rate, Price = price, WholesalePrice = (decimal?)null, Cost = cost,
                SellWithoutStock = false, MinimumStock = 0, Barcodes = barcodes, InitialStock = 500m,
            }))).GetProperty("id").GetGuid();
            catalog.Add(new(id, name, price, cost, unit, rate == "General18"));
        }
        await Product("Arroz selecto 5 lb", grocery, 285m, 230m, "Unit", "Exempt", null, Rice);
        await Product("Habichuelas rojas 15 oz", grocery, 75m, 55m, "Unit", "General18", null, "7460000000023");
        await Product("Aceite de soya 1 L", grocery, 210m, 170m, "Unit", "General18", null, "7460000000030");
        await Product("Huevos (30)", grocery, 330m, 270m, "Unit", "Exempt", null, "7460000000047");
        await Product("Café molido 1 lb", grocery, 340m, 275m, "Unit", "General18", null, "7460000000054");
        await Product("Refresco 2 L", drinks, 95m, 70m, "Unit", "General18", null, Soda);
        await Product("Agua 1 galón", drinks, 60m, 40m, "Unit", "General18", null, "7460000000061");
        await Product("Jugo de naranja 1 L", drinks, 120m, 90m, "Unit", "General18", null, "7460000000078");
        await Product("Queso de freír", deli, 220m, 165m, "Pound", "Exempt", "2001");
        await Product("Salami", deli, 180m, 130m, "Pound", "General18", "2002");
        await Product("Jamón de pavo", deli, 260m, 195m, "Pound", "General18", "2003");

        // The store's PC uploads to the cloud as in a real store; the week before today goes up from a second PC of the same store,
        // signed with its own key, so the panel's "last 7 days" and Talia have something to show.
        await Ok(await admin.PostAsJsonAsync("/api/sync/connect", new { Code = await Code() }));
        var historyToday = await SendHistoryAsync(cloud.Base, await Code(), catalog);

        // ── The cashier opens Caja 1 ──
        await page.GotoAsync("/sign-in");
        await page.GetByRole(AriaRole.Button, new() { Name = "María" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Region, new() { Name = "PIN" })).ToBeFocusedAsync();
        await keys.TypeAsync(CashierPin);
        await keys.PressAsync("Enter");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Abrir caja" })).ToBeVisibleAsync();
        await page.GetByLabel("Cantidad de 1,000", new() { Exact = true }).FillAsync("2");
        await page.GetByLabel("Cantidad de 100", new() { Exact = true }).FillAsync("5");
        await keys.PressAsync("F12");
        var scanner = page.GetByLabel("Código de barras o PLU");
        var lines = page.GetByRole(AriaRole.List, new() { Name = "Líneas" }).GetByRole(AriaRole.Listitem);
        await Expect(scanner).ToBeFocusedAsync();
        await page.Mouse.MoveAsync(900, 600);
        await using (await page.Screencast.ShowActionsAsync(new() { Cursor = ScreencastCursor.Pointer, Duration = 350, FontSize = 1 }))
        {
            // ── Take "venta": by unit and by code, then on the scale ──
            await using (var take = await Take.StartAsync(page, "venta", 1920, 1080))
            {
                await Pause(1200);
                take.Mark("unidad");
                await Pause(500);
                await scanner.PressSequentiallyAsync(Rice, new() { Delay = 35 });
                await keys.PressAsync("Enter");
                await Expect(lines).ToHaveCountAsync(1);
                await Pause(700);
                var soda = page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Refresco 2 L" });
                await page.GetByRole(AriaRole.Button, new() { Name = "Bebidas" }).ClickAsync();
                await Pause(500);
                for (var i = 0; i < 3; i++)
                {
                    await soda.ClickAsync();
                    await Pause(450);
                }
                await Pause(900);
                take.Mark("peso");
                await page.GetByRole(AriaRole.Button, new() { Name = "Quesos y embutidos" }).ClickAsync();
                await Pause(500);
                await page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Queso de freír" }).ClickAsync();
                var weigh = page.GetByRole(AriaRole.Dialog, new() { Name = "Pesar" });
                await Expect(weigh.GetByText("Peso estable")).ToBeVisibleAsync();
                await Pause(1500);
                await keys.PressAsync("Enter");
                await Expect(weigh).ToBeHiddenAsync();
                await Pause(2200);
            }

            // ── Take "cobro": cash, the change and the invoice with its e-CF ──
            await using (var take = await Take.StartAsync(page, "cobro", 1920, 1080))
            {
                take.Mark("ecf");
                await Pause(400);
                await keys.PressAsync("F12");
                var checkout = page.GetByRole(AriaRole.Dialog, new() { Name = "Cobrar" });
                await Expect(checkout.GetByLabel("Efectivo recibido")).ToBeFocusedAsync();
                await Pause(700);
                await keys.PressAsync("Control+A");
                await checkout.GetByLabel("Efectivo recibido").PressSequentiallyAsync("1000", new() { Delay = 110 });
                await Pause(500);
                await keys.PressAsync("Enter");
                await Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Venta guardada" }).First).ToBeVisibleAsync();
                await Pause(1400);
                await page.GetByRole(AriaRole.Button, new() { Name = "Ver factura" }).ClickAsync();
                await Pause(2600);
            }
            await keys.PressAsync("Escape");

            // ── Take "cierre": the blind count, by bill, of exactly what the drawer should hold ──
            await keys.PressAsync("F9");
            await page.GetByRole(AriaRole.Dialog, new() { Name = "Caja" }).GetByRole(AriaRole.Button, new() { Name = "Cerrar caja" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Cerrar caja" })).ToBeVisibleAsync();
            var registers = await Ok(await admin.GetAsync("/api/cash/registers"));
            var shiftId = registers.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Caja 1").GetProperty("openShiftId").GetGuid();
            var expected = (await Ok(await admin.GetAsync($"/api/cash/shifts/{shiftId}/z"))).GetProperty("expectedCash").GetDecimal();
            await using (var take = await Take.StartAsync(page, "cierre", 1920, 1080))
            {
                take.Mark("cierre");
                await Pause(600);
                var left = expected;
                foreach (var bill in new[] { 2000m, 1000m, 500m, 200m, 100m, 50m, 25m, 10m, 5m, 1m })
                {
                    var count = Math.Floor(left / bill);
                    if (count == 0) continue;
                    left -= count * bill;
                    var field = page.GetByLabel($"Cantidad de {bill.ToString("#,##0", CultureInfo.InvariantCulture)}", new() { Exact = true });
                    await field.ClickAsync();
                    await field.PressSequentiallyAsync(count.ToString(CultureInfo.InvariantCulture), new() { Delay = 90 });
                    await Pause(250);
                }
                await Pause(500);
                await keys.PressAsync("F12");
                await Pause(800);
                await keys.PressAsync("F12");
                await Expect(page.Locator(".kpi__value").First).ToBeVisibleAsync();
                await Pause(2600);
            }
        }

        // ── The owner's panel on a phone: the summary, then Talia ──
        var phone = await browser.NewContextAsync(new()
        {
            BaseURL = origin, ViewportSize = new() { Width = 390, Height = 844 }, DeviceScaleFactor = 2, IsMobile = true, HasTouch = true, Locale = "es-DO",
        });
        phone.SetDefaultTimeout(20_000);
        var mobile = await phone.NewPageAsync();
        await mobile.GotoAsync("/app");
        await mobile.GetByLabel("Correo").FillAsync(Email);
        await mobile.GetByLabel("Contraseña").FillAsync(Password);
        await mobile.GetByLabel("Contraseña").PressAsync("Enter");
        await Expect(mobile.GetByRole(AriaRole.Heading, new() { Name = "Resumen" })).ToBeVisibleAsync();
        // The register's sale reaches the cloud within seconds (Sync:IntervalSeconds); reload until today's sales include it.
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-4));
        for (var i = 0; i < 40; i++)
        {
            var summary = await Ok(await owner.GetAsync($"/api/panel/summary?date={today:yyyy-MM-dd}"));
            if (summary.GetProperty("salesCount").GetInt32() > historyToday) break;
            await Pause(1000);
        }
        await mobile.ReloadAsync();
        await Expect(mobile.GetByRole(AriaRole.Heading, new() { Name = "Resumen" })).ToBeVisibleAsync();
        // Before the store opens there is little of today to show: then the panel shows yesterday, a whole day.
        if (DateTime.UtcNow.AddHours(-4).Hour < 10) await mobile.GetByText("Ayer", new() { Exact = true }).ClickAsync();
        await Expect(mobile.GetByRole(AriaRole.Heading, new() { Name = "Lo más vendido" })).ToBeVisibleAsync();
        await Pause(1500);

        // Talia with a fixed answer (no model is called), written from the same top products the panel shows.
        var top = await Ok(await owner.GetAsync($"/api/panel/sales/top-products?from={today.AddDays(-6):yyyy-MM-dd}&to={today:yyyy-MM-dd}"));
        var answer = TaliaAnswer(top);
        await mobile.RouteAsync("**/api/assistant/ask", async route =>
        {
            await Task.Delay(1300);
            await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = JsonSerializer.Serialize(new { answer }) });
        });

        await using (await mobile.Screencast.ShowActionsAsync(new() { Cursor = ScreencastCursor.Pointer, Duration = 350, FontSize = 1 }))
        await using (var take = await Take.StartAsync(mobile, "telefono", 780, 1688))
        {
            take.Mark("telefono");
            await Pause(1500);
            await mobile.EvaluateAsync("() => window.scrollTo({ top: 520, behavior: 'smooth' })");
            await Pause(2200);
            await mobile.EvaluateAsync("() => window.scrollTo({ top: 1100, behavior: 'smooth' })");
            await Pause(2200);
            await mobile.EvaluateAsync("() => window.scrollTo({ top: 0, behavior: 'smooth' })");
            await Pause(900);
            take.Mark("talia");
            await mobile.GetByRole(AriaRole.Button, new() { Name = "Pregúntale a Talia" }).ClickAsync(); // on a phone, Talia's button opens her chat
            var ask = mobile.Locator("#talia-question");
            await Expect(ask).ToBeVisibleAsync();
            await Pause(400);
            await ask.PressSequentiallyAsync("¿Qué se vendió más esta semana?", new() { Delay = 45 });
            await Pause(300);
            await ask.PressAsync("Enter");
            await Expect(mobile.GetByText(answer[..20])).ToBeVisibleAsync();
            await Pause(2800);
        }
        api.Errors500.ShouldBeEmpty();
    }

    // ── The week before ──

    private sealed record Item(Guid Id, string Name, decimal Price, decimal Cost, string Unit, bool Taxed);

    /// <summary>Made-up sales for the two weeks before today and today until now, from a second PC of the store (TK-241: a node key of its
    /// own). Returns how many are today's.</summary>
    private static async Task<int> SendHistoryAsync(Uri cloud, string code, List<Item> catalog)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var plain = new HttpClient { BaseAddress = cloud };
        var nodeId = (await Ok(await plain.PostAsJsonAsync("/api/nodes/register",
            new { code, nodeName = "PC de la oficina", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }))).GetProperty("nodeId").GetGuid();
        using var node = new HttpClient(new NodeSigner(nodeId, key) { InnerHandler = new HttpClientHandler() }) { BaseAddress = cloud };

        var random = new Random(296);
        var cashier = Guid.CreateVersion7();
        var shift = Guid.CreateVersion7();
        var nowUtc = DateTime.UtcNow;
        var storeToday = DateOnly.FromDateTime(nowUtc.AddHours(-4));
        var events = new List<object>();
        long number = 1000;
        var today = 0;
        for (var day = -13; day <= 0; day++)
        {
            var date = storeToday.AddDays(day);
            var count = random.Next(22, 34);
            for (var s = 0; s < count; s++)
            {
                // Store hours 8:00–19:59 (UTC−4); today only until a few minutes ago.
                var local = date.ToDateTime(new TimeOnly(8, 0)).AddMinutes(random.Next(0, 12 * 60));
                var utc = DateTime.SpecifyKind(local.AddHours(4), DateTimeKind.Utc);
                if (utc > nowUtc.AddMinutes(-10)) continue;
                if (day == 0) today++;
                var picked = catalog.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToList();
                var saleLines = picked.Select(p =>
                {
                    var quantity = p.Unit == "Pound" ? Math.Round((decimal)(0.5 + random.NextDouble() * 2), 3) : random.Next(1, 4);
                    var total = Math.Round(p.Price * quantity, 2);
                    var itbis = p.Taxed ? Math.Round(total * 18 / 118, 2) : 0;
                    return new { productId = p.Id, presentationId = (Guid?)null, description = p.Name, quantity, unit = p.Unit, total, itbis, cost = p.Cost };
                }).ToList();
                var saleTotal = saleLines.Sum(l => l.total);
                var saleId = Guid.CreateVersion7();
                var method = random.Next(0, 4) == 0 ? "Card" : "Cash";
                events.Add(Envelope("Abastalia.Sales.Contracts.SaleRecordedV2", saleId, utc, new
                {
                    saleId, number = ++number, shiftId = shift, cashierId = cashier, customerId = (Guid?)null, eNcf = $"E32{number:0000000000}",
                    total = saleTotal, itbis = saleLines.Sum(l => l.itbis), discount = 0m, lines = saleLines,
                    payments = new[] { new { method, amount = saleTotal } },
                }));
            }
        }
        foreach (var batch in events.Chunk(200)) await Ok(await node.PostAsJsonAsync("/ingest/v1/events", new { events = batch }));
        return today;
    }

    /// <summary>An event as the store's outbox uploads it (CloudUploader).</summary>
    private static object Envelope(string type, Guid aggregateId, DateTime occurredUtc, object payload)
    {
        var json = JsonSerializer.SerializeToNode(payload, JsonSerializerOptions.Web)!.AsObject();
        var id = Guid.CreateVersion7();
        json["eventId"] = id;
        json["aggregateId"] = aggregateId;
        json["occurredUtc"] = occurredUtc;
        return new { id, type, aggregateId, occurredUtc, payload = json };
    }

    /// <summary>Signs each call as the store's CloudClient does (Host.Cloud NodeSignature).</summary>
    private sealed class NodeSigner(Guid nodeId, ECDsa key) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
            var payload = $"{nodeId}.{seconds}.{nonce}.{request.Method.Method.ToUpperInvariant()} {request.RequestUri!.PathAndQuery} {Base64Url(SHA256.HashData(body))}";
            var signature = Base64Url(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));
            request.Headers.Authorization = new AuthenticationHeaderValue("Node", $"{nodeId}.{seconds}.{nonce}.{signature}");
            return await base.SendAsync(request, cancellationToken);
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string TaliaAnswer(JsonElement top)
    {
        var es = CultureInfo.InvariantCulture;
        var items = top.EnumerateArray().Take(3).Select(p => (Name: p.GetProperty("description").GetString(), Total: p.GetProperty("total").GetDecimal())).ToList();
        items.Count.ShouldBe(3, top.ToString());
        return $"Esta semana lo más vendido fue {items[0].Name}: RD$ {items[0].Total.ToString("#,##0.00", es)}. " +
               $"Le siguen {items[1].Name} (RD$ {items[1].Total.ToString("#,##0.00", es)}) y {items[2].Name} (RD$ {items[2].Total.ToString("#,##0.00", es)}).";
    }

    // ── Takes ──

    /// <summary>One screencast: each JPEG frame as it comes, named by the milliseconds since the take started (a still screen sends none;
    /// montaje.py holds the last one), and marks.json with when each shot starts and "end".</summary>
    /// <remarks>Chromium's screencast over CDP, not page.Screencast: in 1.63 its OnFrame stops after the first frames unless it also records a
    /// .webm, and then the frames are the viewport's CSS size; CDP gives them at the device scale (sharper text, room to zoom).</remarks>
    private sealed class Take : IAsyncDisposable
    {
        private readonly ICDPSession _cdp;
        private readonly string _folder;
        private readonly long _start = Now();
        private readonly Dictionary<string, double> _marks = [];

        private Take(ICDPSession cdp, string folder) => (_cdp, _folder) = (cdp, folder);

        public static async Task<Take> StartAsync(IPage page, string name, int width, int height)
        {
            var take = new Take(await page.Context.NewCDPSessionAsync(page), Fresh(Path.Combine(Captures, name)));
            take._cdp.Event("Page.screencastFrame").OnEvent += async (_, e) =>
            {
                var frame = e!.Value;
                // When it arrived, on the same clock as the marks.
                await File.WriteAllBytesAsync(Path.Combine(take._folder, $"{Now() - take._start:0000000}.jpg"), Convert.FromBase64String(frame.GetProperty("data").GetString()!));
                await take._cdp.SendAsync("Page.screencastFrameAck", new() { ["sessionId"] = frame.GetProperty("sessionId").GetInt32() });
            };
            await take._cdp.SendAsync("Page.startScreencast", new() { ["format"] = "jpeg", ["quality"] = 92, ["maxWidth"] = width, ["maxHeight"] = height });
            return take;
        }

        public void Mark(string name) => _marks[name] = (Now() - _start) / 1000.0;

        public async ValueTask DisposeAsync()
        {
            Mark("end");
            await _cdp.SendAsync("Page.stopScreencast");
            await _cdp.DetachAsync();
            await File.WriteAllTextAsync(Path.Combine(_folder, "marks.json"), JsonSerializer.Serialize(_marks));
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static string Fresh(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        return Directory.CreateDirectory(folder).FullName;
    }

    /// <summary>The pace of the video: a person reads the screen between actions.</summary>
    private static Task Pause(int ms) => Task.Delay(ms);

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.ShouldBeTrue($"{r.RequestMessage?.Method} {r.RequestMessage?.RequestUri?.PathAndQuery} → {(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
