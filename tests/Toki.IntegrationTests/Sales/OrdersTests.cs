using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.IntegrationTests.Infra;
using Toki.Sales.Contracts;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Sales;

/// <summary>TK-193 orders with a deposit, TK-194 quotes, TK-195 the public catalog's events.</summary>
public sealed class OrdersTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    private async Task<Guid> CustomerAsync()
    {
        var r = await (await Admin()).PostAsJsonAsync("/api/customers", new { Name = "Doña " + Guid.NewGuid().ToString()[..4], PriceLevel = "Retail", CreditLimit = 0 });
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> SteakAsync(decimal price = 200m)
    {
        var admin = await Admin();
        return await admin.ProductAsync(await admin.CategoryAsync(), "Bistec " + Guid.NewGuid().ToString()[..4], price, unit: "Pound", rate: "Exempt", cost: 120m, stock: 100);
    }

    private static async Task<JsonElement> OrderAsync(HttpClient c, Guid id) => await c.GetFromJsonAsync<JsonElement>($"/api/orders/{id}");

    private async Task<decimal> CreditBalanceAsync(Guid customer) =>
        (await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/customers/{customer}")).GetProperty("creditBalance").GetDecimal();

    private static object NewOrder(Guid id, Guid customer, Guid product, decimal pounds, object? deposit = null) => new
    {
        OrderId = id, Kind = "Order", CustomerId = customer, Lines = new[] { new { ProductId = product, Quantity = pounds, Note = "picado fino" } },
        PickupDate = TodayInStore().AddDays(1), PickupTime = "10:30:00", Notes = "Lo recoge el hijo", Deposit = deposit,
    };

    private static DateOnly TodayInStore() => Toki.SharedKernel.BusinessDay.Of(DateTime.UtcNow);

    [Fact]
    public async Task A_deposit_enters_the_Z_waits_as_credit_balance_and_pays_part_of_the_sale_with_the_real_weight()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var (customer, steak) = (await CustomerAsync(), await SteakAsync());
        var orderId = Guid.CreateVersion7();

        var created = await cashier.Client.PostAsJsonAsync("/api/orders", NewOrder(orderId, customer, steak, 3m, new { cashier.ShiftId, Method = "Cash", Amount = 300m }));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.Json()).GetProperty("total").GetDecimal().ShouldBe(600m);
        // A retry with the same id takes no second deposit.
        (await cashier.Client.PostAsJsonAsync("/api/orders", NewOrder(orderId, customer, steak, 3m, new { cashier.ShiftId, Method = "Cash", Amount = 300m })))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await app.XReportAsync(cashier.ShiftId)).GetProperty("expectedCash").GetDecimal().ShouldBe(1300m);
        (await CreditBalanceAsync(customer)).ShouldBe(300m);
        (await (await Admin()).StockAsync(steak)).ShouldBe(100m); // an order moves no stock

        var tomorrow = await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/orders?kind=Order&from={TodayInStore():yyyy-MM-dd}&to={TodayInStore().AddDays(1):yyyy-MM-dd}&openOnly=true");
        var listed = tomorrow.EnumerateArray().Single(o => o.GetProperty("id").GetGuid() == orderId);
        (listed.GetProperty("status").GetString(), listed.GetProperty("pickupTime").GetString(), listed.GetProperty("deposit").GetDecimal()).ShouldBe(("Pending", "10:30:00", 300m));
        listed.GetProperty("lines")[0].GetProperty("note").GetString().ShouldBe("picado fino");
        (await cashier.Client.PostAsync($"/api/orders/{orderId}/ready", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // It weighed 3.25 lb: RD$ 650, 300 from the deposit and 350 in cash.
        var sale = new
        {
            SaleId = Guid.CreateVersion7(), cashier.ShiftId, CustomerId = customer, OrderId = orderId, Lines = new[] { Line(steak, 3.25m) },
            Payments = new object[] { new { Method = "CreditBalance", Amount = 300m }, new { Method = "Cash", Amount = 350m } }, ExpectedTotal = 650m,
        };
        (await cashier.SellAsync(sale)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var order = await OrderAsync(cashier.Client, orderId);
        order.GetProperty("status").GetString().ShouldBe("Delivered");
        order.GetProperty("saleId").GetGuid().ShouldBe(sale.SaleId);
        (await CreditBalanceAsync(customer)).ShouldBe(0m);
        var x = await app.XReportAsync(cashier.ShiftId);
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(1650m); // float + deposit + the rest in cash
        x.GetProperty("customerPayments").GetDecimal().ShouldBe(300m);

        // Delivered: it cannot be charged again.
        (await (await cashier.SellAsync(sale with { SaleId = Guid.CreateVersion7() })).ErrorCode()).ShouldBe("Orders.Closed");
    }

    [Fact]
    public async Task Cancelling_needs_a_supervisor_and_gives_the_deposit_back_from_the_drawer()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 500m);
        var (customer, steak) = (await CustomerAsync(), await SteakAsync());
        var orderId = Guid.CreateVersion7();
        (await cashier.Client.PostAsJsonAsync("/api/orders", NewOrder(orderId, customer, steak, 2m, new { cashier.ShiftId, Method = "Cash", Amount = 200m })))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await cashier.Client.PostAsJsonAsync("/api/orders", NewOrder(Guid.CreateVersion7(), customer, steak, 1m, new { cashier.ShiftId, Method = "Cash", Amount = 500m })))
            .ErrorCode()).ShouldBe("Orders.DepositTooBig");

        var cancel = (Guid? approval) => cashier.Client.PostAsJsonAsync($"/api/orders/{orderId}/cancel", new { cashier.ShiftId, Reason = "No vino", SupervisorApprovalId = approval });
        (await (await cancel(null)).ErrorCode()).ShouldBe("Approval.Required");
        (await cancel(await cashier.Client.ApproveAsync("sales.void", orderId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await OrderAsync(cashier.Client, orderId)).GetProperty("status").GetString().ShouldBe("Cancelled");
        (await CreditBalanceAsync(customer)).ShouldBe(0m);
        var x = await app.XReportAsync(cashier.ShiftId);
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(500m); // the deposit came in and went back out
        x.GetProperty("byMethod").EnumerateArray().Single().GetProperty("refunds").GetDecimal().ShouldBe(200m);
    }

    [Fact]
    public async Task A_quote_moves_nothing_and_becomes_a_sale_at_todays_price()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        var steak = await SteakAsync(price: 200m);
        var quoteId = Guid.CreateVersion7();
        var created = await cashier.Client.PostAsJsonAsync("/api/orders", new { OrderId = quoteId, Kind = "Quote", Lines = new[] { new { ProductId = steak, Quantity = 10m } }, ValidDays = 7 });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.Json()).GetProperty("total").GetDecimal().ShouldBe(2000m);
        var quote = await OrderAsync(cashier.Client, quoteId);
        quote.GetProperty("validUntil").GetString().ShouldBe(TodayInStore().AddDays(7).ToString("yyyy-MM-dd"));
        (await app.XReportAsync(cashier.ShiftId)).GetProperty("expectedCash").GetDecimal().ShouldBe(0m);
        (await (await Admin()).StockAsync(steak)).ShouldBe(100m);
        (await (await cashier.Client.PostAsync($"/api/orders/{quoteId}/ready", null)).ErrorCode()).ShouldBe("Orders.NotAnOrder");

        // The price went up: the sale charges today's price (the register showed the difference).
        var admin = await Admin();
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{steak}");
        (await admin.PutAsJsonAsync($"/api/inventory/products/{steak}", new
        {
            Version = detail.GetProperty("version").GetInt32(), Name = detail.GetProperty("name").GetString(), CategoryId = detail.GetProperty("categoryId").GetGuid(),
            Unit = "Pound", ItbisRate = "Exempt", Price = 210m, SellWithoutStock = false, MinimumStock = 0, IsActive = true,
        })).EnsureSuccessStatusCode();
        var saleId = Guid.CreateVersion7();
        (await cashier.SellAsync(new { SaleId = saleId, cashier.ShiftId, OrderId = quoteId, Lines = new[] { Line(steak, 10m) },
            Payments = new[] { new { Method = "Cash", Amount = 2100m } }, ExpectedTotal = 2100m })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var converted = await OrderAsync(cashier.Client, quoteId);
        (converted.GetProperty("status").GetString(), converted.GetProperty("saleId").GetGuid()).ShouldBe(("Delivered", saleId));
        (await (await Admin()).StockAsync(steak)).ShouldBe(90m);
    }

    [Fact]
    public async Task The_catalog_sends_only_public_fields_and_a_hidden_product_goes_as_not_visible()
    {
        var admin = await Admin();
        var (steak, liver) = (await SteakAsync(250m), await SteakAsync(90m));

        (await admin.PutAsJsonAsync("/api/catalog", new { WhatsApp = "809-555-0101", ProductIds = new[] { steak, liver } })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PutAsJsonAsync("/api/catalog", new { WhatsApp = "809-555-0101", ProductIds = new[] { steak } })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await admin.PutAsJsonAsync("/api/catalog", new { WhatsApp = "123", ProductIds = new[] { steak } })).ErrorCode()).ShouldBe("Validation");

        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/catalog");
        settings.GetProperty("whatsApp").GetString().ShouldBe("18095550101");
        settings.GetProperty("productIds").EnumerateArray().Select(i => i.GetGuid()).ShouldBe([steak]);

        var events = await app.InScopeAsync(async sp => (await sp.GetRequiredService<InfraDbContext>().Outbox
                .Where(m => m.Type == typeof(CatalogItemChangedV1).FullName).OrderBy(m => m.Sequence).Select(m => m.Payload).ToListAsync())
            .Select(p => JsonDocument.Parse(p).RootElement.Clone()).Where(e => e.GetProperty("productId").GetGuid() == steak || e.GetProperty("productId").GetGuid() == liver).ToList());
        events.Count.ShouldBe(4);
        events[^1].GetProperty("visible").GetBoolean().ShouldBeFalse(); // liver, taken off on the second save
        events[^1].GetProperty("productId").GetGuid().ShouldBe(liver);
        events.ShouldAllBe(e => e.EnumerateObject().All(p => new[] { "productId", "name", "category", "unit", "price", "visible", "eventId", "occurredUtc", "aggregateId" }.Contains(p.Name)));

        (await app.Client(await app.UserAsync(Roles.Cashier)).GetAsync("/api/catalog")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var qr = await admin.GetFromJsonAsync<JsonElement>($"/api/catalog/qr?url={Uri.EscapeDataString("https://panel.toki.do/c/" + Guid.NewGuid())}");
        qr.GetProperty("svg").GetString()!.ShouldStartWith("<svg");
    }
}
