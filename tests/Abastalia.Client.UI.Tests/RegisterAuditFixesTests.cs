using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Api;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages;
using Abastalia.Client.UI.Pages.Admin;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-304: the V1 audit of the register and back office screens.</summary>
public sealed class RegisterAuditFixesTests
{
    private static object Product(string name, string unit = "Unit") => new
    {
        id = Guid.NewGuid(), name, category = "Embutidos", unit, itbisRate = "General18", price = 150m, wholesalePrice = (decimal?)null, stock = (decimal?)null, isActive = true,
    };

    private static object CategoryJson(Guid id, string name) => new { id, name, color = "blue", notes = Array.Empty<string>(), isActive = true };

    [Fact]
    public void A_product_without_a_barcode_is_found_by_name_and_Enter_adds_the_only_match()
    {
        var (ctx, api) = ScreenContext.Create("sales.create");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>())
           .Respond("GET", "/api/inventory/products/by-code/jamon", _ => FakeApi.Problem(HttpStatusCode.NotFound, "Inventory.CodeNotFound", "No existe."))
           .Json("GET", "/api/inventory/products?search=jam", new[] { Product("Jamón de pierna"), Product("Jamón cocido") })
           .Json("GET", "/api/inventory/products?search=jamon", new[] { Product("Jamón de pierna") });
        var sale = ctx.Render<Sale>();

        // Typing a name shows the matches as tiles, to tap.
        sale.Find("input.sale__scanner").Input("jam");
        sale.WaitForAssertion(() => sale.FindAll(".sale__tiles .esc-tile__name").Select(t => t.TextContent).ShouldBe(["Jamón de pierna", "Jamón cocido"]));
        sale.Find("[role=status]").TextContent.ShouldBe("Productos con “jam”");

