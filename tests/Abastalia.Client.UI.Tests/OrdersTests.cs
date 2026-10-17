using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Api;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages;
using Abastalia.Client.UI.State;
using Abastalia.Escuadra;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-193 orders, TK-194 quotes (F10 at the register) and TK-195 the catalog in the back office.</summary>
public sealed class OrdersTests
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly CustomerDetail AnaDetail = new(Ana, "Ana Pérez", null, false, "Retail", 0m, 0m, 100m, true);

    private static OrderItem Order(Guid id, DateOnly pickup, decimal deposit = 100m) => new(id, OrderKind.Order, OrderStatus.Ready, Ana, "Ana Pérez", pickup,
        new TimeOnly(10, 30), null, "picado fino", 400m, deposit, PaymentMethod.Cash, null, DateTime.UtcNow,
        [new OrderLine(RegisterTests.Salami.ProductId, null, "Salami 1 lb", "Unit", 2m, 200m, 400m, null)]);

    [Fact]
    public async Task F10_saves_the_cart_as_an_order_for_its_customer_with_a_cash_deposit()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(RegisterTests.Salami, null, 2);
        api.Json("POST", "/api/orders", new OrderCreated(Guid.NewGuid(), 420m), HttpStatusCode.Created);
        string? done = null;
        var dialog = ctx.Render<OrdersDialog>(p => p.Add(x => x.ShiftId, ScreenContext.Shift.ShiftId).Add(x => x.OnDone, m => done = m));

        // Without a customer an order cannot be saved.
        dialog.Find(".esc-notice").TextContent.ShouldContain("Elija el cliente con F6");
        dialog.Find(".esc-button--primary").HasAttribute("disabled").ShouldBeTrue();

        cart.Customer = AnaDetail;
        dialog.Render();
        dialog.Find("input[data-no-scan]").Input("200");
        dialog.Find(".esc-button--primary").Click();

        dialog.WaitForAssertion(() => done.ShouldNotBeNull());
        done!.ShouldContain("abono de RD$ 200.00");
        cart.IsEmpty.ShouldBeTrue();
        var body = api.Requests.Single(r => r.Method == "POST").Body;
        body.ShouldContain("\"kind\":\"Order\"");
        body.ShouldContain($"\"customerId\":\"{Ana}\"");
        body.ShouldContain($"\"pickupDate\":\"{Format.Today.AddDays(1):yyyy-MM-dd}\"");
        body.ShouldContain($"\"deposit\":{{\"shiftId\":\"{ScreenContext.Shift.ShiftId}\",\"method\":\"Cash\",\"amount\":200}}");
    }

    [Fact]
    public async Task A_cash_deposit_with_centavos_is_not_sent()
    {
        // R8 #6: the drawer takes whole pesos, as in a payment on credit.
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(RegisterTests.Salami, null, 2);
        cart.Customer = AnaDetail;
        var dialog = ctx.Render<OrdersDialog>(p => p.Add(x => x.ShiftId, ScreenContext.Shift.ShiftId));

        dialog.Find("input[data-no-scan]").Input("200.50");
        dialog.Find(".esc-button--primary").Click();

        dialog.WaitForAssertion(() => dialog.Markup.ShouldContain("El efectivo se recibe en pesos enteros."));
        api.Requests.ShouldNotContain(r => r.Method == "POST");
    }

    [Fact]
    public async Task Charging_an_order_loads_it_at_todays_price_and_the_checkout_takes_the_deposit_first()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        var orderId = Guid.NewGuid();
        var cart = ctx.Services.GetRequiredService<Cart>();
        api.Json("GET", "/api/orders", new[] { Order(orderId, Format.Today) })
           .Json("GET", $"/api/customers/{Ana}", AnaDetail)
           .Json("GET", "/api/inventory/products/prices", new[] { new ProductPrice(RegisterTests.Salami.ProductId, null, 210m, null) });
        string? done = null;
        var dialog = ctx.Render<OrdersDialog>(p => p.Add(x => x.ShiftId, ScreenContext.Shift.ShiftId).Add(x => x.OnDone, m => done = m));

        dialog.WaitForAssertion(() => dialog.Find("tbody tr").TextContent.ShouldContain("Ana Pérez"));
        dialog.Find("tbody tr").TextContent.ShouldContain("Abonó 100.00");
        dialog.FindAll("button").Single(b => b.TextContent.Trim() == "Cobrar").Click();

        dialog.WaitForAssertion(() => done.ShouldNotBeNull());
        done!.ShouldContain("Salami 1 lb: 200.00 → 210.00");
        done!.ShouldContain("(+20.00)");
        (cart.OrderId, cart.Deposit, cart.Total, cart.Customer?.Id).ShouldBe((orderId, 100m, 420m, Ana));

        api.Json("POST", "/api/sales", new SaleRegistered(cart.SaleId, 1, "E320000000001", 420m, 0m, RegisterTests.ReceiptFor(420m)), HttpStatusCode.Created);
        var checkout = ctx.Render<CheckoutDialog>(p => p.Add(x => x.ShiftId, ScreenContext.Shift.ShiftId));
        checkout.Find(".checkout__left").TextContent.ShouldContain("Saldo a favor");
        checkout.Find(".checkout__left").TextContent.ShouldContain("320.00"); // what is left after the deposit
        checkout.Find(".esc-button--money").Click();
        checkout.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Route == "/api/sales"));
        var sale = api.Requests.Single(r => r.Route == "/api/sales").Body;
        sale.ShouldContain($"\"orderId\":\"{orderId}\"");
        sale.ShouldContain("{\"method\":\"CreditBalance\",\"amount\":100");
    }

    [Fact]
    public async Task The_register_reminds_todays_and_tomorrows_orders()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>())
           .Json("GET", "/api/orders", new[] { Order(Guid.NewGuid(), Format.Today), Order(Guid.NewGuid(), Format.Today.AddDays(1)), Order(Guid.NewGuid(), Format.Today) });
        var sale = ctx.Render<Sale>();

        sale.WaitForAssertion(() => sale.Find(".sale__list .esc-button").TextContent.ShouldContain("Pedidos: 2 para hoy · 1 para mañana"));
    }

    [Fact]
    public async Task The_owner_picks_the_catalog_products_and_gets_the_link_and_its_QR()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("products.manage", "settings.manage");
        using var _ = ctx;
        var (steak, salami, store) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        api.Json("GET", "/api/catalog", new CatalogSettings("18095550101", [steak], "https://panel.abastalia.com"))
           .Json("GET", "/api/inventory/products", new[]
           {
               new ProductSummary(steak, "Bistec de res", "Res", "Pound", "Exempt", 275m, null, 30m, true),
               new ProductSummary(salami, "Salami", "Embutidos", "Unit", "General18", 150m, null, 12m, true),
           })
           .Json("GET", "/api/sync/status", new CloudLink(true, store))
           .Json("GET", "/api/catalog/qr", new CatalogQr("<svg class=\"qr\"></svg>"))
           .Respond("PUT", "/api/catalog", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = ctx.Render<Abastalia.Client.UI.Pages.Admin.Orders>(p => p.Add(x => x.Section, "catalog"));

        page.WaitForAssertion(() => page.Find(".catalog-qr svg.qr").ShouldNotBeNull());
        page.Find("a[target=_blank]").TextContent.ShouldBe($"https://panel.abastalia.com/c/{store}");
        var boxes = page.FindAll("input[type=checkbox]"); // by category: Embutidos (Salami), then Res (steak)
        (boxes[0].HasAttribute("checked"), boxes[1].HasAttribute("checked")).ShouldBe((false, true));
        boxes[0].Change(true);
        page.FindAll("button").Single(b => b.TextContent.Contains("Guardar catálogo")).Click();

        page.WaitForAssertion(() => page.Find(".esc-notice").TextContent.ShouldContain("Catálogo guardado"));
        var body = api.Requests.Single(r => r.Method == "PUT").Body;
        body.ShouldContain("\"whatsApp\":\"18095550101\"");
        body.ShouldContain(steak.ToString());
        body.ShouldContain(salami.ToString());
    }
}
