using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.CashRegister.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Inventory.Contracts;
using Toki.Sales.Contracts;

namespace Toki.Cloud.Tests;

public sealed class PanelTests(CloudApp app) : IClassFixture<CloudApp>
{
    private static readonly Guid Steak = Guid.CreateVersion7();
    private static readonly Guid Ground = Guid.CreateVersion7();

    public static SaleRecordedV2 Sale(decimal total, Guid cashier, Guid product, string description, params SalePaymentV1[] payments) =>
        new(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), cashier, null, "E320000000001", total, Math.Round(total * 18 / 118, 2), 0,
            [new SaleLineV2(product, null, description, 2, "Pound", total, Math.Round(total * 18 / 118, 2), 100)],
            payments.Length == 0 ? [new SalePaymentV1("Cash", total)] : payments);

    [Fact]
    public async Task Sales_voids_and_shift_closings_show_in_the_summary_of_their_tenant_only()
    {
        var a = await app.OwnerAsync();
        var b = await app.OwnerAsync();
        var nodeA = await app.StoreWithNodeAsync(a, "Tienda A");
        var nodeB = await app.StoreWithNodeAsync(b, "Tienda B");
        var cashier = Guid.CreateVersion7();
        var now = app.Clock.GetUtcNow().UtcDateTime; // 11:00 in the store

        var kept = Sale(500, cashier, Steak, "Bistec de res", new SalePaymentV1("Cash", 300), new SalePaymentV1("Card", 200));
        var voided = Sale(300, cashier, Ground, "Carne molida");
        var shift = new ShiftClosedV1(kept.ShiftId, Guid.CreateVersion7(), cashier, 12, 2300, 2250, -50);
        (await nodeA.SendAsync(
            Events.Envelope(kept, now),
            Events.Envelope(voided, now.AddMinutes(5)),
            Events.Envelope(new SaleVoidedV1(voided.SaleId, "E340000000001", "Cliente se arrepintió", null), now.AddMinutes(10)),
            Events.Envelope(new ReturnRecordedV2(Guid.CreateVersion7(), kept.SaleId, "E340000000002", "Cash", 100, 15.25m, []), now.AddMinutes(20)),
            Events.Envelope(shift, now.AddHours(6)),
            Events.Envelope(new StockMovementRecordedV1(Guid.CreateVersion7(), Steak, Guid.CreateVersion7(), StockMovementType.Sale, -2, 100, kept.SaleId), now),
            Events.Envelope(new PriceChangedV1(Steak, 250, 275, null, null), now),
            Events.Envelope(new FiscalDocumentIssuedV1(Guid.CreateVersion7(), "E320000000001", EcfType.Consumer32, kept.SaleId, 500, 76.27m), now)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await nodeB.SendAsync(Events.Envelope(Sale(1000, Guid.CreateVersion7(), Steak, "Bistec de res"), now))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await app.ApplyAsync()).ShouldBe(9);

        var summary = await a.Client.GetFromJsonAsync<JsonElement>($"/api/panel/summary?date={CloudApp.Today:yyyy-MM-dd}");
        summary.GetProperty("salesTotal").GetDecimal().ShouldBe(500);
        summary.GetProperty("salesCount").GetInt32().ShouldBe(1);
        summary.GetProperty("averageTicket").GetDecimal().ShouldBe(500);
        summary.GetProperty("voidedCount").GetInt32().ShouldBe(1);
        summary.GetProperty("returnsTotal").GetDecimal().ShouldBe(100);
        summary.GetProperty("netTotal").GetDecimal().ShouldBe(400);
        var hour = summary.GetProperty("byHour").EnumerateArray().ShouldHaveSingleItem();
        hour.GetProperty("hour").GetInt32().ShouldBe(11);
        hour.GetProperty("total").GetDecimal().ShouldBe(500);
        var mix = summary.GetProperty("paymentMix").EnumerateArray().ToDictionary(p => p.GetProperty("method").GetString()!, p => p.GetProperty("amount").GetDecimal());
        mix.ShouldBe(new Dictionary<string, decimal> { ["Cash"] = 300, ["Card"] = 200 });
        var issue = summary.GetProperty("issues").EnumerateArray().ShouldHaveSingleItem();
        issue.GetProperty("kind").GetString().ShouldBe("ShiftDifference");
        issue.GetProperty("amount").GetDecimal().ShouldBe(-50);
        issue.GetProperty("storeName").GetString().ShouldBe("Tienda A");
        issue.GetProperty("message").GetString().ShouldBe("Z #12: faltante de RD$ 50.00");

        var other = await b.Client.GetFromJsonAsync<JsonElement>($"/api/panel/summary?date={CloudApp.Today:yyyy-MM-dd}");
        other.GetProperty("salesTotal").GetDecimal().ShouldBe(1000);
        other.GetProperty("issues").GetArrayLength().ShouldBe(0);

        // Asking for the other tenant's store gives nothing: the tenant comes from the token, never from the request.
        var foreign = await b.Client.GetFromJsonAsync<JsonElement>($"/api/panel/summary?date={CloudApp.Today:yyyy-MM-dd}&storeId={nodeA.StoreId}");
        foreign.GetProperty("salesCount").GetInt32().ShouldBe(0);

        var days = await a.Client.GetFromJsonAsync<JsonElement>($"/api/panel/sales/by-day?from=2026-10-14&to={CloudApp.Today:yyyy-MM-dd}");
        days.GetArrayLength().ShouldBe(2);
        days[1].GetProperty("total").GetDecimal().ShouldBe(500);
        days[1].GetProperty("net").GetDecimal().ShouldBe(400);

        var top = await a.Client.GetFromJsonAsync<JsonElement>("/api/panel/sales/top-products");
        var product = top.EnumerateArray().ShouldHaveSingleItem(); // the voided ground beef is left out
        product.GetProperty("description").GetString().ShouldBe("Bistec de res");
        product.GetProperty("quantity").GetDecimal().ShouldBe(2);
        product.GetProperty("unit").GetString().ShouldBe("Pound"); // TK-180

        var shifts = await a.Client.GetFromJsonAsync<JsonElement>("/api/panel/shifts?onlyDifferences=true");
        shifts.EnumerateArray().ShouldHaveSingleItem().GetProperty("zNumber").GetInt32().ShouldBe(12);
        (await b.Client.GetFromJsonAsync<JsonElement>("/api/panel/shifts")).GetArrayLength().ShouldBe(0);

        var cashiers = await a.Client.GetFromJsonAsync<JsonElement>("/api/panel/sales/by-cashier");
        var row = cashiers.EnumerateArray().ShouldHaveSingleItem();
        row.GetProperty("cashierId").GetGuid().ShouldBe(cashier);
        row.GetProperty("voidedCount").GetInt32().ShouldBe(1);

        var sync = await a.Client.GetFromJsonAsync<JsonElement>("/api/panel/sync-status");
        var store = sync.EnumerateArray().ShouldHaveSingleItem();
        store.GetProperty("storeName").GetString().ShouldBe("Tienda A");
        store.GetProperty("lastEventUtc").GetDateTime().ShouldBe(now.AddHours(6));
        store.GetProperty("pendingEvents").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task A_closed_shift_has_its_printable_Z_with_the_opening_sales_by_method_and_cash_movements()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner, "Tienda Z");
        var (cashier, register) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var sale = Sale(500, cashier, Steak, "Bistec de res", new SalePaymentV1("Cash", 300), new SalePaymentV1("Card", 200));
        var voided = new SaleRecordedV2(Guid.CreateVersion7(), 2, sale.ShiftId, cashier, null, "E320000000002", 300, 45.76m, 0,
            [new SaleLineV2(Ground, null, "Carne molida", 1, "Pound", 300, 45.76m, 100)], [new SalePaymentV1("Cash", 300)]);
        (await node.SendAsync(
            Events.Envelope(new ShiftOpenedV1(sale.ShiftId, register, cashier, 1000), now.AddHours(-1)),
            Events.Envelope(sale, now),
            Events.Envelope(voided, now.AddMinutes(1)),
            Events.Envelope(new SaleVoidedV1(voided.SaleId, "E340000000009", "Error", null), now.AddMinutes(2)),
            Events.Envelope(new CashMovementRecordedV1(sale.ShiftId, "Withdrawal", PaymentMethod.Cash, 200, null), now.AddMinutes(3)),
            Events.Envelope(new ShiftClosedV1(sale.ShiftId, register, cashier, 7, 1100, 1100, 0), now.AddHours(1)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.ApplyAsync();

        var z = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/panel/shifts/{sale.ShiftId}");
        (z.GetProperty("shift").GetProperty("zNumber").GetInt32(), z.GetProperty("shift").GetProperty("storeName").GetString()).ShouldBe((7, "Tienda Z"));
        (z.GetProperty("openingFloat").GetDecimal(), z.GetProperty("salesCount").GetInt32(), z.GetProperty("salesTotal").GetDecimal(), z.GetProperty("voidedCount").GetInt32())
            .ShouldBe((1000m, 1, 500m, 1));
        z.GetProperty("payments").EnumerateArray().Select(p => (p.GetProperty("method").GetString(), p.GetProperty("amount").GetDecimal())).ShouldBe([("Cash", 300m), ("Card", 200m)]);
        var cash = z.GetProperty("cashMovements").EnumerateArray().ShouldHaveSingleItem();
        (cash.GetProperty("type").GetString(), cash.GetProperty("amount").GetDecimal()).ShouldBe(("Withdrawal", 200m));

        (await (await app.OwnerAsync()).Client.GetAsync($"/api/panel/shifts/{sale.ShiftId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound); // other tenant
    }

    [Fact]
    public async Task A_range_longer_than_a_year_is_rejected()
    {
        var owner = await app.OwnerAsync();
        var r = await owner.Client.GetAsync("/api/panel/sales/by-day?from=2025-01-01&to=2026-10-15");
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await r.ErrorCode()).ShouldBe("Panel.InvalidRange");
    }
}
