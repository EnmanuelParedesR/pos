using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using static Microsoft.Playwright.Assertions;

namespace Abastalia.UiTests;

/// <summary>TK-365: one register page driven as a cashier does (keys, Tab and Enter), as RegisterDayTests does it, for the critical-action
/// suites. Selectors are roles and labels, never layout. Every wait is on something the screen shows.</summary>
public sealed class Till(IPage page)
{
    /// <summary>The TK-365 suites run one after another (each starts its own API, register host and browser), beside the other UI tests.</summary>
    public const string Suite = "TK-365 critical actions";

    public IPage Page { get; } = page;
    public IKeyboard Keys => Page.Keyboard;
    public ILocator Scanner => Page.GetByLabel("Código de barras o PLU");
    public ILocator Lines => Page.GetByRole(AriaRole.List, new() { Name = "Líneas" }).GetByRole(AriaRole.Listitem);
    /// <summary>The cart's "sale saved" panel (the outer status: the notice inside it is one too).</summary>
    public ILocator Saved => Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Venta guardada" }).First;
    public ILocator Heading(string name) => Page.GetByRole(AriaRole.Heading, new() { Name = name });

    /// <summary>What Tab and Enter do on a button without a key of its own.</summary>
    public async Task PressOnAsync(ILocator target)
    {
        await target.FocusAsync();
        await Keys.PressAsync("Enter");
    }

    /// <summary>A fresh page on ¿Quién entra?, the user's tile, the PIN by keys and Enter.</summary>
    public async Task SignInAsync(string name, string pin)
    {
        await Page.GotoAsync("/sign-in");
        await PressOnAsync(Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }));
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "PIN" })).ToBeFocusedAsync();
        await Keys.TypeAsync(pin);
        await Keys.PressAsync("Enter");
        await Expect(Heading("¿Quién entra?")).ToBeHiddenAsync();
    }

    /// <summary>Signs in and opens <paramref name="register"/> with the float counted in 1,000 bills; ends on the sale with the scanner focused.</summary>
    public async Task OpenShiftAsync(string name, string pin, string register, int thousands = 1)
    {
        await SignInAsync(name, pin);
        await Expect(Heading("Abrir caja")).ToBeVisibleAsync();
        var chip = Page.GetByRole(AriaRole.Button, new() { Name = register, Exact = true });
        await chip.ClickAsync(); // a chip has no key; the mouse is what a cashier uses here when there are several registers
        await Expect(chip).ToHaveAttributeAsync("aria-pressed", "true");
        await CountAsync(1000, thousands);
        await Keys.PressAsync("F12");
        await Expect(Scanner).ToBeFocusedAsync();
    }

    public async Task CountAsync(decimal denomination, int quantity)
    {
        await Page.GetByLabel($"Cantidad de {denomination.ToString("#,##0", CultureInfo.InvariantCulture)}", new() { Exact = true }).FocusAsync();
        await Keys.TypeAsync(quantity.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A scan as the scanner does it: the digits and Enter in a burst, into the focused scanner field.</summary>
    public async Task ScanAsync(string code, int lines)
    {
        await Expect(Scanner).ToBeFocusedAsync();
        await Keys.TypeAsync(code + "\n");
        await Expect(Lines).ToHaveCountAsync(lines);
    }

    public async Task TypeInDialogAsync(string title, string value)
    {
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = title });
        await Expect(dialog.GetByRole(AriaRole.Textbox)).ToBeFocusedAsync();
        await Keys.TypeAsync(value);
        await Keys.PressAsync("Enter");
        await Expect(dialog).ToBeHiddenAsync();
    }

    public async Task<ILocator> OpenCheckoutAsync()
    {
        await Expect(Scanner).ToBeFocusedAsync();
        await Keys.PressAsync("F12");
        var checkout = Page.GetByRole(AriaRole.Dialog, new() { Name = "Cobrar" });
        await Expect(checkout.GetByLabel("Efectivo recibido")).ToBeFocusedAsync();
        return checkout;
    }

    /// <summary>F12, the exact cash it suggests, Enter: the sale is saved.</summary>
    public async Task ChargeCashAsync()
    {
        await OpenCheckoutAsync();
        await Keys.PressAsync("Enter");
        await Expect(Saved).ToBeVisibleAsync();
    }

    /// <summary>F6, the customer found by name and chosen with Tab and Enter, Esc back to the sale.</summary>
    public async Task ChooseCustomerAsync(string name)
    {
        await Keys.PressAsync("F6");
        var customer = Page.GetByRole(AriaRole.Dialog, new() { Name = "Cliente y comprobante" });
        await Expect(customer.GetByLabel("Buscar cliente")).ToBeFocusedAsync();
        await Keys.TypeAsync(name);
        await Keys.PressAsync("Enter");
        var row = customer.GetByRole(AriaRole.Row).Filter(new() { HasText = name });
        await PressOnAsync(row);
        await Expect(customer.GetByText("Límite")).ToBeVisibleAsync();
        await Keys.PressAsync("Escape");
        await Expect(customer).ToBeHiddenAsync();
    }

    /// <summary>The supervisor dialog, once per permission in whatever order the server asks: <paramref name="supervisor"/>'s PIN by keys and Enter.</summary>
    public async Task ApproveAsync(string supervisor, string pin, params string[] permissions)
    {
        var pending = permissions.ToList();
        while (pending.Count > 0)
        {
            var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Autoriza un supervisor" });
            await Expect(dialog).ToBeVisibleAsync();
            var text = await dialog.InnerTextAsync();
            var asked = pending.FirstOrDefault(text.Contains) ?? throw new ShouldAssertException($"The supervisor was asked for something else: {text}");
            pending.Remove(asked);
            await ChooseSupervisorAsync(dialog, supervisor);
            await Keys.TypeAsync(pin);
            await Keys.PressAsync("Enter");
            await Expect(dialog.Filter(new() { HasText = asked })).ToBeHiddenAsync();
        }
    }

    public static async Task ChooseSupervisorAsync(ILocator dialog, string supervisor)
    {
        var chip = dialog.GetByRole(AriaRole.Group, new() { Name = "Supervisor" }).GetByRole(AriaRole.Button, new() { Name = supervisor, Exact = true });
        if (await chip.GetAttributeAsync("aria-pressed") != "true") await chip.ClickAsync();
        await Expect(chip).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(dialog.Locator("[data-no-scan]")).ToBeFocusedAsync();
    }
}

