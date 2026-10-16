using System.Net.Http.Json;

namespace Abastalia.IntegrationTests.Infra;

/// <summary>Builds catalog data through the public API.</summary>
public static class Catalog
{
    public static async Task<Guid> CategoryAsync(this HttpClient c, string name = "Res", string[]? notes = null)
    {
        var r = await c.PostAsJsonAsync("/api/inventory/categories", new { Name = name, Color = "#B42318", Notes = notes ?? ["molida", "sin grasa"] });
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> ProductAsync(this HttpClient c, Guid categoryId, string name, decimal price,
        string unit = "Unit", string rate = "General18", decimal cost = 0, decimal stock = 0, string? code = null,
        string? plu = null, bool sellWithoutStock = false, decimal? wholesale = null)
    {
        var r = await c.PostAsJsonAsync("/api/inventory/products", new
        {
            Name = name, CategoryId = categoryId, Unit = unit, Plu = plu, ItbisRate = rate, Price = price,
            WholesalePrice = wholesale, Cost = cost, SellWithoutStock = sellWithoutStock, MinimumStock = 0,
            Barcodes = code is null ? Array.Empty<string>() : [code], InitialStock = stock,
        });
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    public static async Task<decimal> StockAsync(this HttpClient c, Guid productId, Guid? warehouseId = null)
    {
        var p = await c.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/inventory/products/{productId}");
        foreach (var e in p.GetProperty("stock").EnumerateArray())
            if (warehouseId is null ? e.GetProperty("warehouse").GetString() == "Tienda" : e.GetProperty("warehouseId").GetGuid() == warehouseId)
                return e.GetProperty("quantity").GetDecimal();
        return 0;
    }
}
