using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Shouldly;
using static Microsoft.Playwright.Assertions;

namespace Abastalia.UiTests;

/// <summary>TK-324: the Lite selling app as it is deployed. Abastalia.Client.Lite is published and sealed (seal.mjs: SRI and the CSP) for a real
/// Abastalia.Host.Lite, then served to Chromium from its own origin with the headers of its staticwebapp.config.json, as Azure Static Web Apps
/// serves it. The DGII is a stub on the browser's network (TesteCF waits for TK-141), and the app's appsettings turn sending on for it.</summary>
public sealed class LiteSellingTests : IAsyncLifetime
{
    private const string Key = "ui-provisioning-key-0123456789abcdef", Rnc = "131245678", Pin = "1234";
    /// <summary>The app's own origin, served by the routes below (nothing listens there): on 127.0.0.1, as Chromium lets a page call the host on
    /// 127.0.0.1 only from the same address space.</summary>
    private readonly string _origin = $"http://127.0.0.1:{WebProcess.FreePort()}";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"abastalia_lite_ui_{Guid.NewGuid():N}");
    private readonly List<string> _dgii = [];
    private readonly TaskCompletionSource _rfceSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _console = [];
    private WebProcess? _host;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage Page { get; set; } = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _host = await WebProcess.HostAsync("Hosts", "Abastalia.Host.Lite", new()
        {
            ["ASPNETCORE_ENVIRONMENT"] = "E2E",
            ["Database__Provider"] = "Sqlite",
            ["Database__ConnectionString"] = $"Data Source={Path.Combine(_folder, "lite.db")}",
            ["Lite__ProvisioningKey"] = Key,
            ["Lite__AppOrigins__0"] = _origin,
            ["Lite__Plans__Lite__SellingDevices"] = "2", // the test's own device sets the shop up; the browser is the other
            ["Sync__Enabled"] = "false",
            ["Outbox__Process"] = "false",
            ["Abastalia__KeysFolder"] = Path.Combine(_folder, "keys"),
            ["License__File"] = Path.Combine(_folder, "license.jws"),
            ["Serilog__MinimumLevel__Default"] = "Warning",
        }, "/health");
        _http = new HttpClient { BaseAddress = _host.Base };
        await PublishAsync(_host.Base);

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
        _context = await _browser.NewContextAsync(new() { BaseURL = _origin, ViewportSize = new() { Width = 375, Height = 812 }, Locale = "es-DO", HasTouch = true,
            Permissions = ["local-network-access"] }); // the routed origin has no address: Chromium takes it for a public site calling 127.0.0.1
        _context.SetDefaultTimeout(30_000);
        Assertions.SetDefaultExpectTimeout(30_000); // the first WebAssembly start on a cold runner
        await Traces.StartAsync(_context);
        await _context.RouteAsync($"{_origin}/**", ServeAsync);
        foreach (var dgii in new[] { "https://ecf.dgii.gov.do/**", "https://fc.dgii.gov.do/**" }) await _context.RouteAsync(dgii, DgiiAsync);
        Page = await _context.NewPageAsync();
        Page.Console += (_, m) => { if (m.Type is "error" or "warning") lock (_console) _console.Add(m.Text); };
        Page.RequestFailed += (_, r) => { lock (_console) _console.Add($"{r.Method} {r.Url} failed: {r.Failure}"); };
    }

    [Fact]
    public async Task Pair_import_the_certificate_sell_and_the_device_sends_the_e_cf()
    {
        try
        {
            await SellAsync();
        }
        catch (Exception e) when (e is PlaywrightException or ShouldAssertException or TimeoutException)
        {
            throw new InvalidOperationException($"{e.Message}\n── Host.Lite ──\n{_host?.Output()}\n── Browser ──\n{string.Join('\n', _console)}", e);
        }
    }

    private async Task SellAsync()
    {
        var tenant = Guid.CreateVersion7();
        (await Panel().PostAsJsonAsync("/internal/tenants", new { TenantId = tenant, OwnerName = "María Pérez", OwnerCode = "dueno", Pin })).EnsureSuccessStatusCode();
        await SetUpShopAsync(await CodeAsync(tenant));

        // First run: the code from the panel pairs this browser.
        await Page.GotoAsync("/");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Unir este equipo a su negocio" })).ToBeVisibleAsync();
        await Page.GetByLabel("Código de conexión").FillAsync(await CodeAsync(tenant));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Unir este equipo" }).ClickAsync();

        // The owner's .p12: read in the browser, its password never kept.
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Su certificado digital" })).ToBeVisibleAsync();
        await Page.GetByLabel("Archivo del certificado (.p12)").SetInputFilesAsync(new FilePayload { Name = "firma.p12", MimeType = "application/x-pkcs12", Buffer = P12("clave") });
        await Page.GetByLabel("Contraseña del certificado").FillAsync("clave");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Importar el certificado" }).ClickAsync();

        // The PIN, then the register.
        await Page.GetByRole(AriaRole.Button, new() { Name = "María Pérez" }).ClickAsync();
        foreach (var digit in Pin) await Page.GetByRole(AriaRole.Button, new() { Name = digit.ToString(), Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Entrar" }).ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Vender", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Abrir caja y vender" }).ClickAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Arroz Selecto 5 lb" }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Cobrar", Exact = true }).ClickAsync();
        await Page.Keyboard.PressAsync("Enter"); // the cash received is the total
        await Expect(Page.GetByText("Venta guardada · E320000000001")).ToBeVisibleAsync();

        // Right after the sale the device signs the e-CF and its RFCE, the host checks them, and the RFCE goes to the DGII (stub).
        await _rfceSent.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Ver factura" }).ClickAsync();
        await Expect(Page.GetByText("Código de seguridad:")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Img, new() { Name = "Código QR para consultar la factura en la DGII" })).ToBeVisibleAsync();
        (await Page.GetByRole(AriaRole.Link, new() { Name = "Enviar por WhatsApp" }).GetAttributeAsync("href")).ShouldStartWith("https://wa.me/?text=");

        await ScreenshotsAsync();
        lock (_console) _console.Where(m => m.Contains("Content Security Policy", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    /// <summary>For the design critique: the receipt, the register and the first run at a phone, a large phone, a tablet and a laptop.</summary>
    private async Task ScreenshotsAsync()
    {
        var folder = Directory.CreateDirectory(Path.Combine(Traces.Folder, "lite")).FullName;
        await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, "receipt-375.png") });
        await Page.Keyboard.PressAsync("Escape");
        foreach (var scheme in new[] { ColorScheme.Light, ColorScheme.Dark })
        foreach (var (width, height) in new[] { (320, 640), (375, 812), (768, 1024), (1366, 900) })
        {
            await Page.EmulateMediaAsync(new() { ColorScheme = scheme });
            await Page.SetViewportSizeAsync(width, height);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, $"sale-{width}-{scheme.ToString().ToLowerInvariant()}.png") });
        }
    }

    private HttpClient Panel()
    {
        var c = new HttpClient { BaseAddress = _host!.Base };
        c.DefaultRequestHeaders.Add("X-Provisioning-Key", Key);
        return c;
    }

    private async Task<string> CodeAsync(Guid tenant) =>
        (await (await Panel().PostAsync($"/internal/tenants/{tenant}/device-codes", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    /// <summary>What the owner does once in the back office, through a second selling device of the test's own.</summary>
    private async Task SetUpShopAsync(string code)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var paired = await _http.PostAsJsonAsync("/lite/devices/pair", new { code, publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), name = "Prueba" });
        var device = (await paired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deviceId").GetGuid();
        string? token = null;
        async Task<JsonElement> Send(HttpMethod method, string path, object? body = null)
        {
            var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body);
            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } } };
            var hash = Base64Url(SHA256.HashData(bytes));
            var (seconds, nonce) = (DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Base64Url(RandomNumberGenerator.GetBytes(16)));
            var signature = key.SignData(Encoding.UTF8.GetBytes($"{device}.{seconds}.{nonce}.{method.Method} {path} {hash}"), HashAlgorithmName.SHA256);
            request.Headers.Add("X-Device-Signature", $"{device}.{seconds}.{nonce}.{Base64Url(signature)}");
            request.Headers.Add("X-Body-Sha256", hash);
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            var r = await _http.SendAsync(request);
            var text = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{method} {path} {(int)r.StatusCode}: {text}");
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }
        token = (await Send(HttpMethod.Post, "/api/identity/sessions", new { Code = "dueno", Pin })).GetProperty("token").GetString();
        await Send(HttpMethod.Put, "/api/settings/business", new { Name = "Colmado La Esquina", Rnc, Address = "Av. Duarte 25", DiscountCapPercent = 10 });
        await Send(HttpMethod.Put, "/api/fiscal/mode", new { Environment = "TesteCF", Confirm = true });
        var category = (await Send(HttpMethod.Post, "/api/inventory/categories", new { Name = "Colmado", Color = "#0B5CAD", Notes = Array.Empty<string>() })).GetProperty("id").GetGuid();
        foreach (var (name, price) in new[] { ("Arroz Selecto 5 lb", 285m), ("Aceite de soya 1 L", 210m), ("Habichuelas rojas", 95m), ("Café Santo Domingo", 120m) })
            await Send(HttpMethod.Post, "/api/inventory/products", new
            {
                Name = name, CategoryId = category, Unit = "Unit", ItbisRate = "General18", Price = price, Cost = 0, SellWithoutStock = true, MinimumStock = 0,
                Barcodes = Array.Empty<string>(), InitialStock = 0,
            });
        await Send(HttpMethod.Post, "/api/cash/registers", new { Name = "Caja 1" });
    }

    /// <summary>A throwaway certificate for the business's RNC (SERIALNUMBER, as on a real one).</summary>
    private static byte[] P12(string password)
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest($"CN=Firmante de Prueba, SERIALNUMBER={Rnc}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.Export(X509ContentType.Pkcs12, password);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>dotnet publish runs seal.mjs: SRI in index.html, the CSP with this host's origin in staticwebapp.config.json.</summary>
    private async Task PublishAsync(Uri api)
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var project = Path.Combine(WebProcess.RepoRoot, "src", "Client", "Abastalia.Client.Lite", "Abastalia.Client.Lite.csproj");
        var info = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "publish", project, "-c", configuration, "-o", Path.Combine(_folder, "app"), $"-p:LiteApiUrl={api.ToString().TrimEnd('/')}" },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"dotnet publish failed:\n{await output}\n{await errors}");
    }

    private string Wwwroot => Path.Combine(_folder, "app", "wwwroot");

    /// <summary>As Static Web Apps: the file, or index.html for a route of the app, with the config's global headers. Sending to the DGII is on.</summary>
    private async Task ServeAsync(IRoute route)
    {
        var path = new Uri(route.Request.Url).AbsolutePath.TrimStart('/');
        var file = Path.Combine(Wwwroot, path.Replace('/', Path.DirectorySeparatorChar));
        if (path.Length == 0 || !File.Exists(file)) file = Path.Combine(Wwwroot, "index.html");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Wwwroot, "staticwebapp.config.json")))!;
        var headers = config["globalHeaders"]!.AsObject().ToDictionary(h => h.Key, h => h.Value!.GetValue<string>());
        var body = await File.ReadAllBytesAsync(file);
        if (path == "appsettings.json")
        {
            var settings = JsonNode.Parse(body)!;
            settings["Fiscal"]!["SendToDgii"] = true;
            body = Encoding.UTF8.GetBytes(settings.ToJsonString());
        }
        headers["Content-Type"] = Path.GetExtension(file) switch
        {
            ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css", ".json" => "application/json", ".wasm" => "application/wasm",
            ".svg" => "image/svg+xml", ".woff2" => "font/woff2", ".webmanifest" => "application/manifest+json", _ => "application/octet-stream",
        };
        await route.FulfillAsync(new() { Status = 200, Headers = headers, BodyBytes = body });
    }

    /// <summary>The DGII as docs/ecf-propio.md documents it: seed, token, every e-CF and RFCE accepted. Open CORS, as the real one answers.</summary>
    private async Task DgiiAsync(IRoute route)
    {
        var cors = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*", ["Access-Control-Allow-Headers"] = "authorization,accept", ["Access-Control-Allow-Methods"] = "GET,POST" };
        if (route.Request.Method == "OPTIONS")
        {
            await route.FulfillAsync(new() { Status = 204, Headers = cors });
            return;
        }
        var path = new Uri(route.Request.Url).AbsolutePath;
        lock (_dgii) _dgii.Add(path);
        if (path.EndsWith("/recepcionfc/api/recepcion/ecf", StringComparison.Ordinal)) _rfceSent.TrySetResult();
        var body = path.EndsWith("/semilla", StringComparison.Ordinal) ? "<SemillaModel><valor>v</valor><fecha>2026-10-17T10:00:00</fecha></SemillaModel>"
            : path.EndsWith("/validarsemilla", StringComparison.Ordinal) ? JsonSerializer.Serialize(new { token = "dgii", expira = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ") })
            : JsonSerializer.Serialize(new { codigo = 1, estado = "Aceptado", secuenciaUtilizada = true, mensajes = Array.Empty<string>() });
        await route.FulfillAsync(new() { Status = 200, Headers = cors, ContentType = body.StartsWith('<') ? "text/xml" : "application/json", Body = body });
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null) await Traces.StopAsync(_context, "lite-selling");
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        _http?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { } // best effort: a temp folder left behind is harmless
        catch (UnauthorizedAccessException) { }
    }
}