/// <summary>TK-365: a store set up as RegisterDayTests sets it up: the first install through the screen on the main PC, the back office over the API
/// with the owner's session (catalog, sequences, a customer). Each scenario adds its own cashiers and registers so none depends on another.</summary>
public sealed class Store
{
    public const string OwnerName = "Dueña", OwnerPin = "1234", CashierPin = "4321";
    public const string Soda = "7461234567890";
    public const string Rnc = "131245678";
    public static readonly Guid CashierWithOpening = new("0199e000-0000-7000-8000-000000000004");

    public required HttpClient Admin { get; init; }
    public required string Device { get; init; }

    public static async Task<Store> SetUpAsync(RegisterApp app)
    {
        var till = new Till(app.Page);
        await app.Page.GotoAsync("/");
        await till.PressOnAsync(app.Page.GetByRole(AriaRole.Tab, new() { Name = "Primera instalación" }));
        await app.Page.GetByLabel("Su nombre").FillAsync(OwnerName);
        await app.Page.GetByLabel("Su código para entrar").FillAsync("admin");
        await app.Page.GetByLabel("PIN (4 a 8 dígitos)").FillAsync(OwnerPin);
        await till.PressOnAsync(app.Page.GetByRole(AriaRole.Button, new() { Name = "Instalar Abastalia" }));
        await Expect(till.Heading("¿Quién entra?")).ToBeVisibleAsync();

        var device = JsonDocument.Parse(await File.ReadAllTextAsync(app.ClientFile)).RootElement.GetProperty("device").GetString()!;
        var token = (await Ok(await app.Api.Client(device).PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin = OwnerPin }))).GetProperty("token").GetString();
        var admin = app.Api.Client(device, token);
        // The address: the certificate signer needs it in the XML (EcfQueueTests).
        await Ok(await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Carnicería La Esquina", Rnc, Address = "Av. Duarte 25, Santo Domingo", DiscountCapPercent = 10m }));
        foreach (var type in new[] { 31, 32, 34 })
            await Ok(await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = type, From = 1, To = 1000, ExpiresOn = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1) }));
        var grocery = (await Ok(await admin.PostAsJsonAsync("/api/inventory/categories", new { Name = "Colmado", Color = "#0B5CAD", Notes = Array.Empty<string>() }))).GetProperty("id").GetGuid();
        await Ok(await admin.PostAsJsonAsync("/api/inventory/products", new
        {
            Name = "Refresco 2L", CategoryId = grocery, Unit = "Unit", Plu = (string?)null, ItbisRate = "General18", Price = 59m, WholesalePrice = (decimal?)null, Cost = 0m,
            SellWithoutStock = false, MinimumStock = 0, Barcodes = new[] { Soda }, InitialStock = 500m,
        }));
        return new Store { Admin = admin, Device = device };
    }

    /// <summary>A cashier with opening (sells, opens and closes their shift; no credit, withdrawals, voids nor returns) and a register of their own.</summary>
    public async Task CashierAndRegisterAsync(string cashier, string code, string register)
    {
        await Ok(await Admin.PostAsJsonAsync("/api/identity/users", new { Name = cashier, Code = code, Pin = CashierPin, RoleId = CashierWithOpening }));
        await Ok(await Admin.PostAsJsonAsync("/api/cash/registers", new { Name = register }));
    }

    public async Task<JsonElement> ShiftZAsync(string register)
    {
        var registers = await Ok(await Admin.GetAsync("/api/cash/registers"));
        var shift = registers.EnumerateArray().Single(r => r.GetProperty("name").GetString() == register).GetProperty("openShiftId").GetGuid();
        return await Ok(await Admin.GetAsync($"/api/cash/shifts/{shift}/z"));
    }

    /// <summary>A license as the vendor signs it (compact JWS, ES256), with the test API's key.</summary>
    public static string SignedLicense(System.Security.Cryptography.ECDsa key, bool blocked)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64("""{"alg":"ES256","typ":"JWT"}"""u8.ToArray());
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Store = "Carnicería La Esquina", Plan = "Negocio", Features = Array.Empty<string>(), Limits = new Dictionary<string, int> { ["Registers"] = 5 },
            IssuedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddDays(30), Blocked = blocked, Rnc,
        }, JsonSerializerOptions.Web));
        return $"{header}.{body}.{B64(key.SignData(System.Text.Encoding.ASCII.GetBytes($"{header}.{body}"), System.Security.Cryptography.HashAlgorithmName.SHA256))}";
    }

    public static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.ShouldBeTrue($"{r.RequestMessage?.Method} {r.RequestMessage?.RequestUri?.PathAndQuery} → {(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
