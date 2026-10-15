using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.CashRegister.Contracts;
using Toki.Customers.Contracts;
using Toki.Host.Cloud.Assistant;
using Toki.Host.Cloud.Panel;
using Toki.Identity.Contracts;
using Toki.Purchasing.Contracts;
using Toki.Sales.Contracts;

namespace Toki.Cloud.Tests;

/// <summary>TK-165, TK-166, TK-167: names, returned lines and the rest of the store's events in the replica.</summary>
public sealed class ReplicaTests(CloudApp app) : IClassFixture<CloudApp>
{
    [Fact]
    public async Task Cashier_and_register_names_show_in_the_panel_and_the_assistant_and_an_older_name_never_wins()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var cashier = Guid.CreateVersion7();
        var register = Guid.CreateVersion7();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var sale = PanelTests.Sale(500, cashier, Guid.CreateVersion7(), "Bistec de res");

        (await node.SendAsync(
            Events.Envelope(new UserProfileChangedV1(cashier, "Yudelka", true), now),
            Events.Envelope(new UserProfileChangedV1(cashier, "Nombre viejo", true), now.AddHours(-1)), // arrives late
            Events.Envelope(new RegisterChangedV1(register, "Caja 1"), now),
            Events.Envelope(sale, now),
            Events.Envelope(new ShiftClosedV1(sale.ShiftId, register, cashier, 3, 1000, 950, -50), now.AddHours(1)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.ApplyAsync()).ShouldBe(5);

        var byCashier = await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/sales/by-cashier");
        byCashier.EnumerateArray().ShouldHaveSingleItem().GetProperty("cashierName").GetString().ShouldBe("Yudelka");

        var shift = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/shifts")).EnumerateArray().ShouldHaveSingleItem();
        (shift.GetProperty("cashierName").GetString(), shift.GetProperty("registerName").GetString()).ShouldBe(("Yudelka", "Caja 1"));

        var issue = (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/panel/summary?date={CloudApp.Today:yyyy-MM-dd}"))
            .GetProperty("issues").EnumerateArray().ShouldHaveSingleItem();
        issue.GetProperty("cashierName").GetString().ShouldBe("Yudelka");
        issue.GetProperty("registerName").GetString().ShouldBe("Caja 1");
        issue.GetProperty("message").GetString().ShouldBe("Z #3 (Yudelka): faltante de RD$ 50.00");

        await using var scope = app.Services.CreateAsyncScope();
        var assistant = new OwnerAssistant(null!, scope.ServiceProvider.GetRequiredService<PanelQueries>()); // tools only: no model call
        var day = $$"""{"from":"{{CloudApp.Today:yyyy-MM-dd}}","to":"{{CloudApp.Today:yyyy-MM-dd}}"}""";
        (await assistant.RunToolAsync(owner.TenantId, new ChatToolCall("1", "sales_by_cashier", day), CancellationToken.None)).ShouldContain("\"cashierName\":\"Yudelka\"");
        (await assistant.RunToolAsync(owner.TenantId, new ChatToolCall("2", "shift_differences", day), CancellationToken.None)).ShouldContain("\"registerName\":\"Caja 1\"");
    }

    [Fact]
    public async Task Returned_lines_are_subtracted_from_the_top_products_of_the_day_they_happened()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var steak = Guid.CreateVersion7();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var sale = PanelTests.Sale(500, Guid.CreateVersion7(), steak, "Bistec de res"); // 2 at 250, ITBIS 76.27, unit cost 100
        var returned = new ReturnRecordedV2(Guid.CreateVersion7(), sale.SaleId, "E340000000009", "Cash", 250, 38.14m,
            [new ReturnLineV2(steak, null, "Bistec de res", 1, 250, 38.14m, 100)]);

        await node.SendAsync(Events.Envelope(sale, now), Events.Envelope(returned, now.AddMinutes(30)));
        (await app.ApplyAsync()).ShouldBe(2);

        var product = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/sales/top-products")).EnumerateArray().ShouldHaveSingleItem();
        product.GetProperty("quantity").GetDecimal().ShouldBe(1);
        product.GetProperty("total").GetDecimal().ShouldBe(250);
        product.GetProperty("profit").GetDecimal().ShouldBe(111.87m); // (500 − 76.27 − 200) − (250 − 38.14 − 100)

        // Applying the return again replaces its lines instead of adding them.
        await node.SendAsync(Events.Envelope(returned with { EventId = Guid.CreateVersion7() }, now.AddMinutes(30)));
        await app.ApplyAsync();
        (await app.InDbAsync(db => db.ReturnLines.CountAsync(l => l.ReturnId == returned.ReturnId))).ShouldBe(1);
    }

    [Fact]
    public async Task Receivables_purchases_shift_openings_and_cash_movements_are_projected()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var (ana, luis) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var (s1, s2, s3) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var purchase = new PurchasePostedV1(Guid.CreateVersion7(), Guid.CreateVersion7(), "B0100000001", 1000, 180, 1180, true);
        var shift = new ShiftOpenedV1(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 2000);

        (await node.SendAsync(
            Events.Envelope(new OnAccountChargeRecordedV1(ana, s1, 500), now),
            Events.Envelope(new OnAccountChargeRecordedV1(ana, s2, 300), now),
            Events.Envelope(new SaleVoidedV1(s2, "E340000000010", "Error", null), now), // its charge no longer counts
            Events.Envelope(new ReturnRecordedV2(Guid.CreateVersion7(), s1, "E340000000011", "CreditBalance", 50, 7.63m, []), now),
            Events.Envelope(new CustomerPaymentRecordedV1(ana, Guid.CreateVersion7(), 100), now),
            Events.Envelope(new OnAccountChargeRecordedV1(luis, s3, 200), now),
            Events.Envelope(purchase, now),
            Events.Envelope(new PurchaseVoidedV1(purchase.PurchaseId, "Factura duplicada"), now),
            Events.Envelope(shift, now),
            Events.Envelope(new CashMovementRecordedV1(shift.ShiftId, "Withdrawal", PaymentMethod.Cash, -500, null), now))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.ApplyAsync()).ShouldBe(10);

        var receivables = await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/receivables");
        receivables.GetProperty("total").GetDecimal().ShouldBe(550); // Ana 500 − 50 − 100, Luis 200
        receivables.GetProperty("customerCount").GetInt32().ShouldBe(2);
        var top = receivables.GetProperty("topDebtors")[0];
        (top.GetProperty("customerId").GetGuid(), top.GetProperty("debt").GetDecimal()).ShouldBe((ana, 350m));
        (await (await app.OwnerAsync()).Client.GetFromJsonAsync<JsonElement>("/api/panel/receivables")).GetProperty("total").GetDecimal().ShouldBe(0);

        (await app.InDbAsync(db => db.Purchases.CountAsync(p => p.Id == purchase.PurchaseId && p.OnCredit))).ShouldBe(1);
        (await app.InDbAsync(db => db.PurchaseVoids.CountAsync(v => v.PurchaseId == purchase.PurchaseId))).ShouldBe(1);
        (await app.InDbAsync(db => db.OpenedShifts.SingleAsync(s => s.ShiftId == shift.ShiftId))).OpeningFloat.ShouldBe(2000);
        (await app.InDbAsync(db => db.CashMovements.SingleAsync(m => m.ShiftId == shift.ShiftId))).Method.ShouldBe("Cash");
    }

