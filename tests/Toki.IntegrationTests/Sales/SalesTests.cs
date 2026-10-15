using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Sales;

public sealed class SalesTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    private async Task<(Guid PorkChop, Guid Soda)> ProductsAsync()
    {
        var admin = await Admin();
        var cat = await admin.CategoryAsync();
        var porkChop = await admin.ProductAsync(cat, "Chuleta " + Guid.NewGuid().ToString()[..4], 165m, unit: "Pound", rate: "Exempt", cost: 110m, stock: 50);
        var soda = await admin.ProductAsync(cat, "Refresco " + Guid.NewGuid().ToString()[..4], 59m, cost: 35m, stock: 24);
        return (porkChop, soda);
    }

    private Task<int> CountAsync<T>(Func<IServiceProvider, IQueryable<T>> q) => app.InScopeAsync(sp => q(sp).CountAsync());

    [Fact]
    public async Task A_cash_sale_moves_everything_in_one_transaction()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var (porkChop, soda) = await ProductsAsync();

        // 2.35 lb × 165 = 387.75 (exempt) + 2 × 59 = 118 (ITBIS 18 %: 18.00) → 505.75
        var r = await cashier.SellAsync(Sale(cashier.ShiftId, 505.75m, 1000m, Line(porkChop, 2.35m), Line(soda, 2)));
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var sale = await r.Json();
        sale.GetProperty("change").GetDecimal().ShouldBe(494m); // no coins under RD$ 1: the 0.25 stays in the drawer as rounding
        sale.GetProperty("eNcf").GetString()!.ShouldStartWith("E32");
        var receipt = sale.GetProperty("receipt");
        receipt.GetProperty("itbis").EnumerateArray().Sum(i => i.GetProperty("itbis").GetDecimal()).ShouldBe(18m);
        receipt.GetProperty("securityCode").GetString()!.Length.ShouldBe(6);

        var admin = await Admin();
        (await admin.StockAsync(porkChop)).ShouldBe(47.65m);
        (await admin.StockAsync(soda)).ShouldBe(22m);
        var x = await app.XReportAsync(cashier.ShiftId);
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(1506m);
        x.GetProperty("rounding").GetDecimal().ShouldBe(0.25m);
        (await CountAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox.Where(m => m.Type.EndsWith("SaleRecordedV1")))).ShouldBeGreaterThan(0);
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Repeating_the_same_sale_returns_the_same_response_without_duplicating()
    {
        var cashier = await app.CashierWithShiftAsync();
        var (_, soda) = await ProductsAsync();
        var sale = Sale(cashier.ShiftId, 59m, 100m, Line(soda, 1));

        var first = await (await cashier.SellAsync(sale)).Json();
        var second = await cashier.SellAsync(sale);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await second.Json()).GetProperty("eNcf").GetString().ShouldBe(first.GetProperty("eNcf").GetString());
        (await (await Admin()).StockAsync(soda)).ShouldBe(23m);
    }

    [Fact]
    public async Task If_the_total_changed_nothing_is_saved_and_no_eNCF_is_spent()
    {
        var cashier = await app.CashierWithShiftAsync();
        var (_, soda) = await ProductsAsync();

        var before = await (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1)))).Json();
        var wrong = await cashier.SellAsync(Sale(cashier.ShiftId, 50m, 100m, Line(soda, 1)));
        wrong.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await wrong.ErrorCode()).ShouldBe("Sales.TotalChanged");
        var after = await (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1)))).Json();

        var n1 = long.Parse(before.GetProperty("eNcf").GetString()![3..]);
        var n2 = long.Parse(after.GetProperty("eNcf").GetString()![3..]);
        n2.ShouldBe(n1 + 1); // no gap: the failed sale gave its number back
    }

    [Fact]
    public async Task Without_stock_the_whole_sale_fails()
    {
        var cashier = await app.CashierWithShiftAsync();
        var (porkChop, soda) = await ProductsAsync();
        var r = await cashier.SellAsync(Sale(cashier.ShiftId, 59m * 30, 2000m, Line(soda, 30)));
        (await r.ErrorCode()).ShouldBe("Inventory.OutOfStock");
        (await (await Admin()).StockAsync(porkChop)).ShouldBe(50m);
    }

    [Fact]
    public async Task A_discount_above_the_cap_needs_a_supervisor()
    {
        var cashier = await app.CashierWithShiftAsync();
        var (_, soda) = await ProductsAsync();
        var saleId = Guid.CreateVersion7();
        object Body(params Guid[] approvals) => new
        {
            SaleId = saleId, ShiftId = cashier.ShiftId, Lines = new[] { Line(soda, 2) }, GlobalDiscount = new { IsPercentage = true, Value = 20m },
            Payments = new[] { new { Method = "Cash", Amount = 100m } }, ExpectedTotal = 94.40m, SupervisorApprovals = approvals,
        };

        (await (await cashier.SellAsync(Body())).ErrorCode()).ShouldBe("Approval.Required");
        var ok = await cashier.SellAsync(Body(await cashier.Client.ApproveAsync("sales.large-discount", saleId)));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ok.Json()).GetProperty("total").GetDecimal().ShouldBe(94.40m);
    }

    [Fact]
    public async Task Changing_the_price_or_selling_wholesale_needs_a_supervisor()
    {
        var cashier = await app.CashierWithShiftAsync();
        var (_, soda) = await ProductsAsync();
        var r = await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Lines = new[] { new { ProductId = soda, Quantity = 1m, UnitPrice = 40m } },
            Payments = new[] { new { Method = "Cash", Amount = 40m } }, ExpectedTotal = 40m,
        });
        (await r.ErrorCode()).ShouldBe("Approval.Required");
    }

    [Fact]
    public async Task Fiscal_credit_needs_an_RNC()
    {
        var cashier = await app.CashierWithShiftAsync();
        var (_, soda) = await ProductsAsync();
        var withoutRnc = await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Lines = new[] { Line(soda, 1) }, Payments = new[] { new { Method = "Cash", Amount = 59m } },
            ExpectedTotal = 59m, DocumentType = "FiscalCredit31",
        });
        withoutRnc.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var withRnc = await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Lines = new[] { Line(soda, 1) }, Payments = new[] { new { Method = "Cash", Amount = 59m } },
            ExpectedTotal = 59m, DocumentType = "FiscalCredit31", CustomerRnc = "131245678", CustomerName = "Colmado El Sol SRL",
        });
        withRnc.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await withRnc.Json()).GetProperty("eNcf").GetString()!.ShouldStartWith("E31");
    }

    [Fact]
    public async Task Voiding_within_the_shift_returns_stock_and_money_with_a_credit_note()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 500m);
        var (porkChop, _) = await ProductsAsync();
        var sale = await (await cashier.SellAsync(Sale(cashier.ShiftId, 330m, 500m, Line(porkChop, 2m)))).Json();
        var saleId = sale.GetProperty("saleId").GetGuid();

        var withoutPermission = await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/void", new { Reason = "Cliente se arrepintió" });
        (await withoutPermission.ErrorCode()).ShouldBe("Approval.Required");

        var voided = await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/void",
            new { Reason = "Cliente se arrepintió", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", saleId) });
        voided.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await voided.Json()).GetProperty("creditNoteENcf").GetString()!.ShouldStartWith("E34");

        (await (await Admin()).StockAsync(porkChop)).ShouldBe(50m);
        var x = await app.XReportAsync(cashier.ShiftId);
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(500m);
        x.GetProperty("voidedCount").GetInt32().ShouldBe(1);

        var again = await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/void",
            new { Reason = "otra vez", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", saleId) });
        (await again.ErrorCode()).ShouldBe("Sales.AlreadyVoided");
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_partial_return_restocks_what_it_can_and_gives_the_cash_back()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var (porkChop, soda) = await ProductsAsync();
        var sale = await (await cashier.SellAsync(Sale(cashier.ShiftId, 495m + 177m, 1000m, Line(porkChop, 3m), Line(soda, 3)))).Json();
        var saleId = sale.GetProperty("saleId").GetGuid();
        var detail = await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}");
        var porkChopLine = detail.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("description").GetString()!.StartsWith("Chuleta")).GetProperty("id").GetGuid();
        var sodaLine = detail.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("description").GetString()!.StartsWith("Refresco")).GetProperty("id").GetGuid();

        object ReturnBody(Guid id, Guid approval, params object[] lines) =>
            new { ReturnId = id, ShiftId = cashier.ShiftId, Lines = lines, Method = "Cash", Reason = "Mal estado", SupervisorApprovalId = approval };

        // 1 lb of pork chop goes to waste (not restocked) and 1 soda back to the shelf.
        var returnId = Guid.CreateVersion7();
        var r = await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", ReturnBody(returnId, await cashier.Client.ApproveAsync("sales.return", saleId),
            new { SaleLineId = porkChopLine, Quantity = 1m, Restock = false }, new { SaleLineId = sodaLine, Quantity = 1m, Restock = true }));
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ret = await r.Json();
        ret.GetProperty("total").GetDecimal().ShouldBe(165m + 59m);
        ret.GetProperty("creditNoteENcf").GetString()!.ShouldStartWith("E34");

        var admin = await Admin();
        (await admin.StockAsync(porkChop)).ShouldBe(47m);
        (await admin.StockAsync(soda)).ShouldBe(22m);
        (await app.XReportAsync(cashier.ShiftId)).GetProperty("expectedCash").GetDecimal().ShouldBe(1000m + 672m - 224m);

        // Cannot return more than what is left of the line.
        var tooMuch = await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", ReturnBody(Guid.CreateVersion7(),
            await cashier.Client.ApproveAsync("sales.return", saleId), new { SaleLineId = sodaLine, Quantity = 3m, Restock = true }));
        (await tooMuch.ErrorCode()).ShouldBe("Sales.ReturnQuantityExceeded");

        // With returns, a void is no longer possible.
        var voidResponse = await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/void",
            new { Reason = "x", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", saleId) });
        (await voidResponse.ErrorCode()).ShouldBe("Sales.HasReturns");
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task After_the_close_a_sale_cannot_be_voided_it_must_be_returned()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        var (_, soda) = await ProductsAsync();
        var saleId = (await (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1)))).Json()).GetProperty("saleId").GetGuid();
        var close = await cashier.Client.PostAsJsonAsync($"/api/cash/shifts/{cashier.ShiftId}/close", new
        {
            CashCount = new[] { new { Denomination = 50m, Quantity = 1 }, new { Denomination = 5m, Quantity = 1 }, new { Denomination = 1m, Quantity = 4 } },
            SupervisorApprovalId = await cashier.Client.ApproveAsync("register.approve-close", cashier.ShiftId),
        });
        close.EnsureSuccessStatusCode();

        var admin = await Admin();
        var voidResponse = await admin.PostAsJsonAsync($"/api/sales/{saleId}/void", new { Reason = "tarde" });
        (await voidResponse.ErrorCode()).ShouldBe("Sales.OutsideShift");
    }

    [Fact]
    public async Task A_price_label_charges_what_the_label_says()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = await Admin();
        var plu = NewPlu();
        var porkChop = await admin.ProductAsync(await admin.CategoryAsync(), "Chuleta " + plu, 165m, unit: "Pound", rate: "Exempt", cost: 110m, stock: 50, plu: plu);
        var other = await admin.ProductAsync(await admin.CategoryAsync(), "Costilla " + plu, 150m, unit: "Pound", rate: "Exempt", stock: 50);
        object Body(Guid product, decimal total, string code) => new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Payments = new[] { new { Method = "Cash", Amount = 400m } }, ExpectedTotal = total,
            Lines = new[] { new { ProductId = product, Quantity = 1m, ScaleLabel = code } },
        };

        // The server reads the amount from the code: a register that claims a lower total is told the real one.
        var label = PriceLabel(plu, 387.75m);
        (await (await cashier.SellAsync(Body(porkChop, 100m, label))).ErrorCode()).ShouldBe("Sales.TotalChanged");
        // A label printed for another product cannot be charged on this one.
        (await (await cashier.SellAsync(Body(other, 387.75m, label))).ErrorCode()).ShouldBe("Sales.ScaleLabelMismatch");

        var r = await cashier.SellAsync(Body(porkChop, 387.75m, label));
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.StockAsync(porkChop)).ShouldBe(47.65m); // 387.75 / 165 = 2.35 lb
    }

    [Fact]
    public async Task Mixed_payment_and_held_sales()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        var (_, soda) = await ProductsAsync();
        var r = await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Lines = new[] { Line(soda, 4) }, ExpectedTotal = 236m,
            Payments = new object[] { new { Method = "Card", Amount = 200m, Reference = "AUT-778" }, new { Method = "Cash", Amount = 50m } },
        });
        (await r.Json()).GetProperty("change").GetDecimal().ShouldBe(14m);
        var x = await app.XReportAsync(cashier.ShiftId);
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(36m);

        var held = Guid.CreateVersion7();
        (await cashier.Client.PutAsJsonAsync("/api/sales/held", new { Id = held, ShiftId = cashier.ShiftId, Label = "Señora del pollo", Total = 300m, Content = new { lines = 3 } }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var list = await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/held?shiftId={cashier.ShiftId}");
        list[0].GetProperty("content").GetProperty("lines").GetInt32().ShouldBe(3);
        (await cashier.Client.DeleteAsync($"/api/sales/held/{held}?shiftId={cashier.ShiftId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/held?shiftId={cashier.ShiftId}")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Closing_the_shift_deletes_its_held_sales_through_the_outbox()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        (await cashier.Client.PutAsJsonAsync("/api/sales/held", new { Id = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Label = "Olvidada", Total = 10m, Content = new { } }))
            .EnsureSuccessStatusCode();
        (await cashier.Client.PostAsJsonAsync($"/api/cash/shifts/{cashier.ShiftId}/close",
            new { CashCount = Array.Empty<object>(), SupervisorApprovalId = await cashier.Client.ApproveAsync("register.approve-close", cashier.ShiftId) })).EnsureSuccessStatusCode();

        await app.InScopeAsync(sp => sp.GetRequiredService<Toki.BuildingBlocks.Messaging.OutboxProcessor>().ProcessAsync(CancellationToken.None));
        (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/held?shiftId={cashier.ShiftId}")).GetArrayLength().ShouldBe(0);
        (await CountAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox.Where(m => m.Type.EndsWith("ShiftClosedV1") && m.ProcessedUtc == null))).ShouldBe(0);
    }
}
