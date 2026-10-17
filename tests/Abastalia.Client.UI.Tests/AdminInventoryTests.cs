using System.Net;
using AngleSharp.Dom;
using Bunit;
using Shouldly;
using Abastalia.Client.UI.Pages.Admin;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-184: the inventory back office (stock, kardex, adjustments, blind counts, transfers).</summary>
public sealed class AdminInventoryTests
{
    private static readonly Guid Main = Guid.NewGuid(), Ribs = Guid.NewGuid(), Salami = Guid.NewGuid(), Res = Guid.NewGuid();

    internal static IElement Button<T>(IRenderedComponent<T> page, string text) where T : Microsoft.AspNetCore.Components.IComponent =>
        page.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal));

    private static void Warehouses(FakeApi api, bool two = false) =>
        api.Json("GET", "/api/inventory/warehouses", two
            ? new[] { new { id = Main, name = "Tienda", isActive = true, isMain = true }, new { id = Guid.NewGuid(), name = "Cuarto frío", isActive = true, isMain = false } }
            : new[] { new { id = Main, name = "Tienda", isActive = true, isMain = true } });

    [Fact]
    public async Task The_stock_list_values_the_inventory_filters_what_is_low_and_an_adjustment_takes_stock_out_with_its_reason()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("inventory.view", "inventory.adjust", "costs.view");
        using var _ = ctx;
        Warehouses(api);
        api.Json("GET", "/api/inventory/stock", new[]
            {
                new { productId = Ribs, name = "Costilla", category = "Res", unit = "Pound", quantity = 4.5m, cost = 120m, value = 540m, belowMinimum = true, needsCount = false },
                new { productId = Salami, name = "Salami", category = "Embutidos", unit = "Unit", quantity = 12m, cost = 100m, value = 1200m, belowMinimum = false, needsCount = false },
            })
           .Json("GET", $"/api/inventory/products/{Ribs}/movements", new[]
            {
                new { sequence = 1L, dateUtc = DateTime.UtcNow, type = "Purchase", warehouseId = Main, quantity = 4.5m, unitCost = 120m, resultingStock = 4.5m, referenceId = (Guid?)null, reason = (string?)null },
            })
           .Json("POST", "/api/inventory/adjustments", new { adjustmentId = Guid.NewGuid() });
        var page = ctx.Render<Inventory>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.selectable").Count.ShouldBe(2));
        page.Find(".esc-stat__value").TextContent.ShouldBe("RD$ 1,740.00");
        page.FindAll("button.esc-chip").Single(b => b.TextContent == "Bajo el mínimo").Click();
        page.FindAll("tbody tr.selectable").ShouldHaveSingleItem().TextContent.ShouldContain("4.5 lb");

        page.Find("tbody tr.selectable").Click();
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("Compra"));
        Button(page, "Ajustar existencia").Click();
        page.Find("[role=dialog] select").Change("Damage");
        page.Find("[role=dialog] input.esc-num").Input("5");
        Button(page, "Guardar ajuste").Click();
        page.Find(".esc-field__error").TextContent.ShouldContain("No se puede sacar más de lo que hay (4.5 lb)");
        api.Requests.ShouldNotContain(r => r.Route == "/api/inventory/adjustments");

        page.Find("[role=dialog] input.esc-num").Input("1.25");
        Button(page, "Guardar ajuste").Click();
        page.WaitForAssertion(() => api.Requests.Single(r => r.Route == "/api/inventory/adjustments").Body
            .ShouldContain("\"quantity\":-1.25,\"reason\":\"Damage\""));
        page.WaitForAssertion(() => page.Find("[role=dialog] .esc-notice--success").TextContent.ShouldContain("Daño: −1.25 lb guardado."));
    }

    [Fact]
    public async Task Someone_who_only_counts_does_not_see_the_inventory_menu_items_they_cannot_use()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("reports.view", "inventory.count");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/stock-counts", Array.Empty<object>());
        var page = ctx.Render<StockCounts>();

        page.WaitForAssertion(() => page.Find(".esc-side__nav").TextContent.ShouldContain("Inventario"));
        page.Find(".esc-side__nav").TextContent.ShouldNotContain("Compras");
        page.FindAll("[role=tab]").ShouldBeEmpty(); // only one inventory page for this user: no tabs
        page.WaitForAssertion(() => page.Find(".esc-empty__title").TextContent.ShouldBe("Todavía no hay conteos"));
    }

    [Fact]
    public async Task A_blind_count_is_typed_saved_and_applied_and_shows_what_was_missing()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("inventory.count", "inventory.adjust");
        using var _ = ctx;
        var count = Guid.NewGuid();
        Warehouses(api);
        api.Json("GET", "/api/inventory/categories", new[] { new { id = Res, name = "Res", color = "#B42318", notes = Array.Empty<string>(), isActive = true } })
           .Json("GET", "/api/inventory/stock-counts", new[]
            {
                new { id = count, status = "Open", warehouseId = Main, categoryId = (Guid?)Res, createdUtc = DateTime.UtcNow, appliedUtc = (DateTime?)null, lineCount = 2, countedCount = 0 },
            })
           .Json("GET", $"/api/inventory/stock-counts/{count}", new
            {
                id = count, status = "Open", warehouseId = Main,
                lines = new[] { new { productId = Ribs, product = "Costilla", unit = "Pound", counted = (decimal?)null }, new { productId = Salami, product = "Salami", unit = "Unit", counted = (decimal?)null } },
            })
           .Respond("PUT", $"/api/inventory/stock-counts/{count}/lines", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
           .Json("POST", $"/api/inventory/stock-counts/{count}/apply", new
            {
                differences = new[] { new { productId = Ribs, product = "Costilla", systemStock = 8m, counted = 6.5m, difference = -1.5m, value = -180m } },
                differenceValue = -180m,
            });
        var page = ctx.Render<StockCounts>();

        page.WaitForAssertion(() => page.Find("tbody tr.selectable").TextContent.ShouldContain("Res"));
        page.Find("tbody tr.selectable").Click();
        page.WaitForAssertion(() => page.Find("input[aria-label='Contado de Salami']"));
        page.Find("input[aria-label='Contado de Salami']").Change("2.5");
        page.Find(".esc-field__error").TextContent.ShouldContain("Unidades enteras");
        page.Find("input[aria-label='Contado de Salami']").Change("2");
        page.Find("input[aria-label='Contado de Costilla']").Change("6.5");
        page.Markup.ShouldContain("2 de 2 contados");

        Button(page, "Revisar y aplicar").Click();
        Button(page, "Aplicar conteo").Click();
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("Faltó mercancía por RD$ 180.00 al costo"));
        var saved = api.Requests.Single(r => r.Method == "PUT").Body;
        saved.ShouldContain($"\"productId\":\"{Salami}\",\"counted\":2");
        saved.ShouldContain($"\"productId\":\"{Ribs}\",\"counted\":6.5");
        api.Requests.Single(r => r.Route.EndsWith("/apply", StringComparison.Ordinal)).Body.ShouldBe("{\"supervisorApprovalId\":null}");
    }

    [Fact]
    public async Task A_transfer_moves_the_picked_products_between_two_warehouses()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("inventory.transfer");
        using var _ = ctx;
        Warehouses(api, two: true);
        api.Json("GET", "/api/inventory/products", new[]
            {
                new { id = Ribs, name = "Costilla", category = "Res", unit = "Pound", itbisRate = "Exempt", price = 180m, wholesalePrice = (decimal?)null, stock = (decimal?)null, isActive = true },
            })
           .Json("POST", "/api/inventory/transfers", new { transferId = Guid.NewGuid() });
        var page = ctx.Render<Transfers>();

        page.WaitForAssertion(() => page.Find(".picker input").Input("cost"));
        page.WaitForAssertion(() => page.Find(".picker__list button").Click());
        page.Find("input[aria-label='Cantidad de Costilla']").Change("0");
        Button(page, "Transferir").Click();
        page.WaitForAssertion(() => page.Find(".esc-field__error").TextContent.ShouldContain("mayor que cero")); // the click handler is async

        page.Find("input[aria-label='Cantidad de Costilla']").Change("8");
        Button(page, "Transferir").Click();
        page.WaitForAssertion(() => page.Find(".esc-notice--success").TextContent.Trim().ShouldBe("Se pasó 1 producto de Tienda a Cuarto frío."));
        api.Requests.Single(r => r.Route == "/api/inventory/transfers").Body.ShouldContain($"\"lines\":[{{\"productId\":\"{Ribs}\",\"quantity\":8}}]");
    }
}
