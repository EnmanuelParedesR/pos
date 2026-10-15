using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Client.UI.Api;
using Toki.Client.UI.Components;
using Toki.Client.UI.Pages;
using Toki.Client.UI.State;
using Toki.Hardware;

namespace Toki.Client.UI.Tests;

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
        var (ctx, _, hw) = Create(new(Printer: new("EPSON TM-T20")), "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        var menu = ctx.Render<RegisterMenu>();
        menu.FindAll("button").Single(b => b.TextContent.Contains("Abrir la gaveta", StringComparison.Ordinal)).Click();

        var approver = ctx.Services.GetRequiredService<Approver>();
        menu.WaitForAssertion(() => approver.Current.ShouldNotBeNull().Permission.ShouldBe("register.open-drawer"));
        hw.Printer.DrawerOpenings.ShouldBe(0);
        approver.Respond(null); // cancelled
        menu.WaitForAssertion(() => menu.FindAll("button").Single(b => b.TextContent.Contains("Abrir la gaveta", StringComparison.Ordinal)).HasAttribute("disabled").ShouldBeFalse());
        hw.Printer.DrawerOpenings.ShouldBe(0);

        var (ctx2, _, hw2) = Create(new(Printer: new("EPSON TM-T20")), "register.open", "register.close", "register.cash-in", "credit.collect", "register.open-drawer");
        using var __ = ctx2;
        var allowed = ctx2.Render<RegisterMenu>();
        allowed.FindAll("button").Single(b => b.TextContent.Contains("Abrir la gaveta", StringComparison.Ordinal)).Click();
        allowed.WaitForAssertion(() => hw2.Printer.DrawerOpenings.ShouldBe(1));
        allowed.Markup.ShouldContain("Gaveta abierta.");
    }
}
