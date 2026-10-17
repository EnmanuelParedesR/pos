using System.Net;
using Bunit;
using Shouldly;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages.Admin;
using static Abastalia.Client.UI.Tests.AdminInventoryTests;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-239: gaps from the components audit (presentations, label formats, product paging, taxpayer lookup).</summary>
public sealed class AdminAuditGapsTests
{
    private static readonly Guid Beer = Guid.NewGuid(), Box = Guid.NewGuid(), Drinks = Guid.NewGuid();

    private static object Product(string name) => new
    {
        id = Guid.NewGuid(), name, category = "Bebidas", unit = "Unit", itbisRate = "General18", price = 100m, wholesalePrice = (decimal?)null, stock = (decimal?)5m, isActive = true,
    };

    [Fact]
    public void Products_page_by_50_with_Ver_mas_and_the_search_stays_on_the_server()
    {
        var (ctx, api) = ScreenContext.Create("products.manage", "inventory.view");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/categories", new[] { new { id = Drinks, name = "Bebidas", color = "#0B5CAD", notes = Array.Empty<string>(), isActive = true } })
           .Respond("GET", "/api/inventory/products", _ => FakeApi.Response(HttpStatusCode.OK, Enumerable.Range(1, 50).Select(i => Product($"Producto {i:00}"))))
           .Json("GET", "/api/inventory/products?search=&includeInactive=true&page=2", new[] { Product("Producto 51") });
        var page = ctx.Render<Products>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.selectable").Count.ShouldBe(50));
        Button(page, "Ver más productos").Click();
        page.WaitForAssertion(() => page.FindAll("tbody tr.selectable").Count.ShouldBe(51));
        page.FindAll("button").ShouldNotContain(b => b.TextContent.Contains("Ver más", StringComparison.Ordinal)); // a short page is the last
        api.Requests.ShouldContain(r => r.Route.Contains("page=2", StringComparison.Ordinal));
    }

