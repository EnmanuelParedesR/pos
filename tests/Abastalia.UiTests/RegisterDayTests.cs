using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using static Microsoft.Playwright.Assertions;

namespace Abastalia.UiTests;

/// <summary>TK-123 (R7 #13c): a whole register day in the browser against the real API, with the keyboard as a cashier works:
/// F-keys, Enter, Esc, digits and Tab. A button without a key is reached as Tab would reach it (focus, then Enter).
/// Every wait is on something the screen shows; nothing sleeps.</summary>
public sealed class RegisterDayTests(RegisterApp app) : IClassFixture<RegisterApp>
{
    private const string AdminPin = "1234", CashierPin = "4321";
    private const string Soda = "7461234567890", SodaPack = "7461234567906";
    private static readonly Guid CashierRole = new("0199e000-0000-7000-8000-000000000004"); // Cajero con apertura

    private IPage Page => app.Page;
    private IKeyboard Keys => app.Page.Keyboard;
    private ILocator Scanner => Page.GetByLabel("Código de barras o PLU");
    private ILocator Lines => Page.GetByRole(AriaRole.List, new() { Name = "Líneas" }).GetByRole(AriaRole.Listitem);
    /// <summary>The cart's "sale saved" panel (the outer status: the notice inside it is one too).</summary>
    private ILocator Saved => Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Venta guardada" }).First;

    [Fact]
    public async Task A_register_day_by_keyboard()
    {
        try
        {
            await DayAsync();
        }
        catch (Exception e) when (e is PlaywrightException or ShouldAssertException or TimeoutException)
        {
            throw new InvalidOperationException($"{e.Message}\n{app.Logs()}", e);
        }
        app.Api.Errors500.ShouldBeEmpty();
    }

