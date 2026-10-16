using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Api;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages;
using Abastalia.Client.UI.State;
using Abastalia.Hardware;

namespace Abastalia.Client.UI.Tests;

/// <summary>The scale and the printer at the register (TK-117, TK-118), on the test drivers.</summary>
public sealed class PeripheralsTests
{
    private static readonly ProductByCode Chuleta = new(Guid.NewGuid(), null, "Chuleta ahumada", "Pound", "General18", 185m, null, 1m, null, null, [], 40m);

    private static (BunitContext Ctx, FakeApi Api, TestDrivers Hw) Create(HardwareSettings settings, params string[] permissions)
    {
        var (ctx, api) = ScreenContext.Create(permissions.Length == 0 ? ["sales.create", "register.open", "register.close", "register.cash-in", "credit.collect"] : permissions);
        ctx.Services.GetRequiredService<Peripherals>().SaveAsync(settings).GetAwaiter().GetResult();
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>())
           .Json("GET", "/api/inventory/products/by-code/2001", Chuleta)
           .Json("GET", "/api/inventory/products/by-code/7460000000017", RegisterTests.Salami);
        return (ctx, api, ctx.Services.GetRequiredService<TestDrivers>());
    }

    private static void Scan(IRenderedComponent<Sale> sale, string code)
    {
        sale.Find("input.sale__scanner").Input(code);
        sale.Find("form").Submit();
    }

    [Fact]
    public void A_by_the_pound_product_waits_for_a_stable_weight_then_takes_it()
    {
        var (ctx, _, hw) = Create(new(Scale: new("COM3", ScaleProtocol.Cas)));
        using var _ = ctx;
        hw.Scale.Pounds = 1.8m;
        hw.Scale.Status = ScaleStatus.Unstable;
        var sale = ctx.Render<Sale>();

        Scan(sale, "2001");
        sale.WaitForAssertion(() => sale.Find(".weigh .esc-pill").TextContent.ShouldBe("Esperando peso estable"));
        sale.Find(".weigh .esc-pill").ClassList.ShouldContain("esc-pill--warning");
        sale.Find("[role=dialog] .esc-button--primary").HasAttribute("disabled").ShouldBeTrue();

        hw.Scale.Pounds = 1.75m;
        hw.Scale.Status = ScaleStatus.Stable;
        sale.WaitForAssertion(() => sale.Find(".weigh .esc-pill").TextContent.ShouldBe("Peso estable"));
        sale.Find(".weigh__value").TextContent.ShouldContain("1.750");
        sale.Find("[role=dialog] .esc-button--primary").Click();

        var cart = ctx.Services.GetRequiredService<Cart>();
        sale.WaitForAssertion(() => cart.Lines.Count.ShouldBe(1)); // the click lands on the renderer while the scale keeps polling
        var line = cart.Lines[0];
        (line.Quantity, line.Note).ShouldBe((1.75m, null));
        line.ToSaleLine().Quantity.ShouldBe(1.75m);
    }

    [Fact]
    public void When_the_scale_does_not_answer_the_weight_is_typed_with_a_reason()
    {
        var (ctx, _, hw) = Create(new(Scale: new("COM3", ScaleProtocol.Toledo8217)));
        using var _ = ctx;
        hw.Scale.Status = ScaleStatus.Disconnected;
        var sale = ctx.Render<Sale>();

        Scan(sale, "2001");
        sale.WaitForAssertion(() => sale.Find(".weigh .esc-pill").TextContent.ShouldContain("La balanza no responde"));
        sale.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Escribir el peso", StringComparison.Ordinal)).Click();
        sale.Find("[role=dialog] input").Input("2.5");
        sale.Find("[role=dialog] .esc-button--primary").Click();
        sale.Find("[role=dialog] .esc-notice").TextContent.ShouldContain("Elija por qué");

        sale.FindAll("[aria-label=Motivo] .esc-chip")[0].Click();
        sale.Find("[role=dialog] .esc-button--primary").Click();

        var line = ctx.Services.GetRequiredService<Cart>().Lines.Single();
        line.Quantity.ShouldBe(2.5m);
        line.ToSaleLine().Note.ShouldBe("Peso escrito: La balanza no responde");
    }

    [Fact]
    public void Without_a_scale_the_weight_is_typed_as_before()
    {
        var (ctx, _, _) = Create(new());
        using var _ = ctx;
        var sale = ctx.Render<Sale>();

        Scan(sale, "2001");
        sale.Find("[role=dialog] h2").TextContent.ShouldBe("Peso en libras");
        sale.FindAll(".weigh").ShouldBeEmpty();
    }

    [Fact]
    public void A_cash_sale_prints_the_receipt_and_opens_the_drawer_in_one_job()
    {
        var (ctx, api, hw) = Create(new(Printer: new("EPSON TM-T20", 80)));
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        var receipt = RegisterTests.ReceiptFor(210m) with { Payments = [new("Efectivo", 210m, 500m)], Change = 290m };
        api.Respond("POST", "/api/sales", _ => FakeApi.Response(HttpStatusCode.Created, new SaleRegistered(cart.SaleId, 1, "E320000000001", 210m, 290m, receipt)));
        var sale = ctx.Render<Sale>();

        Scan(sale, "7460000000017");
        sale.WaitForAssertion(() => sale.Find(".sale__line"));
        sale.FindAll("button").Single(b => b.TextContent.Contains("Cobrar", StringComparison.Ordinal)).Click();
        sale.Find("[role=dialog] input[type=checkbox]").HasAttribute("checked").ShouldBeTrue(); // "Imprimir factura", on by default
        sale.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Confirmar cobro", StringComparison.Ordinal)).Click();

        sale.WaitForAssertion(() => hw.Printer.Printed.Select(p => (p.Receipt.ENcf, p.OpenDrawer)).ShouldBe([(receipt.ENcf, true)]));
        hw.Printer.DrawerOpenings.ShouldBe(0); // the kick went with the receipt
    }

    [Theory]
    [InlineData("Accepted", "Aceptada por la DGII")]
    [InlineData("Pending", "Pendiente de respuesta de la DGII")]
    public void TK208_a_31_waits_for_the_DGII_and_prints_its_answer(string status, string printed)
    {
        var (ctx, api, hw) = Create(new(Printer: new("EPSON TM-T20", 80)));
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        var receipt = RegisterTests.ReceiptFor(210m) with { ENcf = "E310000000005", Payments = [new("Tarjeta", 210m, null)] };
        api.Respond("POST", "/api/sales", _ => FakeApi.Response(HttpStatusCode.Created, new SaleRegistered(cart.SaleId, 1, receipt.ENcf, 210m, 0m, receipt)))
           .Json("POST", "/api/fiscal/documents/E310000000005/await-result", new { status, message = (string?)null });
        var sale = ctx.Render<Sale>();

        Scan(sale, "7460000000017");
        sale.WaitForAssertion(() => sale.Find(".sale__line"));
        sale.FindAll("button").Single(b => b.TextContent.Contains("Cobrar", StringComparison.Ordinal)).Click();
        sale.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Confirmar cobro", StringComparison.Ordinal)).Click();

        sale.WaitForAssertion(() => hw.Printer.Printed.Single().Receipt.DgiiStatus.ShouldBe(printed));
    }

    [Fact]
    public void TK208_a_31_the_DGII_rejected_is_not_printed_and_the_cashier_is_told()
    {
        var (ctx, api, hw) = Create(new(Printer: new("EPSON TM-T20", 80)));
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        var receipt = RegisterTests.ReceiptFor(210m) with { ENcf = "E310000000006", Payments = [new("Tarjeta", 210m, null)] };
        api.Respond("POST", "/api/sales", _ => FakeApi.Response(HttpStatusCode.Created, new SaleRegistered(cart.SaleId, 1, receipt.ENcf, 210m, 0m, receipt)))
           .Json("POST", "/api/fiscal/documents/E310000000006/await-result", new { status = "Rejected", message = "Error" });
        var sale = ctx.Render<Sale>();

        Scan(sale, "7460000000017");
        sale.WaitForAssertion(() => sale.Find(".sale__line"));
        sale.FindAll("button").Single(b => b.TextContent.Contains("Cobrar", StringComparison.Ordinal)).Click();
        sale.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Confirmar cobro", StringComparison.Ordinal)).Click();

        sale.WaitForAssertion(() => sale.Markup.ShouldContain("La DGII rechazó esta factura"));
        hw.Printer.Printed.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_printing_off_a_cash_sale_only_opens_the_drawer_and_the_choice_is_remembered()
    {
        var (ctx, api, hw) = Create(new(Printer: new("COM4", 58)));
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        var receipt = RegisterTests.ReceiptFor(210m) with { Payments = [new("Efectivo", 210m, 210m)] };
        api.Respond("POST", "/api/sales", _ => FakeApi.Response(HttpStatusCode.Created, new SaleRegistered(cart.SaleId, 1, "E320000000001", 210m, 0m, receipt)));
        var sale = ctx.Render<Sale>();

        Scan(sale, "7460000000017");
        sale.WaitForAssertion(() => sale.Find(".sale__line"));
        sale.FindAll("button").Single(b => b.TextContent.Contains("Cobrar", StringComparison.Ordinal)).Click();
        sale.Find("[role=dialog] input[type=checkbox]").Change(false);
        sale.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Confirmar cobro", StringComparison.Ordinal)).Click();

        sale.WaitForAssertion(() => hw.Printer.DrawerOpenings.ShouldBe(1));
        hw.Printer.Printed.ShouldBeEmpty();
        ctx.Services.GetRequiredService<Peripherals>().Settings.PrintAfterSale.ShouldBeFalse();
        (await ctx.Services.GetRequiredService<ILocalStore>().ReadAsync("hardware") ?? "").ShouldContain("\"printAfterSale\":false");
    }

    [Fact]
    public void A_failed_print_keeps_the_sale_and_says_how_to_reprint()
    {
        var (ctx, api, hw) = Create(new(Printer: new("EPSON TM-T20")));
        using var _ = ctx;
        hw.Printer.Offline = true;
        var cart = ctx.Services.GetRequiredService<Cart>();
        api.Respond("POST", "/api/sales", _ => FakeApi.Response(HttpStatusCode.Created, new SaleRegistered(cart.SaleId, 1, "E320000000001", 210m, 0m, RegisterTests.ReceiptFor(210m))));
        var sale = ctx.Render<Sale>();

        Scan(sale, "7460000000017");
        sale.WaitForAssertion(() => sale.Find(".sale__line"));
        sale.FindAll("button").Single(b => b.TextContent.Contains("Cobrar", StringComparison.Ordinal)).Click();
        sale.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Confirmar cobro", StringComparison.Ordinal)).Click();

        sale.WaitForAssertion(() => sale.Markup.ShouldContain("No se pudo imprimir la factura."));
        sale.Markup.ShouldContain("Venta guardada");
    }

    [Fact]
    public void The_receipt_dialog_reprints_without_opening_the_drawer()
    {
        var (ctx, _, hw) = Create(new(Printer: new("EPSON TM-T20")));
        using var _ = ctx;
        var receipt = RegisterTests.ReceiptFor(210m);
        var dialog = ctx.Render<ReceiptDialog>(p => p.Add(x => x.Receipt, receipt));

        dialog.FindAll("button").Single(b => b.TextContent.Contains("Imprimir", StringComparison.Ordinal)).Click();

        dialog.WaitForAssertion(() => hw.Printer.Printed.ShouldBe([(receipt, false)]));
        dialog.Markup.ShouldContain("Factura enviada a la impresora.");
    }

    [Fact]
    public void Opening_the_drawer_without_a_sale_needs_the_permission_or_a_supervisor()
    {
        // TK-239: the server records the opening (with its reason) before the drawer opens; without the permission it asks for a supervisor.
        var (ctx, api, hw) = Create(new(Printer: new("EPSON TM-T20")), "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        var route = $"/api/cash/shifts/{ScreenContext.Shift.ShiftId}/drawer-openings";
        api.Respond("POST", route, body => body.Contains("\"supervisorApprovalId\":null", StringComparison.Ordinal)
            ? FakeApi.Problem(HttpStatusCode.Forbidden, "Approval.Required", "Hace falta un supervisor.", new { permission = new[] { "register.open-drawer" } })
            : FakeApi.Response(HttpStatusCode.OK, new { openingId = Guid.NewGuid() }));
        var menu = ctx.Render<RegisterMenu>();
        Entry(menu).Click();
        Confirm(menu).Click();
        menu.Markup.ShouldContain("Escriba el motivo.");
        api.Requests.ShouldNotContain(r => r.Route == route);

        menu.Find("input[maxlength='200']").Input("Cambio para un cliente");
        Confirm(menu).Click();
        var approver = ctx.Services.GetRequiredService<Approver>();
        menu.WaitForAssertion(() => approver.Current.ShouldNotBeNull().Permission.ShouldBe("register.open-drawer"));
        hw.Printer.DrawerOpenings.ShouldBe(0);
        approver.Respond(null); // cancelled: nothing opens
        menu.WaitForAssertion(() => Confirm(menu).HasAttribute("disabled").ShouldBeFalse());
        hw.Printer.DrawerOpenings.ShouldBe(0);

        Confirm(menu).Click();
        menu.WaitForAssertion(() => approver.Current.ShouldNotBeNull());
        approver.Respond(Guid.NewGuid());
        menu.WaitForAssertion(() => hw.Printer.DrawerOpenings.ShouldBe(1));
        menu.Markup.ShouldContain("Gaveta abierta.");
        api.Requests.Last(r => r.Route == route).Body.ShouldContain("\"reason\":\"Cambio para un cliente\"");

        // The server refuses (shift closed elsewhere): the drawer stays shut.
        var (ctx2, api2, hw2) = Create(new(Printer: new("EPSON TM-T20")), "register.open-drawer");
        using var __ = ctx2;
        api2.Respond("POST", route, _ => FakeApi.Problem(HttpStatusCode.Conflict, "CashRegister.ShiftClosed", "El turno ya está cerrado."));
        var closed = ctx2.Render<RegisterMenu>();
        Entry(closed).Click();
        closed.Find("input[maxlength='200']").Input("Revisar billetes");
        Confirm(closed).Click();
        closed.WaitForAssertion(() => closed.Markup.ShouldContain("El turno ya está cerrado."));
        hw2.Printer.DrawerOpenings.ShouldBe(0);
    }

    [Fact]
    public void The_Z_prints_on_the_thermal_printer_after_closing_and_from_Turnos()
    {
        // TK-239: the closing's result and Admin → Turnos print the Z on this PC's printer, at its width.
        var (ctx, api, hw) = Create(new(Printer: new("EPSON TM-T20", 58)), "register.close", "register.approve-close", "shifts.view");
        using var _ = ctx;
        var shift = ScreenContext.Shift.ShiftId;
        var supervisor = Guid.NewGuid();
        var z = new ZReport(shift, 7, "Caja 1", "Yudelka M.", DateTime.UtcNow.AddHours(-8), DateTime.UtcNow, 500m,
            [new MethodTotal(PaymentMethod.Cash, 2500m, 0, 0, 0, 2500m)], 10, 1, 0, 0, 0, 0, 0, 3000m, 2950m, -50m, supervisor, "#42 abc", DrawerOpenings: 2);
        api.Json("GET", "/api/cash/denominations", new[] { 1000m, 500m })
           .Json("POST", $"/api/cash/shifts/{shift}/close", z)
           .Json("GET", $"/api/cash/shifts/{shift}/z", z)
           .Json("GET", "/api/reports/shifts", new[] { new ShiftSummary(shift, "Caja 1", "Yudelka M.", z.OpenedUtc, z.ClosedUtc, 7, 500m, 3000m, 2950m, -50m) })
           .Json("GET", "/api/settings/business", new BusinessProfile("Carnicería La Esquina", "131245678", null, null, null, 10m))
           .Json("GET", "/api/identity/users/active", new[] { new ActiveUser(supervisor, "Ramón D.", "02") });

        var close = ctx.Render<CloseRegister>();
        close.WaitForAssertion(() => close.Find("input[aria-label='Cantidad de 1,000']").Input("3"));
        close.FindAll("button").Single(b => b.TextContent.Contains("Confirmar conteo", StringComparison.Ordinal)).Click();
        close.FindAll("button").Single(b => b.TextContent.Contains("Sí, cerrar", StringComparison.Ordinal)).Click();
        close.WaitForAssertion(() => close.FindAll("button").Single(b => b.TextContent.Contains("Imprimir el cierre", StringComparison.Ordinal)).Click());
        close.WaitForAssertion(() => close.Markup.ShouldContain("Cierre impreso."));
        var printed = hw.Printer.Reports.ShouldHaveSingleItem();
        (printed.BusinessName, printed.Title).ShouldBe(("Carnicería La Esquina", "Cierre Z #7"));
        var text = Abastalia.Hardware.EscPos.EscPos.Text(printed, 32);
        text.ShouldContain("Faltaron");
        text.ShouldContain("Ramón D.");
        text.ShouldContain("Aperturas de gaveta sin venta  2");

        var shifts = ctx.Render<Abastalia.Client.UI.Pages.Admin.Shifts>();
        shifts.WaitForAssertion(() => shifts.Find("tbody tr.selectable").Click());
        shifts.WaitForAssertion(() => shifts.FindAll("button").Single(b => b.TextContent.Contains("Imprimir en la impresora de la caja", StringComparison.Ordinal)).Click());
        shifts.WaitForAssertion(() => hw.Printer.Reports.Count.ShouldBe(2));
        hw.Printer.Offline = true;
        shifts.FindAll("button").Single(b => b.TextContent.Contains("Imprimir en la impresora de la caja", StringComparison.Ordinal)).Click();
        shifts.WaitForAssertion(() => shifts.Markup.ShouldContain("No se pudo imprimir."));
    }

    private static AngleSharp.Dom.IElement Entry(IRenderedComponent<RegisterMenu> menu) =>
        menu.FindAll("button").Single(b => b.TextContent.Contains("Abrir la gaveta sin venta", StringComparison.Ordinal));

    private static AngleSharp.Dom.IElement Confirm(IRenderedComponent<RegisterMenu> menu) =>
        menu.FindAll("button").Single(b => b.TextContent.Contains("Abrir la gaveta", StringComparison.Ordinal) && !b.TextContent.Contains("sin venta", StringComparison.Ordinal));

    [Fact]
    public async Task The_register_reports_only_the_kind_of_scale_and_printer_once_per_change()
    {
        var (ctx, api, _) = Create(new(Printer: new("EPSON TM-T20", 58), Scale: new("COM3", ScaleProtocol.Cas)));
        using var _ = ctx;
        api.Respond("PUT", "/api/identity/devices/current/peripherals", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var peripherals = ctx.Services.GetRequiredService<Peripherals>();
        var client = ctx.Services.GetRequiredService<AbastaliaApi>();
        List<string> Reports() => [.. api.Requests.Where(r => r.Route == "/api/identity/devices/current/peripherals").Select(r => r.Body)];

        await peripherals.ReportAsync(client);
        await peripherals.ReportAsync(client); // nothing changed: not sent again
        Reports().ShouldHaveSingleItem().ShouldBe("{\"scaleProtocol\":\"Cas\",\"printerWidth\":58}"); // never the port or the printer's name

        await peripherals.SaveAsync(new());
        await peripherals.ReportAsync(client);
        Reports().Last().ShouldBe("{\"scaleProtocol\":null,\"printerWidth\":null}");
    }
}
