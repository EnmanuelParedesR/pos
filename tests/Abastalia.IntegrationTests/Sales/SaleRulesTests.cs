using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Abastalia.IntegrationTests.Infra;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Sales;

/// <summary>Price, discount, unit and shift rules a sale must enforce on the server (review R3).</summary>
public sealed class SaleRulesTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    private async Task<Guid> ProductAsync(string name, decimal price, decimal cost = 0, string unit = "Unit", string rate = "General18",
        decimal stock = 100, decimal? wholesale = null)
    {
        var admin = await Admin();
        return await admin.ProductAsync(await admin.CategoryAsync(), $"{name} {Guid.NewGuid().ToString()[..4]}", price, unit: unit, rate: rate,
            cost: cost, stock: stock, wholesale: wholesale);
    }

    private static object Body(Guid saleId, Guid shiftId, decimal total, decimal cash, object[] lines, string priceLevel = "Retail", Guid[]? approvals = null) => new
    {
        SaleId = saleId, ShiftId = shiftId, Lines = lines, Payments = new[] { new { Method = "Cash", Amount = cash } }, ExpectedTotal = total,
        PriceLevel = priceLevel, SupervisorApprovals = approvals ?? [],
    };

    private static async Task<string?> RequestedPermission(HttpResponseMessage r)
    {
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var detail = (await r.Json()).GetProperty("detail").GetString()!;
        return detail[(detail.LastIndexOf('(') + 1)..^2];
    }

    [Fact]
    public async Task A_large_discount_on_a_single_line_needs_a_supervisor_even_if_the_sale_is_large()
    {
        var cashier = await app.CashierWithShiftAsync();
        var porkChop = await ProductAsync("Chuleta", 165m, cost: 110m, unit: "Pound", rate: "Exempt");
        var soda = await ProductAsync("Refresco", 59m, cost: 35m);

        // 20 % off the sodas is 1.3 % of the whole sale, but 20 % of that line (cap: 10 %).
        var lines = new object[] { Line(porkChop, 10m), new { ProductId = soda, Quantity = 2m, Discount = new { IsPercentage = true, Value = 20m } } };
        var saleId = Guid.CreateVersion7();
        (await RequestedPermission(await cashier.SellAsync(Body(saleId, cashier.ShiftId, 1744.40m, 1745m, lines)))).ShouldBe("sales.large-discount");

        var ok = await cashier.SellSupervisedAsync(saleId, approvals => Body(saleId, cashier.ShiftId, 1744.40m, 1745m, lines, approvals: approvals));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_sale_with_a_100_percent_discount_is_recorded_without_payments()
    {
        // TK-239: RD$ 0 (a gift or a promotion): no payment, still an e-CF (the XSD allows MontoTotal 0) and a sale on the shift.
        var cashier = await app.CashierWithShiftAsync();
        var soda = await ProductAsync("Refresco", 59m);
        var saleId = Guid.CreateVersion7();
        object Sale(Guid[] approvals) => new
        {
            SaleId = saleId, ShiftId = cashier.ShiftId, Lines = new[] { new { ProductId = soda, Quantity = 1m, Discount = new { IsPercentage = true, Value = 100m } } },
            Payments = Array.Empty<object>(), ExpectedTotal = 0m, SupervisorApprovals = approvals,
        };
        (await RequestedPermission(await cashier.SellAsync(Sale([])))).ShouldBe("sales.large-discount");

        var ok = await cashier.SellSupervisedAsync(saleId, Sale);
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        var sale = await ok.Json();
        (sale.GetProperty("total").GetDecimal(), sale.GetProperty("change").GetDecimal()).ShouldBe((0m, 0m));
        sale.GetProperty("eNcf").GetString().ShouldStartWith("E32");

        // A sale with a total still needs a payment.
        var other = new { SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Lines = new[] { Line(soda, 1m) }, Payments = Array.Empty<object>(), ExpectedTotal = 59m };
        (await cashier.SellAsync(other)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_RD_0_sale_counts_in_the_Z_sales()
    {
        var cashier = await app.CashierWithShiftAsync();
        var soda = await ProductAsync("Refresco", 59m);
        var saleId = Guid.CreateVersion7();
        (await cashier.SellSupervisedAsync(saleId, approvals => new
        {
            SaleId = saleId, ShiftId = cashier.ShiftId, Lines = new[] { new { ProductId = soda, Quantity = 1m, Discount = new { IsPercentage = true, Value = 100m } } },
            Payments = Array.Empty<object>(), ExpectedTotal = 0m, SupervisorApprovals = approvals,
        })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var z = await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/cash/shifts/{cashier.ShiftId}/z");
        z.GetProperty("saleCount").GetInt32().ShouldBe(1);
        z.GetProperty("expectedCash").GetDecimal().ShouldBe(z.GetProperty("openingFloat").GetDecimal());
    }

    [Fact]
    public async Task A_small_discount_that_leaves_the_line_below_cost_needs_a_supervisor()
    {
        var cashier = await app.CashierWithShiftAsync();
        var water = await ProductAsync("Agua", 118m, cost: 95m); // 100 before ITBIS

        // 10 % (inside the cap) leaves 90 before ITBIS, under the cost of 95.
        var lines = new object[] { new { ProductId = water, Quantity = 1m, Discount = new { IsPercentage = true, Value = 10m } } };
        var saleId = Guid.CreateVersion7();
        (await RequestedPermission(await cashier.SellAsync(Body(saleId, cashier.ShiftId, 106.20m, 107m, lines)))).ShouldBe("sales.price-override");

        var ok = await cashier.SellSupervisedAsync(saleId, approvals => Body(saleId, cashier.ShiftId, 106.20m, 107m, lines, approvals: approvals));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Wholesale_uses_the_wholesale_price_and_without_a_wholesale_customer_needs_a_supervisor()
    {
        var cashier = await app.CashierWithShiftAsync();
        var rice = await ProductAsync("Arroz", 50m, cost: 30m, wholesale: 45m);
        var saleId = Guid.CreateVersion7();
        var lines = new object[] { Line(rice, 10m) };

        (await RequestedPermission(await cashier.SellAsync(Body(saleId, cashier.ShiftId, 450m, 450m, lines, "Wholesale")))).ShouldBe("sales.price-override");
        var ok = await cashier.SellSupervisedAsync(saleId, approvals => Body(saleId, cashier.ShiftId, 450m, 450m, lines, "Wholesale", approvals));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ok.Json()).GetProperty("total").GetDecimal().ShouldBe(450m);
    }

    [Fact]
    public async Task A_presentation_moves_its_factor_prints_its_name_and_is_returned_proportionally()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = await Admin();
        var beer = await ProductAsync("Cerveza", 120m, cost: 80m, stock: 48);
        var box = (await (await admin.PostAsJsonAsync($"/api/inventory/products/{beer}/presentations", new { Name = "Caja 12", Factor = 12m, Price = 1300m }))
            .Json()).GetProperty("id").GetGuid();

        var sale = await (await cashier.SellAsync(Sale(cashier.ShiftId, 2600m, 2600m, new { ProductId = beer, PresentationId = box, Quantity = 2m }))).Json();
        (await admin.StockAsync(beer)).ShouldBe(24m);
        var saleId = sale.GetProperty("saleId").GetGuid();
        var line = (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}")).GetProperty("lines")[0];
        line.GetProperty("description").GetString()!.ShouldEndWith("(Caja 12)");

        var ret = await cashier.Client.SupervisedAsync(saleId, approval => cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", new
        {
            ReturnId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Method = "Cash", Reason = "Sobró",
            Lines = new[] { new { SaleLineId = line.GetProperty("id").GetGuid(), Quantity = 1m } }, SupervisorApprovalId = approval,
        }));
        (await ret.Json()).GetProperty("total").GetDecimal().ShouldBe(1300m);
        (await admin.StockAsync(beer)).ShouldBe(36m);
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task What_is_sold_by_unit_does_not_accept_fractions()
    {
        var cashier = await app.CashierWithShiftAsync();
        var soda = await ProductAsync("Refresco", 59m);
        (await (await cashier.SellAsync(Sale(cashier.ShiftId, 88.50m, 89m, Line(soda, 1.5m)))).ErrorCode()).ShouldBe("Sales.InvalidQuantity");

        var saleId = (await (await cashier.SellAsync(Sale(cashier.ShiftId, 118m, 118m, Line(soda, 2m)))).Json()).GetProperty("saleId").GetGuid();
        var line = (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}")).GetProperty("lines")[0].GetProperty("id").GetGuid();
        var ret = await cashier.Client.SupervisedAsync(saleId, approval => cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", new
        {
            ReturnId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Method = "Cash", Reason = "x",
            Lines = new[] { new { SaleLineId = line, Quantity = 0.5m } }, SupervisorApprovalId = approval,
        }));
        (await ret.ErrorCode()).ShouldBe("Sales.InvalidQuantity");
    }

    [Fact]
    public async Task A_consumer_sale_from_250_thousand_needs_an_RNC()
    {
        var cashier = await app.CashierWithShiftAsync();
        var fridge = await ProductAsync("Nevera", 125_000m, stock: 4);
        (await (await cashier.SellAsync(Sale(cashier.ShiftId, 250_000m, 250_000m, Line(fridge, 2m)))).ErrorCode()).ShouldBe("Sales.RncRequired");

        var withRnc = await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Lines = new[] { Line(fridge, 2m) }, ExpectedTotal = 250_000m,
            Payments = new[] { new { Method = "Transfer", Amount = 250_000m, Reference = "TRF-250" } }, CustomerRnc = "001-1234567-8", CustomerName = "Juan Pérez",
        });
        withRnc.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_same_SaleId_with_another_cart_does_not_return_the_old_sale()
    {
        var cashier = await app.CashierWithShiftAsync();
        var soda = await ProductAsync("Refresco", 59m);
        var saleId = Guid.CreateVersion7();
        (await cashier.SellAsync(Body(saleId, cashier.ShiftId, 59m, 59m, [Line(soda, 1m)]))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var other = await cashier.SellAsync(Body(saleId, cashier.ShiftId, 118m, 118m, [Line(soda, 2m)]));
        other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await other.ErrorCode()).ShouldBe("Sales.DuplicateId");
    }

    [Fact]
    public async Task No_sale_on_a_closed_shift_or_on_another_cashiers_shift()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        var other = await app.CashierWithShiftAsync();
        var soda = await ProductAsync("Refresco", 59m);

        (await (await other.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1m)))).ErrorCode()).ShouldBe("CashRegister.NotYourShift");

        (await cashier.Client.PostAsJsonAsync($"/api/cash/shifts/{cashier.ShiftId}/close", new
        {
            CashCount = Array.Empty<object>(), SupervisorApprovalId = await cashier.Client.ApproveAsync("register.approve-close", cashier.ShiftId),
        })).EnsureSuccessStatusCode();
        (await (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1m)))).ErrorCode()).ShouldBe("CashRegister.ShiftClosed");
    }
}
