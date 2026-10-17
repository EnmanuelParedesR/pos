using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Api;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-168 (wholesale prices), TK-169 (R7 #18 e-CF queue, #26 leaving the back office, #27 re-pricing after Sales.TotalChanged).</summary>
public sealed class PricesAndLeavingTests
{
    private static readonly ProductByCode Ribs = new(Guid.NewGuid(), null, "Costilla", "Unit", "Exempt", 200m, 180m, 1m, null, null, [], null);
    private static readonly ProductByCode Salt = new(Guid.NewGuid(), null, "Sal", "Unit", "Exempt", 25m, null, 1m, null, null, [], null);

    private static CustomerDetail Customer(string level) => new(Guid.NewGuid(), "Colmado Ana", null, false, level, 0, 0, 0, true);

    [Fact]
    public void A_wholesale_customer_gets_wholesale_prices_and_another_customer_puts_retail_back()
    {
        var cart = new Cart();
        cart.Add(Ribs, null);
        cart.Add(Salt, null);
        var changed = cart.Lines[0] with { OverriddenPrice = 150m };
        cart.Update(changed);
        cart.Add(Ribs, null); // a second line of ribs at the list price
        cart.Total.ShouldBe(150m + 25m + 200m);

        cart.Customer = Customer("Wholesale");
        cart.Total.ShouldBe(150m + 25m + 180m); // the changed price stays; salt has no wholesale price
        cart.Lines[2].UsesWholesale.ShouldBeTrue();
        cart.Lines[1].UsesWholesale.ShouldBeFalse();
        cart.ToRegisterSale(Guid.NewGuid(), [], cart.Total, []).PriceLevel.ShouldBe(PriceLevel.Wholesale);
        cart.Add(Ribs, null).Price.ShouldBe(180m); // added after the customer: wholesale too (adds up on its line)

        cart.Customer = Customer("Retail");
        cart.Lines.Where(l => l.OverriddenPrice is null && l.ProductId == Ribs.ProductId).ShouldAllBe(l => l.Price == 200m);
        cart.ToRegisterSale(Guid.NewGuid(), [], cart.Total, []).PriceLevel.ShouldBe(PriceLevel.Retail);
        cart.Customer = null;
        cart.Lines.ShouldAllBe(l => !l.UsesWholesale);
    }

    [Fact]
    public async Task The_register_line_says_wholesale_price()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>());
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Customer = Customer("Wholesale");
        cart.Add(Ribs, null);
        var sale = ctx.Render<Sale>();

        sale.WaitForAssertion(() => sale.Find(".sale__line").TextContent.ShouldContain("Precio mayorista"));
        sale.Find(".sale__line").TextContent.ShouldContain("180.00");
    }

    [Fact]
    public async Task After_the_server_corrects_the_total_the_lines_take_the_current_prices()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(Ribs, null);
        api.Respond("POST", "/api/sales", _ => FakeApi.Problem(HttpStatusCode.Conflict, "Sales.TotalChanged", "El total cambió.", new { total = new[] { "220.00" } }))
           .Json("GET", "/api/inventory/products/prices", new[] { new ProductPrice(Ribs.ProductId, null, 220m, 190m) });
        var checkout = ctx.Render<CheckoutDialog>(p => p.Add(x => x.ShiftId, ScreenContext.Shift.ShiftId));

        checkout.Find(".esc-button--money").Click();
        checkout.WaitForAssertion(() => checkout.Find(".esc-notice").TextContent.ShouldContain("RD$ 220.00"));
        cart.Lines.Single().Price.ShouldBe(220m);
        cart.Total.ShouldBe(220m); // the screen matches the server
        api.Requests.ShouldContain(r => r.Route == $"/api/inventory/products/prices?ids={Ribs.ProductId}");
    }

    [Fact]
    public async Task Leaving_the_back_office_with_an_unpaid_sale_asks_to_hold_or_void_it_first()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "reports.view");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business", new BusinessProfile("Carnicería", null, null, null, null, 10))
           .Respond("DELETE", "/api/identity/sessions/current", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(Ribs, null);
        var admin = ctx.Render<Admin>(p => p.Add(x => x.Title, "Resumen"));

        admin.Find(".esc-side__foot a[title=Salir]").Click();
        admin.WaitForAssertion(() => admin.Find("[role=dialog]").TextContent.ShouldContain("Hay una venta sin cobrar."));
        api.Requests.ShouldNotContain(r => r.Method == "DELETE");

        admin.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Anularla y salir")).Click();
        admin.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Method == "DELETE" && r.Route == "/api/identity/sessions/current"));
        cart.IsEmpty.ShouldBeTrue();
        ctx.Services.GetRequiredService<Session>().IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task The_status_bar_shows_the_ecf_queue_amber_when_the_oldest_waits_over_a_day()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/queue", new FiscalQueue(3, 0, DateTime.UtcNow.AddHours(40), 0, 0));
        var frame = ctx.Render<Frame>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "venta"))));

        frame.WaitForAssertion(() => frame.Find(".esc-status-bar").TextContent.ShouldContain("e-CF en cola: 3"));
        frame.FindAll(".esc-status-bar .esc-offline").Single().TextContent.ShouldBe("e-CF en cola: 3");
    }

    [Fact]
    public async Task The_backup_phrase_warning_is_only_for_who_can_set_it_and_takes_them_there()
    {
        // TK-362: the owner sees it and a tap opens Respaldos; a cashier signing in next never sees the owner's last answer.
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "backups.manage");
        using var _ = ctx;
        api.Json("GET", "/api/backups/warning", new BackupWarning("Falta la frase de los respaldos."));
        var frame = ctx.Render<Frame>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "venta"))));
        IEnumerable<AngleSharp.Dom.IElement> Warning() => frame.FindAll(".esc-status-bar button").Where(b => b.TextContent.Contains("Sin respaldos", StringComparison.Ordinal));

        frame.WaitForAssertion(() => Warning().Single().TextContent.ShouldBe("Sin respaldos: ponga la frase"));
        Warning().Single().Click();
        ctx.Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("admin/settings?section=backups");

        var session = ctx.Services.GetRequiredService<Session>();
        session.Open(new OpenedSession("token-2", Guid.NewGuid(), "Cajera", DateTime.UtcNow.AddHours(12)), new Me(Guid.NewGuid(), "Cajera", Guid.NewGuid(), ["sales.create"]));
        frame.WaitForAssertion(() => Warning().ShouldBeEmpty());
    }

    [Fact]
    public async Task TK376_running_low_on_e_NCF_warns_the_cashier_to_tell_the_owner_and_takes_the_owner_to_Comprobantes()
    {
        var alerts = new List<SequenceAlert>
        {
            new("FiscalCredit31", 4000, null, false, false, 20),
            new("Consumer32", 120, null, true, false, 20),
            new("CreditNote34", 0, null, true, true, 20),
        };
        var (cashier, cashierApi) = await ScreenContext.CreateAsync("sales.create");
        using var _ = cashier;
        cashierApi.Json("GET", "/api/fiscal/alerts", alerts);
        var bar = cashier.Render<Frame>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "venta"))));
        bar.WaitForAssertion(() => bar.FindAll(".esc-status-bar [role=status]").Select(s => (s.TextContent, s.ClassName)).ShouldContain(("Quedan 120 comprobantes de consumo: avise al dueño", "esc-offline")));
        // Red first: none left stops the sale.
        var warnings = bar.FindAll(".esc-status-bar [role=status]").Where(s => s.TextContent.Contains("comprobantes de", StringComparison.Ordinal)).ToList();
        warnings.Select(s => s.TextContent).ShouldBe(["No quedan comprobantes de nota de crédito: avise al dueño", "Quedan 120 comprobantes de consumo: avise al dueño"]);
        warnings[0].ClassName.ShouldBe("esc-status-bar__critical");
        bar.FindAll(".esc-status-bar button").ShouldNotContain(b => b.TextContent.Contains("comprobantes", StringComparison.Ordinal));

        var (owner, ownerApi) = await ScreenContext.CreateAsync("sales.create", "fiscal.manage");
        using var __ = owner;
        ownerApi.Json("GET", "/api/fiscal/alerts", alerts);
        var frame = owner.Render<Frame>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "venta"))));
        frame.WaitForAssertion(() => frame.FindAll(".esc-status-bar button").Select(b => b.TextContent)
            .ShouldContain("Quedan 120 comprobantes de consumo: cargue más"));
        frame.FindAll(".esc-status-bar button").Single(b => b.TextContent.StartsWith("Quedan 120", StringComparison.Ordinal)).Click();
        owner.Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("admin/settings?section=fiscal");

        // Nothing to warn about (Simulado, TesteCF or plenty left): nothing on the bar.
        var (calm, calmApi) = await ScreenContext.CreateAsync("sales.create", "fiscal.manage");
        using var ___ = calm;
        calmApi.Json("GET", "/api/fiscal/alerts", alerts.Select(a => a with { Alert = false, Critical = false }).ToList());
        var quiet = calm.Render<Frame>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "venta"))));
        quiet.WaitForAssertion(() => calmApi.Requests.ShouldContain(r => r.Route == "/api/fiscal/alerts"));
        quiet.Find(".esc-status-bar").TextContent.ShouldNotContain("comprobantes de");
    }

    [Fact]
    public async Task A_cashier_without_the_fiscal_permission_does_not_see_the_queue()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        var frame = ctx.Render<Frame>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "venta"))));

        frame.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Route == "/health"));
        frame.Markup.ShouldNotContain("e-CF en cola");
        api.Requests.ShouldNotContain(r => r.Route == "/api/fiscal/queue");
    }
}