    private async Task DayAsync()
    {
        // ── First install on this PC: the owner ──
        await Page.GotoAsync("/");
        await PressOnAsync(Page.GetByRole(AriaRole.Tab, new() { Name = "Primera instalación" }));
        await Page.GetByLabel("Su nombre").FocusAsync();
        await Keys.TypeAsync("Dueña");
        await Keys.PressAsync("Tab");
        await Keys.TypeAsync("admin");
        await Keys.PressAsync("Tab");
        await Keys.TypeAsync(AdminPin);
        await Keys.PressAsync("Tab");
        await Keys.PressAsync("Enter"); // "Nombre de este equipo" keeps "PC principal"; Enter submits the form
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "¿Quién entra?" })).ToBeVisibleAsync();

        // The back office (catalog, users, the register) is set up over the API with the owner's session on this same PC.
        var device = JsonDocument.Parse(await File.ReadAllTextAsync(app.ClientFile)).RootElement.GetProperty("device").GetString()!;
        var admin = app.Api.Client(device, (await Ok(await app.Api.Client(device).PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin = AdminPin })))
            .GetProperty("token").GetString());
        var cashierId = (await Ok(await admin.PostAsJsonAsync("/api/identity/users", new { Name = "Cajera", Code = "caja", Pin = CashierPin, RoleId = CashierRole })))
            .GetProperty("id").GetGuid();
        await Ok(await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Carnicería La Esquina", Rnc = "131245678", DiscountCapPercent = 10m }));
        foreach (var type in new[] { 31, 32, 34 })
            await Ok(await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = type, From = 1, To = 1000, ExpiresOn = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1) }));
        await Ok(await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja 1" }));
        var meats = (await Ok(await admin.PostAsJsonAsync("/api/inventory/categories", new { Name = "Carnes", Color = "#B42318", Notes = Array.Empty<string>() }))).GetProperty("id").GetGuid();
        var grocery = (await Ok(await admin.PostAsJsonAsync("/api/inventory/categories", new { Name = "Colmado", Color = "#0B5CAD", Notes = Array.Empty<string>() }))).GetProperty("id").GetGuid();
        async Task<Guid> Product(string name, Guid category, decimal price, string unit, string rate, string? plu, params string[] barcodes) =>
            (await Ok(await admin.PostAsJsonAsync("/api/inventory/products", new
            {
                Name = name, CategoryId = category, Unit = unit, Plu = plu, ItbisRate = rate, Price = price, WholesalePrice = (decimal?)null, Cost = 0m,
                SellWithoutStock = false, MinimumStock = 0, Barcodes = barcodes, InitialStock = 500m,
            }))).GetProperty("id").GetGuid();
        await Product("Chuleta", meats, 165m, "Pound", "Exempt", "1234");
        var soda = await Product("Refresco 2L", grocery, 59m, "Unit", "General18", null, Soda);
        var pack = (await Ok(await admin.PostAsJsonAsync($"/api/inventory/products/{soda}/presentations", new { Name = "Fardo 8", Factor = 8m, Price = 440m }))).GetProperty("id").GetGuid();
        await Ok(await admin.PostAsJsonAsync($"/api/inventory/products/{soda}/barcodes", new { Code = SodaPack, PresentationId = pack }));
        await Ok(await admin.PostAsJsonAsync("/api/customers", new { Name = "Colmado Vecino", CreditLimit = 3000m }));

        // ── The cashier signs in with her PIN and opens the register counting the float by denomination ──
        await Page.GotoAsync("/sign-in");
        await SignInAsync("Cajera");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Abrir caja" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Caja 1" })).ToHaveAttributeAsync("aria-pressed", "true");
        await CountAsync(1000, 2);
        await CountAsync(100, 5);
        await Expect(Page.GetByText("2,500.00")).ToBeVisibleAsync();
        await Keys.PressAsync("F12");

        // ── Sale 1: barcode, F3 quantity, PLU on the scale, F7 discount over the cap, F4 price change; cash with centavos ──
        await ScanAsync(Soda, lines: 1);
        await Keys.PressAsync("F3");
        await TypeInDialogAsync("Cantidad", "2");
        await Expect(Lines.Nth(0)).ToContainTextAsync("118.00");
        await ScanCodeAsync("1234");
        var weigh = Page.GetByRole(AriaRole.Dialog, new() { Name = "Pesar" });
        await Expect(weigh.GetByText("Peso estable")).ToBeVisibleAsync();
        await Keys.PressAsync("Enter");
        await Expect(Lines).ToHaveCountAsync(2);
        await Expect(Lines.Nth(1)).ToContainTextAsync("412.50");
        await Keys.PressAsync("F7"); // the new line is the selected one
        await TypeInDialogAsync("Descuento", "15");
        await Expect(Lines.Nth(1)).ToContainTextAsync("350.62");
        await Keys.PressAsync("ArrowUp");
        await Keys.PressAsync("F4");
        await TypeInDialogAsync("Cambiar precio", "60");
        await Expect(Lines.Nth(0)).ToContainTextAsync("120.00");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Venta actual" })).ToContainTextAsync("470.62");
        await ChargeCashAsync("500", "Descuento mayor que el tope", "Cambiar el precio");
        await Expect(Saved).ToContainTextAsync("29.00"); // 500 − 470.62: the 0.38 stays in the drawer as rounding

        // ── Sale 2: a weight label, a price label and a presentation; card plus cash ──
        await ScanAsync(Label("20", "01234", "01500"), lines: 1);
        await ScanAsync(Label("22", "1234", "033000"), lines: 2);
        await ScanAsync(SodaPack, lines: 3);
        await Expect(Lines.Nth(0)).ToContainTextAsync("247.50");
        await Expect(Lines.Nth(1)).ToContainTextAsync("330.00");
        await Expect(Lines.Nth(2)).ToContainTextAsync("440.00");
        var checkout = await OpenCheckoutAsync();
        await Keys.PressAsync("F6");
        await Expect(checkout.GetByLabel("Monto")).ToBeFocusedAsync();
        await Keys.TypeAsync("500");
        var partial = checkout.GetByRole(AriaRole.Button, new() { Name = "Agregar este pago" });
        await Expect(partial).ToBeVisibleAsync(); // it shows once the amount is less than the total
        await Expect(partial).ToBeDisabledAsync(); // R11 A16: the card authorization first
        await Keys.PressAsync("Tab");
        await Keys.TypeAsync("483920");
        await Expect(partial).ToBeEnabledAsync();
        await Keys.PressAsync("Tab");
        await Expect(partial).ToBeFocusedAsync();
        await Keys.PressAsync("Enter");
        await Expect(checkout).ToContainTextAsync("517.50"); // still to pay
        var cash = checkout.GetByLabel("Efectivo recibido");
        await Expect(cash).ToBeFocusedAsync();
        await Expect(cash).ToHaveValueAsync("518");
        await Keys.TypeAsync("600");
        await Keys.PressAsync("Enter");
        await Expect(Saved).ToContainTextAsync("82.00");

        // ── Sale 3: on account (fiado) for a customer found with F6 ──
        await Keys.PressAsync("F6");
        var customer = Page.GetByRole(AriaRole.Dialog, new() { Name = "Cliente y comprobante" });
        await Expect(customer.GetByLabel("Buscar cliente")).ToBeFocusedAsync();
        await Keys.TypeAsync("Colmado");
        await Keys.PressAsync("Enter");
        var row = customer.GetByRole(AriaRole.Row).Filter(new() { HasText = "Colmado Vecino" });
        await Expect(row).ToBeVisibleAsync();
        await Keys.PressAsync("Tab");
        await Keys.PressAsync("Tab");
        await Expect(row).ToBeFocusedAsync();
        await Keys.PressAsync("Enter");
        await Expect(customer.GetByText("Límite")).ToBeVisibleAsync();
        await Keys.PressAsync("Escape");
        await Expect(customer).ToBeHiddenAsync();
        await ScanAsync(Soda, lines: 1);
        checkout = await OpenCheckoutAsync();
        await Keys.PressAsync("F8");
        await Expect(checkout.GetByLabel("Monto")).ToHaveValueAsync("59.00");
        await Expect(checkout.GetByLabel("Monto")).ToBeFocusedAsync();
        await Keys.PressAsync("Enter");
        await ApproveAsync("a crédito RD$ 59.00 a Colmado Vecino"); // R11 A6: selling on account needs credit.sell, the cashier does not have it
        await Expect(Saved).ToBeVisibleAsync();

        // ── Hold a sale and resume it ──
        await ScanAsync(Soda, lines: 1);
        await Keys.PressAsync("F5");
        var hold = Page.GetByRole(AriaRole.Dialog, new() { Name = "Poner la venta en espera" });
        await Expect(hold.GetByLabel("Nombre para encontrarla")).ToBeFocusedAsync();
        await Keys.PressAsync("Control+A");
        await Keys.TypeAsync("Señora del pollo");
        await Keys.PressAsync("Enter");
        await Expect(hold).ToBeHiddenAsync();
        await Expect(Lines).ToHaveCountAsync(0);
        await Keys.PressAsync("F5");
        var held = Page.GetByRole(AriaRole.Dialog, new() { Name = "Ventas en espera" });
        await PressOnAsync(held.GetByRole(AriaRole.Row).Filter(new() { HasText = "Señora del pollo" }).GetByRole(AriaRole.Button, new() { Name = "Retomar" }));
        await Expect(held).ToBeHiddenAsync();
        await Expect(Lines).ToHaveCountAsync(1);
        await ChargeCashAsync(null);
        await Expect(Saved).ToBeVisibleAsync();

        // ── A cash withdrawal, which needs the supervisor ──
        await Keys.PressAsync("F9");
        var menu = Page.GetByRole(AriaRole.Dialog, new() { Name = "Caja" });
        await Expect(menu).ToBeFocusedAsync(); // a dialog without fields opens with the focus on itself
        await Keys.PressAsync("Tab");
        await Expect(menu.GetByRole(AriaRole.Button, new() { Name = "Entrada de efectivo" })).ToBeFocusedAsync();
        await Keys.PressAsync("Tab");
        await Expect(menu.GetByRole(AriaRole.Button, new() { Name = "Salida de efectivo" })).ToBeFocusedAsync();
        await Keys.PressAsync("Enter");
        var withdrawal = Page.GetByRole(AriaRole.Dialog, new() { Name = "Salida de efectivo" });
        await Expect(withdrawal.GetByLabel("Monto RD$")).ToBeFocusedAsync();
        await Keys.TypeAsync("300");
        await Keys.PressAsync("Enter");
        await ApproveAsync("Sacar efectivo de la caja");
        await Expect(Page.GetByText("Registrado: RD$ 300.00.")).ToBeVisibleAsync();
        await Keys.PressAsync("Escape");
        await Expect(menu).ToBeHiddenAsync();

        // ── The session ends on the server (the owner resets the PIN): back to the PIN, the cart intact ──
        await Expect(Scanner).ToBeFocusedAsync();
        await ScanAsync(Soda, lines: 1);
        await Ok(await admin.PutAsJsonAsync($"/api/identity/users/{cashierId}", new { Name = "Cajera", RoleId = CashierRole, IsActive = true, Pin = CashierPin }));
        await ScanCodeAsync(Soda);
        await Expect(Page.GetByText("Su sesión terminó. Entre de nuevo con su PIN.")).ToBeVisibleAsync();
        await SignInAsync("Cajera");
        await Expect(Lines).ToHaveCountAsync(1);
        await Expect(Lines.Nth(0)).ToContainTextAsync("Refresco 2L");

        // ── A scan by habit while the checkout is open is ignored (escuadra.js): no payment of 7,461,234 ──
        checkout = await OpenCheckoutAsync();
        cash = checkout.GetByLabel("Efectivo recibido");
        await Expect(cash).ToHaveValueAsync("59");
        await Keys.TypeAsync(Soda + "\n"); // a scanner: 13 digits and Enter within a few milliseconds
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Se ignoró un escaneo" })).ToBeVisibleAsync();
        await Expect(cash).ToHaveValueAsync("59");
        await Expect(checkout).ToBeVisibleAsync();

        // ── The store server goes down while charging: amber, the cart stays; when it is back, the charge goes through once ──
        await app.Api.StopAsync();
        await Keys.PressAsync("Enter");
        await Expect(checkout.GetByText("No se sabe si la venta se guardó")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Sin conexión con el servidor · reintentando")).ToBeVisibleAsync();
        await Keys.PressAsync("Escape");
        await Expect(checkout).ToBeHiddenAsync();
        await Expect(Lines).ToHaveCountAsync(1);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Cobrar" })).ToBeDisabledAsync();
        await app.Api.LaunchAsync();
        checkout = await OpenCheckoutAsync();
        await Expect(checkout.GetByText("El último intento de cobro no recibió respuesta")).ToBeVisibleAsync();
        await Keys.PressAsync("Enter");
        await Expect(Saved).ToBeVisibleAsync();
        await Expect(Page.GetByText("En línea")).ToBeVisibleAsync();

        // ── Shift sales: void the last one, return one soda of the first ──
        await Keys.PressAsync("F9");
        await PressOnAsync(menu.GetByRole(AriaRole.Button, new() { Name = "Ventas de este turno" }));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Ventas de este turno" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Contado" })).ToHaveCountAsync(4); // five sales, one on account: none charged twice
        var detail = Page.GetByRole(AriaRole.Region, new() { Name = "Detalle" });

        await PressOnAsync(Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Contado" }).Filter(new() { HasText = "59.00" }).First);
        await PressOnAsync(detail.GetByRole(AriaRole.Button, new() { Name = "Anular venta" }));
        await Expect(detail.GetByLabel("Motivo")).ToBeFocusedAsync();
        await Keys.TypeAsync("cliente se arrepintió");
        await PressOnAsync(detail.GetByRole(AriaRole.Button, new() { Name = "Anular venta" }));
        await ApproveAsync("Anular una venta");
        await Expect(detail.GetByText("Venta anulada.")).ToBeVisibleAsync();

        await PressOnAsync(Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "470.62" }));
        await PressOnAsync(detail.GetByRole(AriaRole.Button, new() { Name = "Devolver líneas" }));
        await Expect(detail.GetByLabel("Motivo")).ToBeFocusedAsync();
        await Keys.TypeAsync("venía rota");
        await detail.GetByLabel("Devolver de Refresco 2L").FocusAsync(); // Tab, Tab… to the soda's quantity
        await Keys.TypeAsync("1");
        await PressOnAsync(detail.GetByRole(AriaRole.Button, new() { Name = "Devolver", Exact = true }));
        await ApproveAsync("Devolver mercancía");
        await Expect(detail.GetByText("Devolución registrada.")).ToBeVisibleAsync();
        await Expect(detail.GetByRole(AriaRole.Status).Filter(new() { HasText = "Devuelva al cliente" })).ToContainTextAsync("60.00");
        await Keys.PressAsync("Escape");

        // ── Blind close: count, F12 twice, the supervisor; the Z on the screen is the server's ──
        await Expect(Scanner).ToBeFocusedAsync();
        await Keys.PressAsync("F9");
        await PressOnAsync(menu.GetByRole(AriaRole.Button, new() { Name = "Cerrar caja" }));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Cerrar caja" })).ToBeVisibleAsync();
        var registers = await Ok(await admin.GetAsync("/api/cash/registers"));
        var shiftId = registers.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Caja 1").GetProperty("openShiftId").GetGuid();
        // 2,500 float + 471 (sale 1) + 518 (sale 2) + 59 + 59 (held, offline) − 59 voided − 300 withdrawn − 60 returned.
        (await Ok(await admin.GetAsync($"/api/cash/shifts/{shiftId}/z"))).GetProperty("expectedCash").GetDecimal().ShouldBe(3188m);
        await CountAsync(2000, 1);
        await CountAsync(1000, 1);
        await CountAsync(100, 1);
        await CountAsync(50, 1);
        await CountAsync(25, 1);
        await CountAsync(10, 1);
        await CountAsync(1, 2); // 3,188 minus one peso: the drawer is short
        await Keys.PressAsync("F12");
        await Expect(Page.GetByText("¿Cerrar con RD$ 3,187.00 contado?")).ToBeVisibleAsync();
        await Keys.PressAsync("F12");
        await ApproveAsync("Cerrar turno con diferencia");
        await Expect(Page.GetByText("Faltaron RD$ 1.00.")).ToBeVisibleAsync();

        var z = await Ok(await admin.GetAsync($"/api/cash/shifts/{shiftId}/z"));
        var kpis = Page.Locator(".kpi__value");
        await Expect(kpis).ToHaveTextAsync([Amount(z.GetProperty("expectedCash")), Amount(z.GetProperty("countedCash")), Amount(z.GetProperty("difference"))]);
        await Expect(Page.GetByText($"Cierre Z #{z.GetProperty("zNumber").GetInt64()}")).ToBeVisibleAsync();
        z.GetProperty("difference").GetDecimal().ShouldBe(-1m);
    }

    // ── keyboard helpers ──

    /// <summary>What Tab and Enter do on a button without a key of its own.</summary>
    private async Task PressOnAsync(ILocator target)
    {
        await target.FocusAsync();
        await Keys.PressAsync("Enter");
    }

    private async Task SignInAsync(string name)
    {
        await PressOnAsync(Page.GetByRole(AriaRole.Button, new() { Name = name }));
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "PIN" })).ToBeFocusedAsync();
        await Keys.TypeAsync(CashierPin);
        await Keys.PressAsync("Enter");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "¿Quién entra?" })).ToBeHiddenAsync();
    }

    private async Task CountAsync(decimal denomination, int quantity)
    {
        await Page.GetByLabel($"Cantidad de {denomination.ToString("#,##0", CultureInfo.InvariantCulture)}", new() { Exact = true }).FocusAsync();
        await Keys.TypeAsync(quantity.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A scan as the scanner does it: the digits and Enter in a burst, into the focused scanner field.</summary>
    private async Task ScanCodeAsync(string code)
    {
        await Expect(Scanner).ToBeFocusedAsync();
        await Keys.TypeAsync(code + "\n");
    }

    private async Task ScanAsync(string code, int lines)
    {
        await ScanCodeAsync(code);
        await Expect(Lines).ToHaveCountAsync(lines);
    }

    private async Task TypeInDialogAsync(string title, string value)
    {
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = title });
        await Expect(dialog.GetByRole(AriaRole.Textbox)).ToBeFocusedAsync();
        await Keys.TypeAsync(value);
        await Keys.PressAsync("Enter");
        await Expect(dialog).ToBeHiddenAsync();
    }

    private async Task<ILocator> OpenCheckoutAsync()
    {
        await Expect(Scanner).ToBeFocusedAsync();
        await Keys.PressAsync("F12");
        var checkout = Page.GetByRole(AriaRole.Dialog, new() { Name = "Cobrar" });
        await Expect(checkout.GetByLabel("Efectivo recibido")).ToBeFocusedAsync();
        return checkout;
    }

    /// <summary>F12, the cash received (null: the exact amount it suggests), Enter, and a supervisor for each permission the server asks.</summary>
    private async Task ChargeCashAsync(string? received, params string[] approvals)
    {
        await OpenCheckoutAsync();
        if (received is not null) await Keys.TypeAsync(received);
        await Keys.PressAsync("Enter");
        await ApproveAsync(approvals);
    }

    /// <summary>The supervisor dialog, once per permission in whatever order the server asks: the PIN by keys and Enter.</summary>
    private async Task ApproveAsync(params string[] permissions)
    {
        var pending = permissions.ToList();
        while (pending.Count > 0)
        {
            var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Autoriza un supervisor" });
            await Expect(dialog).ToBeVisibleAsync();
            var text = await dialog.InnerTextAsync();
            var asked = pending.FirstOrDefault(text.Contains) ?? throw new ShouldAssertException($"The supervisor was asked for something else: {text}");
            pending.Remove(asked);
            await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Dueña" })).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(dialog.Locator("[data-no-scan]")).ToBeFocusedAsync();
            await Keys.TypeAsync(AdminPin);
            await Keys.PressAsync("Enter");
            await Expect(dialog.Filter(new() { HasText = asked })).ToBeHiddenAsync();
        }
    }

    // ── data helpers ──

    /// <summary>An in-store EAN-13 label: prefix, PLU, value, check digit.</summary>
    private static string Label(string prefix, string plu, string value)
    {
        var twelve = prefix + plu + value;
        var sum = twelve.Select((d, i) => (d - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return twelve + (10 - sum % 10) % 10;
    }

    private static string Amount(JsonElement e) => e.GetDecimal().ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.ShouldBeTrue($"{r.RequestMessage?.Method} {r.RequestMessage?.RequestUri?.PathAndQuery} → {(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
