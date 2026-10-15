using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Reports;

/// <summary>R5 #2: a return lands on the day it happens in every report, and a closed day never changes. Own app: it moves the clock.</summary>
public sealed class ReturnOnAnotherDayTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task A_return_the_next_day_counts_on_that_day_in_every_report()
    {
        var admin = app.Client(await app.AdminAsync());
        var soda = await admin.ProductAsync(await admin.CategoryAsync("Bebidas"), "Refresco", 59m, cost: 35m, stock: 10);
        var cashier = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var sale = (await (await cashier.SellAsync(Sale(cashier.ShiftId, 177m, 177m, Line(soda, 3m)))).Json()).GetProperty("saleId").GetGuid();
        var day1 = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        async Task<string> Report(string route, DateOnly day) => (await admin.GetFromJsonAsync<JsonElement>($"/api/reports/{route}?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}")).ToString();
        var before = (await Report("sales/by-day", day1), await Report("sales/by-product", day1), await Report("sales/by-category", day1));

        // A day later the sessions have expired (12 h): the owner logs in again and takes the return on the same shift.
        app.Clock.Advance(TimeSpan.FromDays(1));
        var day2 = day1.AddDays(1);
        admin = app.Client(await app.SignInAsync("admin", TokiApp.AdminPin, (await app.AdminAsync()).Device));
        var line = (await admin.GetFromJsonAsync<JsonElement>($"/api/sales/{sale}")).GetProperty("lines")[0].GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync($"/api/sales/{sale}/returns", new
        {
            ReturnId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Method = "Cash", Reason = "vencido", Lines = new[] { new { SaleLineId = line, Quantity = 1m } },
        })).EnsureSuccessStatusCode();

        // Day 1 does not change after the fact.
        (await Report("sales/by-day", day1), await Report("sales/by-product", day1), await Report("sales/by-category", day1)).ShouldBe(before);

        // Day 2: −59 with ITBIS, −50 without, its cost comes back (35): profit −15 by day, by product and by category alike.
        var byDay = JsonDocument.Parse(await Report("sales/by-day", day2)).RootElement.EnumerateArray().Single();
        byDay.GetProperty("returns").GetDecimal().ShouldBe(59m);
        byDay.GetProperty("profit").GetDecimal().ShouldBe(-15m);
        JsonDocument.Parse(await Report("sales/by-product", day2)).RootElement.EnumerateArray().Single().GetProperty("quantity").GetDecimal().ShouldBe(-1m);
        JsonDocument.Parse(await Report("sales/by-category", day2)).RootElement.EnumerateArray().Sum(c => c.GetProperty("profit").GetDecimal()).ShouldBe(-15m);
    }
}