    [Fact]
    public async Task A_customer_balance_event_gives_the_stores_own_debt_with_the_name_and_an_older_one_never_wins()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var (ana, luis) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var sale = Guid.CreateVersion7();

        (await node.SendAsync(
            Events.Envelope(new OnAccountChargeRecordedV1(ana, sale, 500), now),
            Events.Envelope(new CustomerPaymentRecordedV1(ana, Guid.CreateVersion7(), 200), now.AddMinutes(1)),
            // The payment was voided: no other event says so, the balance does.
            Events.Envelope(new CustomerBalanceChangedV1(ana, "Ana Pérez", 500, 0), now.AddMinutes(2)),
            Events.Envelope(new CustomerBalanceChangedV1(ana, "Ana", 300, 0), now.AddMinutes(1)), // arrives late
            Events.Envelope(new OnAccountChargeRecordedV1(luis, Guid.CreateVersion7(), 120), now))).StatusCode.ShouldBe(HttpStatusCode.OK); // older store: no balance
        (await app.ApplyAsync()).ShouldBe(5);

        var receivables = await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/receivables");
        receivables.GetProperty("total").GetDecimal().ShouldBe(620);
        var top = receivables.GetProperty("topDebtors").EnumerateArray().ToList();
        (top[0].GetProperty("customerName").GetString(), top[0].GetProperty("debt").GetDecimal()).ShouldBe(("Ana Pérez", 500m));
        (top[1].GetProperty("customerId").GetGuid(), top[1].GetProperty("debt").GetDecimal()).ShouldBe((luis, 120m));
        top[1].GetProperty("customerName").ValueKind.ShouldBe(JsonValueKind.Null);

        // Paid off: the customer leaves the list.
        await node.SendAsync(Events.Envelope(new CustomerBalanceChangedV1(ana, "Ana Pérez", 0, 25), now.AddMinutes(3)));
        await app.ApplyAsync();
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/receivables")).GetProperty("total").GetDecimal().ShouldBe(120);
    }

    [Fact]
    public async Task A_sale_uploaded_as_V1_still_counts_in_top_products_without_a_unit()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var sale = new SaleRecordedV1(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), Guid.CreateVersion7(), null, "E320000000001", 300, 0, 0,
            [new SaleLineV1(Guid.CreateVersion7(), null, "Chuleta", 1.5m, 300, 0, 150)], [new SalePaymentV1("Cash", 300)]);

        await node.SendAsync(Events.Envelope(sale, app.Clock.GetUtcNow().UtcDateTime));
        (await app.ApplyAsync()).ShouldBe(1);

        var product = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/sales/top-products")).EnumerateArray().ShouldHaveSingleItem();
        product.GetProperty("quantity").GetDecimal().ShouldBe(1.5m);
        product.GetProperty("unit").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
