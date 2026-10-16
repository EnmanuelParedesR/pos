using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using Abastalia.IntegrationTests.Infra;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Reports;

/// <summary>TK-201: cost, profit and stock value only reach users with costs.view. Every endpoint that carries one, for a cashier and a supervisor
/// (who have inventory.view and reports.view but not costs.view) and for the owner. Own app.</summary>
public sealed class CostVisibilityTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task Cost_and_profit_are_null_without_costs_view_and_present_for_the_owner()
    {
        var admin = app.Client(await app.AdminAsync());
        var cat = await admin.CategoryAsync("Res");
        var steak = await admin.ProductAsync(cat, "Bistec", 250m, unit: "Pound", rate: "Exempt", cost: 150m, stock: 20);
        var cashier = await app.CashierWithShiftAsync();
        (await cashier.SellAsync(Sale(cashier.ShiftId, 500m, 500m, Line(steak, 2m)))).EnsureSuccessStatusCode();
        var supervisor = app.Client(await app.UserAsync(Roles.Supervisor));
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        var period = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";

        // The stock keeper sees stock (inventory.view), never its cost (the seeded Cajero no longer sees the inventory screens, R11 A9).
        var keeper = app.Client(await app.UserAsync(Roles.StockKeeper));
        var stock = (await keeper.GetFromJsonAsync<JsonElement>("/api/inventory/stock")).EnumerateArray().Single(s => s.GetProperty("productId").GetGuid() == steak);
        stock.GetProperty("quantity").GetDecimal().ShouldBe(18m);
        Null(stock, "cost", "value");
        Null(await keeper.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{steak}"), "cost");
        foreach (var m in (await keeper.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{steak}/movements")).EnumerateArray())
            Null(m, "unitCost");

        // The supervisor sees reports (reports.view) without cost, profit or value at cost; amounts sold are still there.
        var day = (await supervisor.GetFromJsonAsync<JsonElement>($"/api/reports/sales/by-day?{period}")).EnumerateArray().Single();
        day.GetProperty("netWithoutItbis").GetDecimal().ShouldBe(500m);
        Null(day, "cost", "profit");
        Null((await supervisor.GetFromJsonAsync<JsonElement>($"/api/reports/sales/by-product?{period}"))[0], "cost", "profit");
        Null((await supervisor.GetFromJsonAsync<JsonElement>($"/api/reports/sales/by-category?{period}"))[0], "cost", "profit");
        var value = (await supervisor.GetFromJsonAsync<JsonElement>("/api/reports/inventory/value")).EnumerateArray().Single(v => v.GetProperty("productId").GetGuid() == steak);
        value.GetProperty("saleValue").GetDecimal().ShouldBe(4500m);
        Null(value, "cost", "costValue");
        // Exports too: the cost and profit columns are there, empty.
        var csv = Encoding.UTF8.GetString(await supervisor.GetByteArrayAsync($"/api/reports/sales/by-day?format=csv&{period}")).TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        csv[0].ShouldEndWith("NetoSinItbis,Costo,Utilidad");
        csv[1].ShouldEndWith(",,");

        // The owner sees them all.
        (await admin.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{steak}")).GetProperty("cost").GetDecimal().ShouldBe(150m);
        var owned = (await admin.GetFromJsonAsync<JsonElement>("/api/inventory/stock")).EnumerateArray().Single(s => s.GetProperty("productId").GetGuid() == steak);
        owned.GetProperty("value").GetDecimal().ShouldBe(2700m);
        var ownerDay = (await admin.GetFromJsonAsync<JsonElement>($"/api/reports/sales/by-day?{period}")).EnumerateArray().Single();
        (ownerDay.GetProperty("cost").GetDecimal(), ownerDay.GetProperty("profit").GetDecimal()).ShouldBe((300m, 200m));
        (await admin.GetFromJsonAsync<JsonElement>("/api/identity/me")).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldContain("costs.view");
    }

    private static void Null(JsonElement item, params string[] fields)
    {
        foreach (var f in fields) item.GetProperty(f).ValueKind.ShouldBe(JsonValueKind.Null, f);
    }
}
