using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Purchasing;

/// <summary>TK-191: minimum and maximum per product, the suggested order by last supplier and last cost, and a draft without NCF per supplier.
/// Own app: the order lists every product under its minimum.</summary>
public sealed class SuggestedOrderTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Products_under_their_minimum_are_ordered_up_to_the_maximum_from_their_last_supplier()
    {
        var c = app.Client(await app.AdminAsync());
        var cat = await c.CategoryAsync("Despensa");
        var ribs = await ProductAsync(c, cat, "Costilla", "Pound", minimum: 20, maximum: 50);
        var soda = await ProductAsync(c, cat, "Refresco", "Unit", minimum: 12, maximum: 0);
        var salt = await ProductAsync(c, cat, "Sal", "Unit", minimum: 5, maximum: 10, stock: 2);
        await ProductAsync(c, cat, "Jamón", "Pound", minimum: 5, maximum: 10, stock: 10); // above its minimum: not ordered
        (await (await c.PostAsJsonAsync("/api/inventory/products", new { Name = "Mal", CategoryId = cat, Unit = "Unit", ItbisRate = "General18", Price = 10m, MinimumStock = 9m, MaximumStock = 3m }))
            .ErrorCode()).ShouldBe("Validation");

        var agro = await SupplierAsync(c, "Agro del Cibao");
        var bodega = await SupplierAsync(c, "Bodega Central");
        await PostedAsync(c, agro, (ribs, 10m, 120m), (soda, 6m, 30m));
        await PostedAsync(c, bodega, (ribs, 5.5m, 125m)); // the latest purchase of ribs: Bodega Central at 125

        var groups = (await c.GetFromJsonAsync<JsonElement>("/api/purchasing/suggested-order")).EnumerateArray().ToList();
        groups.Select(g => g.GetProperty("supplier").GetString()).ShouldBe(["Agro del Cibao", "Bodega Central", null]);
        var fromAgro = groups[0].GetProperty("lines").EnumerateArray().ShouldHaveSingleItem();
        (fromAgro.GetProperty("productId").GetGuid(), fromAgro.GetProperty("suggested").GetDecimal(), fromAgro.GetProperty("lastCost").GetDecimal())
            .ShouldBe((soda, 6m, 30m)); // no maximum: back to the minimum of 12
        var fromBodega = groups[1].GetProperty("lines").EnumerateArray().ShouldHaveSingleItem();
        (fromBodega.GetProperty("productId").GetGuid(), fromBodega.GetProperty("stock").GetDecimal()).ShouldBe((ribs, 15.5m));
        (fromBodega.GetProperty("suggested").GetDecimal(), fromBodega.GetProperty("lastCost").GetDecimal()).ShouldBe((35m, 125m)); // 50 − 15.5 = 34.5 → 35 lb
        groups[1].GetProperty("subtotal").GetDecimal().ShouldBe(4375m);
        var neverBought = groups[2].GetProperty("lines").EnumerateArray().ShouldHaveSingleItem();
        (neverBought.GetProperty("productId").GetGuid(), neverBought.GetProperty("suggested").GetDecimal()).ShouldBe((salt, 8m));
        neverBought.GetProperty("lastCost").ValueKind.ShouldBe(JsonValueKind.Null);

        // "Crear compra": a draft without NCF per supplier, with those lines; it is received once the invoice's NCF is written.
        var draft = await DraftAsync(c, bodega, ribs, 35m, 125m);
        (await DraftAsync(c, bodega, ribs, 1m, 125m)).ShouldNotBe(draft); // two drafts waiting for their invoice do not collide
        var saved = await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/purchases/{draft}");
        (saved.GetProperty("status").GetString(), saved.GetProperty("ncf").GetString()).ShouldBe(("Draft", ""));
        (await (await c.PostAsync($"/api/purchasing/purchases/{draft}/post", null)).ErrorCode()).ShouldBe("Purchasing.NcfRequired");
        (await c.StockAsync(ribs)).ShouldBe(15.5m);

        var withNcf = await c.PutAsJsonAsync($"/api/purchasing/purchases/{draft}", new
        {
            SupplierId = bodega, Ncf = "B0100000777", InvoiceDate = Today(), Terms = "Credit",
            Lines = new[] { new { ProductId = ribs, Quantity = 35m, UnitCost = 125m, ItbisRate = "Exempt" } },
        });
        withNcf.StatusCode.ShouldBe(HttpStatusCode.OK, await withNcf.Content.ReadAsStringAsync());
        (await c.PostAsync($"/api/purchasing/purchases/{draft}/post", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.StockAsync(ribs)).ShouldBe(50.5m);
        (await c.GetFromJsonAsync<JsonElement>("/api/purchasing/suggested-order")).EnumerateArray()
            .SelectMany(g => g.GetProperty("lines").EnumerateArray()).ShouldNotContain(l => l.GetProperty("productId").GetGuid() == ribs);
    }

    private DateOnly Today() => DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));

    private static async Task<Guid> ProductAsync(HttpClient c, Guid category, string name, string unit, decimal minimum, decimal maximum, decimal stock = 0)
    {
        var r = await c.PostAsJsonAsync("/api/inventory/products", new
        {
            Name = name, CategoryId = category, Unit = unit, ItbisRate = "Exempt", Price = 100m, Cost = 0m, MinimumStock = minimum, MaximumStock = maximum, InitialStock = stock,
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        var id = (await r.Json()).GetProperty("id").GetGuid();
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{id}")).GetProperty("maximumStock").GetDecimal().ShouldBe(maximum);
        return id;
    }

    private static async Task<Guid> SupplierAsync(HttpClient c, string name) =>
        (await (await c.PostAsJsonAsync("/api/purchasing/suppliers", new { Name = name, Rnc = "101234567", CreditDays = 15 })).Json()).GetProperty("id").GetGuid();

    private async Task PostedAsync(HttpClient c, Guid supplier, params (Guid Product, decimal Quantity, decimal Cost)[] lines)
    {
        var r = await c.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            SupplierId = supplier, Ncf = "B01" + Random.Shared.Next(10_000_000, 99_999_999), InvoiceDate = Today(), Terms = "Cash",
            Lines = lines.Select(l => new { ProductId = l.Product, l.Quantity, UnitCost = l.Cost, ItbisRate = "Exempt" }),
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        (await c.PostAsync($"/api/purchasing/purchases/{(await r.Json()).GetProperty("id").GetGuid()}/post", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<Guid> DraftAsync(HttpClient c, Guid supplier, Guid product, decimal quantity, decimal cost)
    {
        var r = await c.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            Id = Guid.CreateVersion7(), SupplierId = supplier, Ncf = "", InvoiceDate = Today(), Terms = "Credit", Notes = "Pedido sugerido",
            Lines = new[] { new { ProductId = product, Quantity = quantity, UnitCost = cost } },
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await r.Json()).GetProperty("id").GetGuid();
    }
}
