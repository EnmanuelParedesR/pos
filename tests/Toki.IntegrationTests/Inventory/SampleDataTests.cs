using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Inventory;

/// <summary>TK-196: "datos de ejemplo" load with one click, go away with one click and never reach the DGII reports. Own app: exact counts.</summary>
public sealed class SampleDataTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Sample_data_loads_once_stays_out_of_606_607_608_and_is_removed_with_one_click()
    {
        var admin = app.Client(await app.AdminAsync());
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        var period = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        async Task<int> Rows(string report) => (await admin.GetFromJsonAsync<JsonElement>($"/api/reports/dgii/{report}?{period}")).GetArrayLength();

        var loaded = await (await admin.PostAsync("/api/inventory/sample-data", null)).Json();
        (loaded.GetProperty("products").GetInt32(), loaded.GetProperty("categories").GetInt32()).ShouldBe((9, 3));
        (await (await admin.PostAsync("/api/inventory/sample-data", null)).Json()).GetProperty("products").GetInt32().ShouldBe(9); // not twice
        var products = (await admin.GetFromJsonAsync<JsonElement>("/api/inventory/products")).EnumerateArray().ToList();
        products.Count.ShouldBe(9);
        products.ShouldAllBe(p => p.GetProperty("name").GetString()!.EndsWith("(ejemplo)", StringComparison.Ordinal));

        // Catalog only: nothing of it is a fiscal document.
        (await Rows("606"), await Rows("607"), await Rows("608")).ShouldBe((0, 0, 0));

        // One is sold to try the register: a real sale with a real e-CF, so it stays in the 607 and its product only goes inactive.
        var sold = products[0].GetProperty("id").GetGuid();
        var price = products[0].GetProperty("price").GetDecimal();
        var cashier = await app.CashierWithShiftAsync();
        (await cashier.SellAsync(Sale(cashier.ShiftId, price, price, Line(sold, 1)))).EnsureSuccessStatusCode();
        var real = await admin.ProductAsync(await admin.CategoryAsync("Res"), "Bistec", 250m, stock: 10);

        var removed = await (await admin.DeleteAsync("/api/inventory/sample-data")).Json();
        (removed.GetProperty("products").GetInt32(), removed.GetProperty("categories").GetInt32()).ShouldBe((0, 0));
        (await admin.GetFromJsonAsync<JsonElement>("/api/inventory/products")).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ShouldBe([real]);
        var all = (await admin.GetFromJsonAsync<JsonElement>("/api/inventory/products?includeInactive=true")).EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        all.Order().ShouldBe(new[] { real, sold }.Order());
        // The sold product's category stays, inactive, for its history; the other two are gone.
        var categories = (await admin.GetFromJsonAsync<JsonElement>("/api/inventory/categories")).EnumerateArray().Where(c => c.GetProperty("name").GetString()!.Contains("ejemplo")).ToList();
        categories.ShouldHaveSingleItem().GetProperty("isActive").GetBoolean().ShouldBeFalse();
        (await Rows("607")).ShouldBe(1);
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
    }
}