    [Fact]
    public void A_presentation_is_added_with_units_price_and_barcode_then_deactivated()
    {
        var (ctx, api) = ScreenContext.Create("products.manage", "inventory.view", "prices.change");
        using var _ = ctx;
        var withBox = false;
        object Detail() => new
        {
            id = Beer, version = 1, name = "Cerveza", categoryId = Drinks, unit = "Unit", plu = (string?)null, itbisRate = "General18", price = 120m, wholesalePrice = (decimal?)null,
            cost = (decimal?)null, sellWithoutStock = true, minimumStock = 0m, maximumStock = 0m, isActive = true,
            presentations = withBox ? new[] { new { id = Box, name = "Caja 24", factor = 24m, price = 2600m, wholesalePrice = (decimal?)null, isActive = true } } : [],
            barcodes = withBox ? new[] { new { code = "7460000000024", presentationId = (Guid?)Box } } : [],
        };
        api.Json("GET", "/api/inventory/categories", new[] { new { id = Drinks, name = "Bebidas", color = "#0B5CAD", notes = Array.Empty<string>(), isActive = true } })
           .Json("GET", "/api/inventory/products", new[] { new { id = Beer, name = "Cerveza", category = "Bebidas", unit = "Unit", itbisRate = "General18", price = 120m, wholesalePrice = (decimal?)null, stock = (decimal?)48m, isActive = true } })
           .Respond("GET", $"/api/inventory/products/{Beer}", _ => FakeApi.Response(HttpStatusCode.OK, Detail()))
           .Respond("POST", $"/api/inventory/products/{Beer}/presentations", _ => { withBox = true; return FakeApi.Response(HttpStatusCode.Created, new { id = Box }); })
           .Respond("POST", $"/api/inventory/products/{Beer}/barcodes", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
           .Respond("PUT", $"/api/inventory/products/{Beer}/presentations/{Box}", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = ctx.Render<Products>();
        page.WaitForAssertion(() => page.Find("tbody tr.selectable").Click());
        page.WaitForAssertion(() => Button(page, "Agregar presentación").Click());

        page.Find("input[aria-label='Nombre de la presentación']").Change("Caja 24");
        page.Find("input[aria-label='Unidades de la presentación']").Change("24");
        Button(page, "Guardar presentación").Click();
        page.Markup.ShouldContain("Escriba el nombre, las unidades y el precio");
        page.Find("input[aria-label='Precio de la presentación']").Change("2600");
        page.Find("input[aria-label='Código de barras de la presentación']").Change("7460000000024");
        Button(page, "Guardar presentación").Click();

        page.WaitForAssertion(() => page.Find("section[aria-label='Presentaciones'] tbody").TextContent.ShouldContain("7460000000024"));
        api.Requests.Single(r => r.Method == "POST" && r.Route.EndsWith("/presentations", StringComparison.Ordinal)).Body
            .ShouldBe("{\"name\":\"Caja 24\",\"factor\":24,\"price\":2600,\"wholesalePrice\":null,\"isActive\":true}");
        api.Requests.Single(r => r.Route.EndsWith("/barcodes", StringComparison.Ordinal)).Body.ShouldBe($"{{\"code\":\"7460000000024\",\"presentationId\":\"{Box}\"}}");

        Button(page, "Editar").Click(); // the presentation's, not a product's
        page.Find("section[aria-label='Presentaciones'] input[type=checkbox]").Change(false);
        Button(page, "Guardar presentación").Click();
        page.WaitForAssertion(() => api.Requests.Single(r => r.Method == "PUT").Body.ShouldContain("\"isActive\":false"));
        api.Requests.Count(r => r.Route.Contains("/barcodes", StringComparison.Ordinal)).ShouldBe(1); // same code: not sent again
    }

    [Fact]
    public void Label_formats_are_edited_and_a_sample_label_is_read_like_the_register()
    {
        var (ctx, api) = ScreenContext.Create("settings.manage", "products.manage");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/label-formats", new[] { new { prefix = "20", pluLength = 5, isPrice = false, decimals = 3 }, new { prefix = "22", pluLength = 4, isPrice = true, decimals = 2 } })
           .Respond("PUT", "/api/inventory/label-formats", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = ctx.Render<ScaleLabelsSettings>();

        page.WaitForAssertion(() => page.Find("[role=status]").TextContent.ShouldBe("PLU 123 · 2.345 lb")); // the example label of the first format
        var sample = page.Find("input[aria-label='Etiqueta de prueba']");
        sample.Input("2200120023455");
        page.Find("[role=status]").TextContent.ShouldBe("PLU 12 · RD$ 23.45");
        sample.Input("2200120023453");
        page.Find("[role=status]").TextContent.ShouldContain("verificador");
        sample.Input("2500120023450");
        page.Find("[role=status]").TextContent.ShouldContain("Ningún formato");

        page.Find("button[aria-label='Quitar el prefijo 22']").Click();
        Button(page, "Guardar formatos").Click();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Formatos guardados."));
        api.Requests.Single(r => r.Method == "PUT").Body.ShouldBe("{\"formats\":[{\"prefix\":\"20\",\"pluLength\":5,\"isPrice\":false,\"decimals\":3}]}");
    }

    [Fact]
    public void A_typed_RNC_fills_the_name_from_the_registry_and_says_when_it_is_not_loaded()
    {
        var (ctx, api) = ScreenContext.Create("purchasing.manage");
        using var _ = ctx;
        api.Json("GET", "/api/purchasing/suppliers", Array.Empty<object>())
           .Json("GET", "/api/fiscal/taxpayers/131245678", new { rnc = "131245678", name = "COLMADO EL SOL SRL", tradeName = (string?)null, isActive = true })
           .Respond("GET", "/api/fiscal/taxpayers/101000001", _ => FakeApi.Problem(HttpStatusCode.NotFound, "Fiscal.RegistryNotLoaded", "El padrón de la DGII no está cargado."));
        var page = ctx.Render<Suppliers>();
        page.WaitForAssertion(() => Button(page, "Nuevo suplidor").Click());

        var rnc = page.Find("input[inputmode=numeric][maxlength='13']");
        rnc.Change("131-24567-8");
        page.WaitForAssertion(() => page.Find("input[maxlength='150']").GetAttribute("value").ShouldBe("COLMADO EL SOL SRL"));
        page.Find("input[maxlength='150']").Change("Colmado El Sol"); // editable: a later lookup keeps what the user typed
        page.Find("input[inputmode=numeric][maxlength='13']").Change("101000001");
        page.WaitForAssertion(() => page.Markup.ShouldContain("El padrón de la DGII no está cargado; escriba el nombre."));
        page.Find("input[maxlength='150']").GetAttribute("value").ShouldBe("Colmado El Sol");
    }

    [Fact]
    public void The_register_fills_a_new_customers_name_from_the_registry()
    {
        var (ctx, api) = ScreenContext.Create("sales.create", "customers.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/taxpayers/00112345678", new { rnc = "00112345678", name = "JUAN PÉREZ", tradeName = (string?)null, isActive = false });
        var dialog = ctx.Render<CustomerDialog>();

        dialog.FindAll("input[maxlength='13']")[^1].Change("001-1234567-8"); // TK-304: with dashes too
        dialog.WaitForAssertion(() => dialog.FindAll("input[maxlength='150']")[^1].GetAttribute("value").ShouldBe("JUAN PÉREZ"));
        dialog.Markup.ShouldContain("Aparece como inactivo en el padrón de la DGII.");
    }
}