        // Enter: not a code, so a name; the only match goes into the sale.
        sale.Find("input.sale__scanner").Input("jamon");
        sale.Find("form").Submit();
        sale.WaitForAssertion(() => sale.Find(".sale__line").TextContent.ShouldContain("Jamón de pierna"));
    }

    [Fact]
    public void A_scanned_code_that_is_not_found_never_searches_by_name()
    {
        var (ctx, api) = ScreenContext.Create("sales.create");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>())
           .Respond("GET", "/api/inventory/products/by-code/7460000000099", _ => FakeApi.Problem(HttpStatusCode.NotFound, "Inventory.CodeNotFound", "No existe."));
        var sale = ctx.Render<Sale>();

        sale.Find("input.sale__scanner").Input("7460000000099");
        sale.Find("form").Submit();

        sale.WaitForAssertion(() => sale.Find(".esc-notice").TextContent.ShouldContain("No se encontró el código 7460000000099."));
        api.Requests.ShouldNotContain(r => r.Route.Contains("search="));
    }

    [Fact]
    public void A_category_with_more_than_50_products_shows_the_rest_with_Ver_mas()
    {
        var (ctx, api) = ScreenContext.Create("sales.create");
        using var _ = ctx;
        var meats = Guid.NewGuid();
        api.Json("GET", "/api/inventory/categories", new[] { CategoryJson(meats, "Carnes") })
           .Json("GET", $"/api/inventory/products?categoryId={meats}", Enumerable.Range(1, 50).Select(i => Product($"Corte {i:00}")).ToArray())
           .Json("GET", $"/api/inventory/products?categoryId={meats}&page=2", new[] { Product("Corte 51") });
        var sale = ctx.Render<Sale>();

        sale.WaitForAssertion(() => sale.FindAll(".sale__tiles button").Single(b => b.TextContent == "Ver más productos").Click());

        sale.WaitForAssertion(() => sale.FindAll(".sale__tiles .esc-tile__name")[^1].TextContent.ShouldBe("Corte 51"));
        sale.FindAll(".sale__tiles .esc-tile__name").Count.ShouldBe(51); // and no "Ver más": that was the last page
    }

    [Fact]
    public void A_slow_answer_for_an_earlier_category_never_replaces_the_one_tapped_last()
    {
        var (ctx, api) = ScreenContext.Create("sales.create");
        using var _ = ctx;
        var (meats, drinks) = (Guid.NewGuid(), Guid.NewGuid());
        var slow = new TaskCompletionSource<HttpResponseMessage>();
        api.Json("GET", "/api/inventory/categories", new[] { CategoryJson(meats, "Carnes"), CategoryJson(drinks, "Bebidas") })
           .Json("GET", $"/api/inventory/products?categoryId={meats}", new[] { Product("Chuleta") })
           .Json("GET", $"/api/inventory/products?categoryId={drinks}", new[] { Product("Refresco") });
        var sale = ctx.Render<Sale>();
        sale.WaitForAssertion(() => sale.Find(".esc-tile__name").TextContent.ShouldBe("Chuleta"));

        api.RespondAsync("GET", $"/api/inventory/products?categoryId={meats}", _ => slow.Task);
        sale.FindAll(".esc-chip").Single(c => c.TextContent == "Carnes").Click();
        sale.FindAll(".esc-chip").Single(c => c.TextContent == "Bebidas").Click();
        sale.WaitForAssertion(() => sale.Find(".esc-tile__name").TextContent.ShouldBe("Refresco"));
        slow.SetResult(FakeApi.Response(HttpStatusCode.OK, new[] { Product("Costilla") }));

        sale.WaitForAssertion(() => sale.FindAll(".esc-tile__name").Select(t => t.TextContent).ShouldBe(["Refresco"]));
        sale.FindAll(".esc-chip[aria-pressed=true]").Single().TextContent.ShouldBe("Bebidas");
    }

    [Fact]
    public void A_sale_left_by_an_expired_session_waits_for_its_cashier_and_never_goes_to_the_next_one()
    {
        var cart = new Cart();
        var (ana, luis) = (Guid.NewGuid(), Guid.NewGuid());
        cart.HandOver(ana, "Ana").ShouldBe(Cart.HandOverResult.Kept);
        cart.Add(RegisterTests.Salami, null);
        var saleId = cart.SaleId;

        // Ana's session expired; Luis signs in: he starts empty.
        cart.HandOver(luis, "Luis").ShouldBe(Cart.HandOverResult.SetAside);
        (cart.IsEmpty, cart.SetAsideFor).ShouldBe((true, "Ana"));

        // Luis leaves (the cart is empty), Ana comes back: her sale is there, with its id.
        cart.HandOver(ana, "Ana").ShouldBe(Cart.HandOverResult.Restored);
        (cart.Lines.Single().Name, cart.SaleId).ShouldBe(("Salami 1 lb", saleId));
        cart.HandOver(ana, "Ana").ShouldBe(Cart.HandOverResult.Kept); // the same cashier again: nothing moves
        cart.LineCount.ShouldBe(1);
    }

    [Fact]
    public void An_uncertain_checkout_waits_for_its_cashier_with_its_id_so_it_is_never_charged_twice()
    {
        var cart = new Cart();
        var (ana, luis) = (Guid.NewGuid(), Guid.NewGuid());
        cart.HandOver(ana, "Ana");
        cart.Add(RegisterTests.Salami, null);
        cart.CheckoutUncertain = true;
        var saleId = cart.SaleId;

        cart.HandOver(luis, "Luis").ShouldBe(Cart.HandOverResult.SetAside);
        cart.CheckoutUncertain.ShouldBeFalse(); // Luis's new sale

        cart.HandOver(ana, "Ana").ShouldBe(Cart.HandOverResult.Restored);
        (cart.CheckoutUncertain, cart.SaleId).ShouldBe((true, saleId)); // Ana asks the server about that same sale
    }

    [Fact]
    public void The_next_cashier_is_told_whose_sale_was_set_aside()
    {
        var (ctx, api) = ScreenContext.Create("sales.create");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>());
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.HandOver(Guid.NewGuid(), "Ana");
        cart.Add(RegisterTests.Salami, null);
        cart.HandOver(Guid.NewGuid(), "Luis");

        var sale = ctx.Render<Sale>();

        sale.WaitForAssertion(() => sale.Find(".esc-notice").TextContent.ShouldContain("La venta sin cobrar de Ana quedó guardada."));
        sale.FindAll(".sale__line").ShouldBeEmpty();
        cart.LastHandOver.ShouldBeNull(); // shown once
    }

    [Fact]
    public void Signing_in_as_another_cashier_sets_the_previous_ones_sale_aside()
    {
        var (ctx, api) = ScreenContext.Create("sales.create");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.HandOver(Guid.NewGuid(), "Ana");
        cart.Add(RegisterTests.Salami, null);
        var luis = Guid.NewGuid();
        api.Json("GET", "/api/identity/users/active", new[] { new { id = luis, name = "Luis", code = "02" } })
           .Json("POST", "/api/identity/sessions", new { token = "t2", userId = luis, name = "Luis", expiresUtc = DateTime.UtcNow.AddHours(12) })
           .Json("GET", "/api/identity/me", new { userId = luis, name = "Luis", deviceId = Guid.NewGuid(), permissions = new[] { "sales.create" } });
        var signIn = ctx.Render<SignIn>();

        signIn.WaitForAssertion(() => signIn.FindAll(".esc-tile").Single(t => t.TextContent.Contains("Luis")).Click());
        foreach (var k in "1234") signIn.Find("[aria-label=PIN]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = k.ToString() });
        signIn.FindAll("button").Single(b => b.TextContent.Contains("Entrar")).Click();

        signIn.WaitForAssertion(() => (cart.IsEmpty, cart.SetAsideFor).ShouldBe((true, "Ana")));
    }

    [Theory]
    [InlineData("101-01234-5", "101012345", true)]
    [InlineData(" 001-1234567-8 ", "00112345678", true)]
    [InlineData("10101234", "10101234", false)]
    [InlineData("1O1O12345", "1O1O12345", false)] // letters are not digits
    [InlineData("  ", null, false)]
    public void An_RNC_or_cedula_is_accepted_with_dashes_or_spaces(string typed, string? normalized, bool valid)
    {
        TaxId.Normalize(typed).ShouldBe(normalized);
        TaxId.IsValid(TaxId.Normalize(typed)).ShouldBe(valid);
    }

    [Fact]
    public void The_business_RNC_is_saved_without_its_dashes()
    {
        var (ctx, api) = ScreenContext.Create("settings.manage");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business", new { name = "Carnicería La Esquina", discountCapPercent = 10m })
           .Respond("PUT", "/api/settings/business", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = ctx.Render<Settings>();

        page.WaitForAssertion(() => page.Find("input[inputmode=numeric][maxlength='13']").Change("101-01234-5"));
        page.FindAll("form").First(f => f.TextContent.Contains("Datos del negocio")).Submit();

        page.WaitForAssertion(() => api.Requests.Single(r => r.Method == "PUT" && r.Route == "/api/settings/business").Body.ShouldContain("\"rnc\":\"101012345\""));
    }

    [Fact]
    public void The_summary_shows_each_top_product_in_its_own_unit_without_loading_the_catalog()
    {
        var (ctx, api) = ScreenContext.Create("reports.view");
        using var _ = ctx;
        api.Json("GET", "/api/reports/sales/by-product", new[]
        {
            new { productId = Guid.NewGuid(), product = "Chuleta", quantity = 12.5m, unit = "Pound", netWithoutItbis = 2000m, profit = (decimal?)null },
            new { productId = Guid.NewGuid(), product = "Refresco", quantity = 1m, unit = "Unit", netWithoutItbis = 59m, profit = (decimal?)null },
        });
        var page = ctx.Render<Summary>();

        page.WaitForAssertion(() => page.Markup.ShouldContain("12.5 lb"));
        page.Markup.ShouldContain("1 unidad");
        api.Requests.ShouldNotContain(r => r.Route.StartsWith("/api/inventory/products", StringComparison.Ordinal));
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public volatile bool Closed;
        public string Version => "1.4.0";
        public void Follow(string channel, string? maxVersion) { }
        public void CloseToUpdate() => Closed = true;
    }

    [Theory]
    [InlineData("sale", false, true)]
    [InlineData("sale", true, false)] // a dialog is open (a payment, a count)
    [InlineData("admin/products", false, false)] // a form may be half typed
    public void The_register_closes_to_update_only_with_nothing_in_progress(string route, bool dialogOpen, bool closes)
    {
        var updater = new FakeUpdater();
        var (ctx, api) = ScreenContext.Create(s => s.AddSingleton<IAppUpdater>(updater), "sales.create");
        using var _ = ctx;
        ctx.JSInterop.Setup<bool>("escuadra.hasDialog").SetResult(dialogOpen);
        api.Json("PUT", "/api/settings/updates/register", new { channel = "stable", allowedVersion = (string?)null, pendingVersion = "1.5.0", restartRequested = true });
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(route);

        var frame = ctx.Render<Frame>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "PANTALLA"))));

        if (closes) frame.WaitForAssertion(() => updater.Closed.ShouldBeTrue());
        else
        {
            frame.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Route == "/api/settings/updates/register"));
            Thread.Sleep(100); // the answer is read after the request
            updater.Closed.ShouldBeFalse();
        }
    }
}
