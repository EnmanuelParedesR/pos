using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Api;
using Abastalia.Hardware;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.UI.Tests;

/// <summary>What goes wrong at a real register: a session that ends, a timeout after the server saved, a double Enter, a lost field.</summary>
public sealed class RegisterSafetyTests
{
    private static readonly decimal[] Denominations = [2000, 1000, 500, 200, 100, 50, 25, 10, 5, 1];

    private static SaleDetail DetailOf(Guid id, decimal total, params SaleDetailLine[] lines) => new(id, 1, "E320000000001", "Consumer32", DateTime.UtcNow,
        "Completed", "Caja 1", "Yudelka M.", null, null, 0, 0, total, 0, null, lines, [new SaleDetailPayment(PaymentMethod.Cash, total, total, null)], []);

    private static ZReport Z(long number) => new(ScreenContext.Shift.ShiftId, number, "Caja 1", "Yudelka M.", DateTime.UtcNow.AddHours(-8), DateTime.UtcNow, 500m,
        [], 10, 0, 0, 0, 0, 0, 0, 6500m, 6500m, 0m);

    private static void Key<T>(IRenderedComponent<T> screen, string selector, string key) where T : IComponent =>
        screen.Find(selector).KeyDown(new KeyboardEventArgs { Key = key });

    [Fact]
    public async Task When_the_session_expires_the_register_asks_for_the_pin_and_keeps_the_cart()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(RegisterTests.Salami, null);
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>())
           .Json("GET", "/api/identity/users/active", new[] { new ActiveUser(Guid.NewGuid(), "Yudelka M.", "yudelka") })
           .Respond("GET", "/api/inventory/products/by-code/7460000000017", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)); // 12 h later
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("sale");
        var app = ctx.Render<Routes>();

        app.WaitForAssertion(() => app.Find("input.sale__scanner"));
        app.Find("input.sale__scanner").Input("7460000000017");
        app.Find("form").Submit();

        app.WaitForAssertion(() => nav.Uri.ShouldEndWith("sign-in?expired=true"));
        app.WaitForAssertion(() => app.Markup.ShouldContain("Su sesión terminó. Entre de nuevo con su PIN."));
        cart.Lines.Count.ShouldBe(1);
        cart.Lines[0].Name.ShouldBe("Salami 1 lb");
    }

    [Fact]
    public async Task A_checkout_without_an_answer_locks_the_cart_until_the_server_says_whether_it_was_saved()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(RegisterTests.Salami, null);
        var saleId = cart.SaleId;
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>())
           .Respond("POST", "/api/sales", _ => throw new TaskCanceledException()) // the server saved it, the answer was lost
           .Respond("GET", $"/api/sales/{saleId}", _ => throw new HttpRequestException()); // and now it does not answer at all
        var sale = ctx.Render<Sale>();

        Key(sale, ".sale", "F12");
        sale.Find("[role=dialog] .esc-button--money").Click();
        sale.WaitForAssertion(() => sale.Find("[role=dialog] .esc-notice").TextContent.ShouldContain("No se sabe si la venta se guardó"));
        cart.CheckoutUncertain.ShouldBeTrue();

        // Esc while the server still cannot tell: the dialog closes, the cart stays as it was.
        Key(sale, ".esc-overlay", "Escape");
        sale.WaitForAssertion(() => sale.FindAll("[role=dialog]").ShouldBeEmpty());
        // F8 (void) asks first and refuses to empty a cart that may be a saved sale.
        Key(sale, ".sale", "F8");
        sale.WaitForAssertion(() => sale.Find(".esc-notice").TextContent.ShouldContain("No se sabe si el último cobro se guardó"));
        sale.FindAll("[role=dialog]").ShouldBeEmpty();
        cart.Lines.Count.ShouldBe(1);
        cart.SaleId.ShouldBe(saleId);

        // The server is back and has the sale: it is shown as sold, never charged again.
        api.Json("GET", $"/api/sales/{saleId}", DetailOf(saleId, 210m))
           .Json("GET", $"/api/sales/{saleId}/receipt", RegisterTests.ReceiptFor(210m));
        Key(sale, ".sale", "F8");
        sale.WaitForAssertion(() => sale.Markup.ShouldContain("Venta guardada · E320000000001"));
        cart.IsEmpty.ShouldBeTrue();
        cart.CheckoutUncertain.ShouldBeFalse();
        api.Requests.Count(r => r.Method == "POST" && r.Route == "/api/sales").ShouldBe(1);
    }

    [Fact]
    public async Task A_return_retried_after_a_timeout_sends_the_same_id()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "sales.return");
        using var _ = ctx;
        var saleId = Guid.NewGuid();
        var line = new SaleDetailLine(Guid.NewGuid(), "Chuleta", 5m, 165m, 0, 825m, 0, 0, null, "Pound");
        var calls = 0;
        api.Json("GET", "/api/sales", new[] { new SaleSummary(saleId, 1, "E320000000001", DateTime.UtcNow, "Completed", "Yudelka M.", null, 825m) })
           .Json("GET", $"/api/sales/{saleId}", DetailOf(saleId, 825m, line))
           .Json("GET", $"/api/sales/{saleId}/receipt", RegisterTests.ReceiptFor(825m))
           .Respond("POST", $"/api/sales/{saleId}/returns", _ => ++calls == 1
               ? throw new TaskCanceledException()
               : FakeApi.Response(HttpStatusCode.OK, new ReturnRecorded(Guid.NewGuid(), "E340000000001", 330m, 0m, PaymentMethod.Cash)));
        var page = ctx.Render<ShiftSales>();

        page.WaitForAssertion(() => page.Find("tr.selectable").Click());
        page.WaitForAssertion(() => page.FindAll("button").Single(b => b.TextContent.Contains("Devolver líneas", StringComparison.Ordinal)).Click());
        page.Find("input[aria-label='Devolver de Chuleta']").Input("2");
        page.Find("input[maxlength='200']").Input("vino mala");
        page.Markup.ShouldContain("330.00"); // the estimate before confirming: 825 × 2 / 5
        IElementButton().Click();
        page.WaitForAssertion(() => page.Find(".esc-notice").TextContent.ShouldContain("No se sabe si se registró"));
        IElementButton().Click();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Devuelva al cliente"));
        var sent = api.Requests.Where(r => r.Route.EndsWith("/returns", StringComparison.Ordinal)).Select(r => r.Body).ToList();
        sent.Count.ShouldBe(2);
        var id = System.Text.Json.JsonDocument.Parse(sent[0]).RootElement.GetProperty("returnId").GetGuid();
        System.Text.Json.JsonDocument.Parse(sent[1]).RootElement.GetProperty("returnId").GetGuid().ShouldBe(id);
        sent[0].ShouldContain("\"restock\":false"); // meat by the pound does not go back on sale by default

        AngleSharp.Dom.IElement IElementButton() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Devolver");
    }

    [Fact]
    public async Task Closing_counts_the_field_still_being_typed_when_F12_is_pressed()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("register.open", "register.close", "register.cash-in", "credit.collect", "register.approve-close");
        using var _ = ctx;
        api.Json("GET", "/api/cash/denominations", Denominations)
           .Json("POST", $"/api/cash/shifts/{ScreenContext.Shift.ShiftId}/close", Z(7));
        var page = ctx.Render<CloseRegister>();

        page.WaitForAssertion(() => page.Find("input[aria-label='Cantidad de 2,000']").Input("3")); // typed, never left the field (no change event)
        Key(page, ".esc-page", "F12");
        page.Markup.ShouldContain("¿Cerrar con RD$ 6,000.00 contado?"); // first F12 asks
        api.Requests.ShouldNotContain(r => r.Method == "POST");
        Key(page, ".esc-page", "F12");

        page.WaitForAssertion(() => page.Markup.ShouldContain("Cierre Z #7"));
        api.Requests.Single(r => r.Method == "POST").Body.ShouldContain("{\"denomination\":2000,\"quantity\":3}");
    }

    [Fact]
    public async Task Opening_counts_the_field_still_being_typed_when_F12_is_pressed()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        var register = new RegisterInfo(Guid.NewGuid(), "Caja 1", true, null, null);
        api.Json("GET", "/api/cash/denominations", Denominations)
           .Json("GET", "/api/cash/registers", new[] { register })
           .Json("POST", "/api/cash/shifts", new ShiftOpened(Guid.NewGuid()), HttpStatusCode.Created)
           .Json("GET", "/api/cash/shifts/current", ScreenContext.Shift);
        var page = ctx.Render<OpenRegister>();

        page.WaitForAssertion(() => page.Find("input[aria-label='Cantidad de 1,000']").Input("2"));
        Key(page, ".esc-page", "F12");

        page.WaitForAssertion(() => api.Requests.Single(r => r.Method == "POST").Body.ShouldContain("\"openingFloat\":2000"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TK281_with_no_registers_the_first_day_is_not_a_dead_end(bool canManageRegisters)
    {
        var (ctx, api) = await (canManageRegisters ? ScreenContext.CreateAsync("register.open", "registers.manage") : ScreenContext.CreateAsync("register.open"));
        using var _ = ctx;
        api.Json("GET", "/api/cash/denominations", Denominations)
           .Json("GET", "/api/cash/registers", Array.Empty<RegisterInfo>())
           .Json("GET", "/api/cash/shifts/current", ScreenContext.Shift);
        var page = ctx.Render<OpenRegister>();

        page.WaitForAssertion(() => page.Markup.ShouldContain("No hay cajas libres."));
        if (canManageRegisters)
            page.Find("a[href='admin/settings?section=registers']").TextContent.ShouldBe("Crear una caja");
        else
        {
            page.FindAll("a[href='admin/settings?section=registers']").ShouldBeEmpty();
            page.Markup.ShouldContain("Pídale al dueño");
        }
    }

    [Fact]
    public async Task A_close_retried_after_it_was_saved_shows_its_Z()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("register.open", "register.close", "register.cash-in", "credit.collect", "register.approve-close");
        using var _ = ctx;
        var shift = ScreenContext.Shift.ShiftId;
        api.Json("GET", "/api/cash/denominations", Denominations)
           .Respond("POST", $"/api/cash/shifts/{shift}/close", _ => FakeApi.Problem(HttpStatusCode.Conflict, "CashRegister.ShiftClosed", "El turno ya está cerrado."))
           .Json("GET", $"/api/cash/shifts/{shift}/z", Z(12));
        var page = ctx.Render<CloseRegister>();

        page.WaitForAssertion(() => page.Find("input[aria-label='Cantidad de 500']").Input("13"));
        Key(page, ".esc-page", "F12");
        Key(page, ".esc-page", "F12");

        page.WaitForAssertion(() => page.Markup.ShouldContain("Cierre Z #12"));
        page.Markup.ShouldNotContain("El turno ya está cerrado");
        ctx.Services.GetRequiredService<Session>().Shift.ShouldBeNull();
    }

    [Fact]
    public async Task A_supervisor_closes_another_registers_shift_from_the_main_PC()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("register.close", "register.approve-close", "shifts.view");
        using var _ = ctx;
        var other = Guid.NewGuid();
        var open = new ZReport(other, null, "Caja 3", "Cajera", DateTime.UtcNow.AddHours(-3), null, 500m, [], 2, 0, 0, 0, 0, 0, 0, 900m, null, null);
        api.Json("GET", "/api/cash/denominations", Denominations)
           .Json("GET", $"/api/cash/shifts/{other}/z", open)
           .Json("POST", $"/api/cash/shifts/{other}/close", Z(3) with { ShiftId = other, Register = "Caja 3" });
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"close-register?shift={other}");
        var page = ctx.Render<CloseRegister>();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Turno de Caja 3"));
        page.Find("input[aria-label='Cantidad de 500']").Input("1");
        Key(page, ".esc-page", "F12");
        Key(page, ".esc-page", "F12");

        page.WaitForAssertion(() => page.Markup.ShouldContain("Cierre Z #3"));
        api.Requests.ShouldContain(r => r.Method == "POST" && r.Route == $"/api/cash/shifts/{other}/close");
        ctx.Services.GetRequiredService<Session>().Shift.ShouldBe(ScreenContext.Shift); // the supervisor's own shift stays open
    }

    [Fact]
    public async Task A_scanned_pack_of_a_by_the_pound_product_adds_one_piece_without_asking_the_weight()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        var pack = new ProductByCode(Guid.NewGuid(), Guid.NewGuid(), "Chuleta, paquete 2 lb", "Pound", "Exempt", 450m, null, 2m, null, null, [], null);
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>())
           .Json("GET", "/api/inventory/products/by-code/7460000000024", pack);
        var sale = ctx.Render<Sale>();

        sale.Find("input.sale__scanner").Input("7460000000024");
        sale.Find("form").Submit();

        sale.WaitForAssertion(() => sale.Find(".sale__line").TextContent.ShouldContain("Chuleta, paquete 2 lb"));
        sale.FindAll("[role=dialog]").ShouldBeEmpty();
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Lines.Single().Quantity.ShouldBe(1m);
        cart.Total.ShouldBe(450m);
    }

    [Fact]
    public async Task Confirming_twice_while_the_first_is_in_flight_sends_one_sale()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(RegisterTests.Salami, null);
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.RespondAsync("POST", "/api/sales", _ => answer.Task);
        SaleRegistered? sold = null;
        var soldCount = 0;
        var checkout = ctx.Render<CheckoutDialog>(p => p.Add(x => x.ShiftId, ScreenContext.Shift.ShiftId).Add(x => x.OnSold, v => (sold, soldCount) = (v, soldCount + 1)));

        checkout.Find(".esc-button--money").Click();
        checkout.Find(".esc-overlay").KeyDown(new KeyboardEventArgs { Key = "F12" }); // a click and F12, or a double Enter
        checkout.Find(".esc-overlay").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        answer.SetResult(FakeApi.Response(HttpStatusCode.Created, new SaleRegistered(cart.SaleId, 1, "E320000000001", 210m, 0m, RegisterTests.ReceiptFor(210m))));

        checkout.WaitForAssertion(() => sold.ShouldNotBeNull());
        api.Requests.Count(r => r.Route == "/api/sales").ShouldBe(1);
        soldCount.ShouldBe(1);
    }

    [Fact]
    public async Task F3_and_a_key_on_the_pad_replace_the_quantity_instead_of_appending_to_it()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(RegisterTests.Salami, null);
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>());
        var sale = ctx.Render<Sale>();

        Key(sale, ".sale", "F3");
        sale.Find("[role=dialog] h2").TextContent.ShouldBe("Cantidad");
        sale.FindAll(".esc-keypad button").Single(b => b.TextContent == "2").Click();
        sale.Find("[role=dialog] .esc-button--primary").Click();

        cart.Lines.Single().Quantity.ShouldBe(2m); // not 12
    }

    [Fact]
    public async Task A_screen_that_fails_shows_a_notice_and_the_cart_survives()
    {
        var (ctx, _) = await ScreenContext.CreateAsync("sales.create");
        using var __ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(RegisterTests.Salami, null);
        var frame = ctx.Render<Frame>(p => p.Add(x => x.Body, b =>
        {
            b.OpenComponent<Broken>(0);
            b.CloseComponent();
        }));

        frame.WaitForAssertion(() => frame.Markup.ShouldContain("Algo falló en esta pantalla."));
        frame.Find("button.esc-button--primary").TextContent.ShouldContain("Volver a la venta");
        cart.Lines.Single().Name.ShouldBe("Salami 1 lb");
    }

    [Fact]
    public async Task Sign_in_shows_why_the_session_ended()
    {
        var (ctx, api) = await ScreenContext.CreateAsync();
        using var _ = ctx;
        ctx.Services.GetRequiredService<Session>().Close();
        api.Json("GET", "/api/identity/users/active", Array.Empty<ActiveUser>());
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("sign-in?expired=true");

        var signIn = ctx.Render<SignIn>();

        signIn.WaitForAssertion(() => signIn.Markup.ShouldContain("Su sesión terminó."));
    }

    private sealed class Broken : ComponentBase
    {
        protected override void OnInitialized() => throw new InvalidOperationException("JSON inesperado");
    }
}
