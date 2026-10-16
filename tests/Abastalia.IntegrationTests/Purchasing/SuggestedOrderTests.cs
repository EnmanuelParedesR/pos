using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Purchasing;

/// <summary>TK-191: minimum and maximum per product, the suggested order by last supplier and last cost, and a draft without NCF per supplier.
/// Own app: the order lists every product under its minimum.</summary>
public sealed class SuggestedOrderTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
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

        // R8 #16: who buys without costs.view gets the quantities, not the costs.
        var role = (await (await c.PostAsJsonAsync("/api/identity/roles", new { Name = "Comprador", Permissions = new[] { "purchasing.manage" } })).Json()).GetProperty("id").GetGuid();
        var buyer = (await app.Client(await app.UserAsync(role)).GetFromJsonAsync<JsonElement>("/api/purchasing/suggested-order")).EnumerateArray().ToList();
        buyer.Select(g => g.GetProperty("supplier").GetString()).ShouldBe(["Agro del Cibao", "Bodega Central", null]);
        buyer.ShouldAllBe(g => g.GetProperty("subtotal").ValueKind == JsonValueKind.Null
            && g.GetProperty("lines").EnumerateArray().All(l => l.GetProperty("lastCost").ValueKind == JsonValueKind.Null));
        buyer[1].GetProperty("lines")[0].GetProperty("suggested").GetDecimal().ShouldBe(35m);

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

        // Bought by the box: ordered by the box. 1 box of 12 in stock, maximum 40: 28 missing → 3 boxes (36 units) at the box's last cost.
        var beer = await ProductAsync(c, cat, "Cerveza", "Unit", minimum: 20, maximum: 40);
        var box = (await (await c.PostAsJsonAsync($"/api/inventory/products/{beer}/presentations", new { Name = "Caja 12", Factor = 12m, Price = 1300m })).Json())
            .GetProperty("id").GetGuid();
        var bought = await c.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            SupplierId = agro, Ncf = "B0100000888", InvoiceDate = Today(), Terms = "Cash",
            Lines = new[] { new { ProductId = beer, PresentationId = box, Quantity = 1m, UnitCost = 600m, ItbisRate = "Exempt" } },
        });
        bought.StatusCode.ShouldBe(HttpStatusCode.Created, await bought.Content.ReadAsStringAsync());
        (await c.PostAsync($"/api/purchasing/purchases/{(await bought.Json()).GetProperty("id").GetGuid()}/post", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var agroNow = (await c.GetFromJsonAsync<JsonElement>("/api/purchasing/suggested-order")).EnumerateArray().First(g => g.GetProperty("supplierId").GetGuid() == agro);
        var beerLine = agroNow.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("productId").GetGuid() == beer);
        (beerLine.GetProperty("presentationId").GetGuid(), beerLine.GetProperty("presentation").GetString(), beerLine.GetProperty("factor").GetDecimal())
            .ShouldBe((box, "Caja 12", 12m));
        (beerLine.GetProperty("quantity").GetDecimal(), beerLine.GetProperty("suggested").GetDecimal(), beerLine.GetProperty("lastCost").GetDecimal())
            .ShouldBe((3m, 36m, 600m));
        agroNow.GetProperty("subtotal").GetDecimal().ShouldBe(6 * 30m + 3 * 600m);

        // The draft keeps the box: reopened and received, it adds 36 units, not 3.
        var order = await c.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            Id = Guid.CreateVersion7(), SupplierId = agro, Ncf = "", InvoiceDate = Today(), Terms = "Credit", Notes = "Pedido sugerido",
            Lines = new[] { new { ProductId = beer, PresentationId = (Guid?)box, Quantity = 3m, UnitCost = 600m } },
        });
        order.StatusCode.ShouldBe(HttpStatusCode.Created, await order.Content.ReadAsStringAsync());
        var orderId = (await order.Json()).GetProperty("id").GetGuid();
        var line = (await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/purchases/{orderId}")).GetProperty("lines")[0];
        (line.GetProperty("presentationId").GetGuid(), line.GetProperty("quantity").GetDecimal(), line.GetProperty("factor").GetDecimal()).ShouldBe((box, 3m, 12m));
        (await c.PutAsJsonAsync($"/api/purchasing/purchases/{orderId}", new
        {
            SupplierId = agro, Ncf = "B0100000889", InvoiceDate = Today(), Terms = "Credit",
            Lines = new[] { new { ProductId = beer, PresentationId = line.GetProperty("presentationId").GetGuid(), Quantity = 3m, UnitCost = 600m, ItbisRate = "Exempt" } },
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.PostAsync($"/api/purchasing/purchases/{orderId}/post", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.StockAsync(beer)).ShouldBe(48m);
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
