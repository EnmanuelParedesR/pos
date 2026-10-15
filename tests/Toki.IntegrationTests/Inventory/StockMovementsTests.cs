using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Inventory;

/// <summary>Stock movements through the IStockMovements contract (what sales and purchases call) and the catalog rules from review R2.</summary>
public sealed class StockMovementsTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    [Fact]
    public async Task A_purchase_recalculates_the_weighted_average_cost()
    {
        var c = await Admin();
        var id = await c.ProductAsync(await c.CategoryAsync(), "Costilla", 180m, unit: "Pound", cost: 100m, stock: 10);

        (await app.CommandAsync(new MoveStock(id, 30m, Inbound: true, Cost: 140m))).IsSuccess.ShouldBeTrue();

        var p = await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{id}");
        p.GetProperty("cost").GetDecimal().ShouldBe(130m); // (10×100 + 30×140) / 40
        (await c.StockAsync(id)).ShouldBe(40m);
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Selling_without_stock_goes_negative_and_flags_it_for_counting_but_an_adjustment_does_not()
    {
        var c = await Admin();
        var id = await c.ProductAsync(await c.CategoryAsync(), "Pollo", 95m, unit: "Pound", sellWithoutStock: true);

        (await app.CommandAsync(new MoveStock(id, 2.5m, Inbound: false))).IsSuccess.ShouldBeTrue();
        var p = await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{id}");
        var store = p.GetProperty("stock").EnumerateArray().Single(e => e.GetProperty("warehouse").GetString() == "Tienda");
        store.GetProperty("quantity").GetDecimal().ShouldBe(-2.5m);
        store.GetProperty("needsCount").GetBoolean().ShouldBeTrue();

        var shrinkage = await c.PostAsJsonAsync("/api/inventory/adjustments", new { ProductId = id, Quantity = -1m, Reason = "Shrinkage" });
        (await shrinkage.ErrorCode()).ShouldBe("Inventory.OutOfStock");
    }

    [Fact]
    public async Task Two_registers_selling_the_last_pound_only_one_wins()
    {
        var c = await Admin();
        var id = await c.ProductAsync(await c.CategoryAsync(), "Lomo", 300m, unit: "Pound", stock: 1);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => app.CommandAsync(new MoveStock(id, 1m, Inbound: false))));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        (await c.StockAsync(id)).ShouldBe(0m);
    }

    [Fact]
    public async Task A_product_sold_by_unit_does_not_accept_fractions()
    {
        var c = await Admin();
        var id = await c.ProductAsync(await c.CategoryAsync(), "Cerveza", 150m, stock: 24);
        var r = await c.PostAsJsonAsync("/api/inventory/adjustments", new { ProductId = id, Quantity = -0.5m, Reason = "Damage" });
        (await r.ErrorCode()).ShouldBe("Inventory.InvalidQuantity");
    }

    [Fact]
    public async Task The_adjustment_reason_stays_in_the_kardex()
    {
        var c = await Admin();
        var id = await c.ProductAsync(await c.CategoryAsync(), "Chorizo", 220m, unit: "Pound", stock: 5);
        (await c.PostAsJsonAsync("/api/inventory/adjustments", new { ProductId = id, Quantity = -0.75m, Reason = "Shrinkage", Note = "se dañó en la nevera" })).EnsureSuccessStatusCode();
        var kardex = await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{id}/movements");
        kardex[0].GetProperty("reason").GetString().ShouldBe("Merma: se dañó en la nevera");
    }

    [Fact]
    public async Task The_PLU_is_stored_without_zeros_and_4_and_5_digit_labels_find_it()
    {
        var c = await Admin();
        var id = await c.ProductAsync(await c.CategoryAsync(), "Pechuga", 140m, unit: "Pound", plu: "0731");
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{id}")).GetProperty("plu").GetString().ShouldBe("731");

        string Label(string twelve) => twelve + Toki.Inventory.Domain.ScaleLabel.CheckDigit(twelve);
        var weight = await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/by-code/{Label("200073101500")}"); // 5-digit PLU, 1.500 lb
        weight.GetProperty("productId").GetGuid().ShouldBe(id);
        weight.GetProperty("labelQuantity").GetDecimal().ShouldBe(1.5m);

        var price = await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/by-code/{Label("220731021000")}"); // 4-digit PLU, RD$ 210.00
        price.GetProperty("productId").GetGuid().ShouldBe(id);
        price.GetProperty("labelAmount").GetDecimal().ShouldBe(210m);

        (await c.GetFromJsonAsync<JsonElement>("/api/inventory/products/by-code/0731")).GetProperty("productId").GetGuid().ShouldBe(id);
    }

    [Fact]
    public async Task A_presentation_barcode_brings_its_price_and_factor()
    {
        var c = await Admin();
        var id = await c.ProductAsync(await c.CategoryAsync("Bebidas"), "Malta", 60m);
        var pr = await (await c.PostAsJsonAsync($"/api/inventory/products/{id}/presentations", new { Name = "Caja 24", Factor = 24m, Price = 1300m })).Json();
        var code = "CAJA-" + Guid.NewGuid().ToString()[..6];
        (await c.PostAsJsonAsync($"/api/inventory/products/{id}/barcodes", new { Code = code, PresentationId = pr.GetProperty("id").GetGuid() })).EnsureSuccessStatusCode();

        var r = await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/by-code/{code}");
        r.GetProperty("price").GetDecimal().ShouldBe(1300m);
        r.GetProperty("factor").GetDecimal().ShouldBe(24m);
        r.GetProperty("name").GetString().ShouldBe("Malta (Caja 24)");
    }

    [Fact]
    public async Task A_weight_label_does_not_apply_to_a_product_sold_by_unit()
    {
        var c = await Admin();
        await c.ProductAsync(await c.CategoryAsync(), "Huevos", 10m, plu: "555");
        var label = "200055501000";
        label += Toki.Inventory.Domain.ScaleLabel.CheckDigit(label);
        (await (await c.GetAsync($"/api/inventory/products/by-code/{label}")).ErrorCode()).ShouldBe("Inventory.LabelNotApplicable");
    }

    [Fact]
    public async Task Creating_a_product_needs_the_price_permission()
    {
        var admin = await Admin();
        var cat = await admin.CategoryAsync();
        var role = await (await admin.PostAsJsonAsync("/api/identity/roles", new { Name = "Catálogo", Permissions = new[] { "products.manage" } })).Json();
        var catalog = app.Client(await app.UserAsync(role.GetProperty("id").GetGuid()));
        var r = await catalog.PostAsJsonAsync("/api/inventory/products", new { Name = "Chuleta barata", CategoryId = cat, Unit = "Pound", ItbisRate = "General18", Price = 1m, Cost = 0m });
        (await r.ErrorCode()).ShouldBe("Inventory.NoPricePermission");
    }

    [Fact]
    public async Task A_custom_label_format_can_be_configured()
    {
        var c = await Admin();
        (await c.PutAsJsonAsync("/api/inventory/label-formats", new
        {
            Formats = new[] { new { Prefix = "20", PluLength = 5, IsPrice = false, Decimals = 3 }, new { Prefix = "29", PluLength = 4, IsPrice = true, Decimals = 2 } },
        })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var id = await c.ProductAsync(await c.CategoryAsync(), "Queso de hoja", 260m, unit: "Pound", plu: "88");
        var label = "290088031250";
        label += Toki.Inventory.Domain.ScaleLabel.CheckDigit(label);
        var r = await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/by-code/{label}");
        r.GetProperty("productId").GetGuid().ShouldBe(id);
        r.GetProperty("labelAmount").GetDecimal().ShouldBe(312.50m);
    }
}

public sealed class BusinessProfileTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task The_administrator_updates_the_business_details_and_the_cashier_cannot()
    {
        var admin = app.Client(await app.AdminAsync());
        var details = new { Name = "Carnicería La Esquina", Rnc = "131-24567-8", Address = "Calle 1", Phone = "809-555-0101", ReceiptFooter = "Gracias", DiscountCapPercent = 5m };
        (await admin.PutAsJsonAsync("/api/settings/business", details)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var business = await admin.GetFromJsonAsync<JsonElement>("/api/settings/business");
        business.GetProperty("rnc").GetString().ShouldBe("131245678");
        business.GetProperty("discountCapPercent").GetDecimal().ShouldBe(5m);

        (await admin.PutAsJsonAsync("/api/settings/business", details with { Rnc = "123" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.PutAsJsonAsync("/api/settings/business", details)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }
}
