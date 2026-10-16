using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.IntegrationTests.Infra;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Customers;

public sealed class CustomersTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    private async Task<Guid> CustomerAsync(decimal limit = 0, string priceLevel = "Retail", string? taxId = null)
    {
        // These tests are about credit rules; whether selling on credit asks a supervisor (R11 A6) is tested in SaleRulesTests.
        await app.DoNotAskSupervisorAsync("credit.sell");
        var r = await (await Admin()).PostAsJsonAsync("/api/customers", new { Name = "Doña " + Guid.NewGuid().ToString()[..4], TaxId = taxId, PriceLevel = priceLevel, CreditLimit = limit });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ProductAsync(decimal price = 100m, decimal? wholesale = null)
    {
        var admin = await Admin();
        return await admin.ProductAsync(await admin.CategoryAsync(), "Arroz " + Guid.NewGuid().ToString()[..4], price, rate: "Exempt", stock: 500, wholesale: wholesale, cost: 50m);
    }

    private static object OnAccountSale(Guid shift, Guid customer, Guid product, int quantity, decimal total, params Guid[] approvals) => new
    {
        SaleId = Guid.CreateVersion7(), ShiftId = shift, CustomerId = customer, Lines = new[] { Line(product, quantity) },
        Payments = new[] { new { Method = "OnAccount", Amount = total } }, ExpectedTotal = total, SupervisorApprovals = approvals,
    };

    private static Task<HttpResponseMessage> PayAsync(Cashier cashier, Guid customer, decimal amount, string method = "Cash", Guid? paymentId = null) =>
        cashier.Client.PostAsJsonAsync($"/api/customers/{customer}/payments",
            new { CustomerPaymentId = paymentId ?? Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Amount = amount, Method = method, Reference = method == "Cash" ? null : "AUT-1" });

    private async Task<decimal> DebtAsync(Guid customer) =>
        (await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/customers/{customer}")).GetProperty("debt").GetDecimal();

    private async Task<decimal> CreditBalanceAsync(Guid customer) =>
        (await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/customers/{customer}")).GetProperty("creditBalance").GetDecimal();

    [Fact]
    public async Task On_account_respects_the_limit_and_above_it_asks_for_a_supervisor()
    {
        var cashier = await app.CashierWithShiftAsync();
        var customer = await CustomerAsync(limit: 1000m);
        var rice = await ProductAsync();

        (await cashier.SellAsync(OnAccountSale(cashier.ShiftId, customer, rice, 6, 600m))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await DebtAsync(customer)).ShouldBe(600m);

        var saleId = Guid.CreateVersion7();
        object Second(params Guid[] approvals) => new
        {
            SaleId = saleId, ShiftId = cashier.ShiftId, CustomerId = customer, Lines = new[] { Line(rice, 5) },
            Payments = new[] { new { Method = "OnAccount", Amount = 500m } }, ExpectedTotal = 500m, SupervisorApprovals = approvals,
        };
        (await (await cashier.SellAsync(Second())).ErrorCode()).ShouldBe("Approval.Required");
        var r = await cashier.SellSupervisedAsync(saleId, Second);
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await DebtAsync(customer)).ShouldBe(1100m);
    }

    [Fact]
    public async Task A_payment_goes_into_the_register_pays_the_oldest_debts_and_cannot_exceed_the_debt()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        var customer = await CustomerAsync(limit: 5000m);
        var rice = await ProductAsync();
        await cashier.SellAsync(OnAccountSale(cashier.ShiftId, customer, rice, 3, 300m));
        await cashier.SellAsync(OnAccountSale(cashier.ShiftId, customer, rice, 2, 200m));

        var payment = await PayAsync(cashier, customer, 350m);
        payment.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await payment.Json()).GetProperty("remainingDebt").GetDecimal().ShouldBe(150m);
        (await (await PayAsync(cashier, customer, 151m)).ErrorCode()).ShouldBe("Customers.PaymentExceedsDebt");

        var x = await app.XReportAsync(cashier.ShiftId);
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(350m); // on-account sales bring no cash; the payment does
        x.GetProperty("customerPayments").GetDecimal().ShouldBe(350m);

        var statement = await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/customers/{customer}/statement");
        statement.GetProperty("debt").GetDecimal().ShouldBe(150m);
        var receivables = await (await Admin()).GetFromJsonAsync<JsonElement>("/api/reports/receivables");
        receivables.EnumerateArray().Single(i => i.GetProperty("customerId").GetGuid() == customer).GetProperty("days0To30").GetDecimal().ShouldBe(150m);
        var report = await (await Admin()).GetFromJsonAsync<JsonElement>("/api/reports/receivables");
        report.EnumerateArray().Single(i => i.GetProperty("customerId").GetGuid() == customer).GetProperty("total").GetDecimal().ShouldBe(150m);
    }

    [Fact]
    public async Task Voiding_an_on_account_sale_removes_the_debt()
    {
        var cashier = await app.CashierWithShiftAsync();
        var customer = await CustomerAsync(limit: 5000m);
        var rice = await ProductAsync();
        var sale = await (await cashier.SellAsync(OnAccountSale(cashier.ShiftId, customer, rice, 4, 400m))).Json();
        var saleId = sale.GetProperty("saleId").GetGuid();

        (await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/void",
            new { Reason = "equivocación", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", saleId) })).EnsureSuccessStatusCode();
        (await DebtAsync(customer)).ShouldBe(0m);
    }

    [Fact]
    public async Task A_return_to_the_account_lowers_that_sales_debt_and_the_rest_stays_in_favor()
    {
        var cashier = await app.CashierWithShiftAsync();
        var customer = await CustomerAsync(limit: 5000m);
        var rice = await ProductAsync();
        var sale = await (await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, CustomerId = customer, Lines = new[] { Line(rice, 5) }, ExpectedTotal = 500m,
            Payments = new object[] { new { Method = "Cash", Amount = 300m }, new { Method = "OnAccount", Amount = 200m } },
        })).Json();
        var saleId = sale.GetProperty("saleId").GetGuid();
        var line = (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}")).GetProperty("lines")[0].GetProperty("id").GetGuid();

        var returnId = Guid.CreateVersion7();
        var ret = await cashier.Client.SupervisedAsync(saleId, approval => cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", new
        {
            ReturnId = returnId, ShiftId = cashier.ShiftId, Method = "CreditBalance", Reason = "sobró",
            Lines = new[] { new { SaleLineId = line, Quantity = 3m } }, SupervisorApprovalId = approval,
        }));
        ret.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DebtAsync(customer)).ShouldBe(0m);            // 300 returned: 200 pays the debt of that sale
        (await CreditBalanceAsync(customer)).ShouldBe(100m); // the other 100 stays in favor

        // The balance in favor pays the next sale; it cannot be overdrawn.
        (await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, CustomerId = customer, Lines = new[] { Line(rice, 2) }, ExpectedTotal = 200m,
            Payments = new object[] { new { Method = "CreditBalance", Amount = 100m }, new { Method = "Cash", Amount = 100m } },
        })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CreditBalanceAsync(customer)).ShouldBe(0m);
        var noBalance = await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, CustomerId = customer, Lines = new[] { Line(rice, 1) }, ExpectedTotal = 100m,
            Payments = new[] { new { Method = "CreditBalance", Amount = 100m } },
        });
        (await noBalance.ErrorCode()).ShouldBe("Customers.InsufficientCreditBalance");
    }

    [Fact]
    public async Task A_wholesale_customer_buys_at_the_wholesale_price_without_a_supervisor()
    {
        var cashier = await app.CashierWithShiftAsync();
        var customer = await CustomerAsync(priceLevel: "Wholesale");
        var rice = await ProductAsync(price: 100m, wholesale: 85m);
        var r = await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, CustomerId = customer, PriceLevel = "Wholesale", Lines = new[] { Line(rice, 10) },
            Payments = new[] { new { Method = "Cash", Amount = 850m } }, ExpectedTotal = 850m,
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);

        var other = await CustomerAsync();
        (await (await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, CustomerId = other, PriceLevel = "Wholesale", Lines = new[] { Line(rice, 10) },
            Payments = new[] { new { Method = "Cash", Amount = 850m } }, ExpectedTotal = 850m,
        })).ErrorCode()).ShouldBe("Approval.Required");
    }

    [Fact]
    public async Task The_fiscal_credit_receipt_takes_the_customers_RNC()
    {
        var cashier = await app.CashierWithShiftAsync();
        var customer = await CustomerAsync(taxId: "130" + Random.Shared.Next(100000, 999999));
        var rice = await ProductAsync();
        var r = await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, CustomerId = customer, DocumentType = "FiscalCredit31", Lines = new[] { Line(rice, 1) },
            Payments = new[] { new { Method = "Cash", Amount = 100m } }, ExpectedTotal = 100m,
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await r.Json()).GetProperty("receipt").GetProperty("customerRnc").GetString()!.Length.ShouldBe(9);
    }

    [Fact]
    public async Task The_cashier_creates_customers_but_cannot_give_them_credit_or_reactivate_blocked_ones()
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.PostAsJsonAsync("/api/customers", new { Name = "Cliente rápido" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await cashier.PostAsJsonAsync("/api/customers", new { Name = "Con crédito", CreditLimit = 5000m })).ErrorCode()).ShouldBe("Customers.CreditPermissionRequired");

        // The owner blocks a customer who does not pay; a cashier (R11 A9: customers.quick-add, not customers.manage) edits no customer at all,
        // so cannot unblock them, nor raise the limit or set wholesale.
        var defaulter = await CustomerAsync(limit: 1000m);
        (await (await Admin()).PutAsJsonAsync($"/api/customers/{defaulter}", new { Name = "Moroso", CreditLimit = 1000m, IsActive = false })).EnsureSuccessStatusCode();
        (await cashier.PutAsJsonAsync($"/api/customers/{defaulter}", new { Name = "Moroso", CreditLimit = 1000m, IsActive = true })).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.PutAsJsonAsync($"/api/customers/{defaulter}", new { Name = "Moroso", CreditLimit = 1000m, PriceLevel = "Wholesale", IsActive = false })).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        // Blocked customers only show up for whoever can reactivate them.
        (await cashier.GetFromJsonAsync<JsonElement>("/api/customers?search=Moroso&includeInactive=true")).EnumerateArray().ShouldNotContain(c => c.GetProperty("id").GetGuid() == defaulter);
        (await (await Admin()).GetFromJsonAsync<JsonElement>("/api/customers?search=Moroso&includeInactive=true")).EnumerateArray().ShouldContain(c => c.GetProperty("id").GetGuid() == defaulter);
        (await cashier.GetAsync("/api/reports/receivables")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Repeating_the_same_payment_does_not_charge_it_twice()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        var customer = await CustomerAsync(limit: 5000m);
        await cashier.SellAsync(OnAccountSale(cashier.ShiftId, customer, await ProductAsync(), 10, 1000m));
        var id = Guid.CreateVersion7();

        var responses = await Task.WhenAll(PayAsync(cashier, customer, 500m, paymentId: id), PayAsync(cashier, customer, 500m, paymentId: id));
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        (await DebtAsync(customer)).ShouldBe(500m);
        (await app.XReportAsync(cashier.ShiftId)).GetProperty("expectedCash").GetDecimal().ShouldBe(500m);
    }

    [Fact]
    public async Task A_cash_payment_is_in_whole_pesos_and_the_cents_become_rounding()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        var customer = await CustomerAsync(limit: 5000m);
        await cashier.SellAsync(OnAccountSale(cashier.ShiftId, customer, await ProductAsync(388.58m), 1, 388.58m));

        (await PayAsync(cashier, customer, 388.58m)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // no coins under RD$ 1
        (await (await PayAsync(cashier, customer, 390m)).ErrorCode()).ShouldBe("Customers.PaymentExceedsDebt");
        var r = await PayAsync(cashier, customer, 389m);
        (await r.Json()).GetProperty("applied").GetDecimal().ShouldBe(388.58m);
        (await DebtAsync(customer)).ShouldBe(0m);
        var x = await app.XReportAsync(cashier.ShiftId);
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(389m);
        x.GetProperty("rounding").GetDecimal().ShouldBe(0.42m);
    }

    [Fact]
    public async Task Voiding_a_partly_paid_on_account_sale_pays_other_debts_and_the_statement_balances()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        var rice = await ProductAsync();
        async Task<Guid> SellOnAccount(Guid customer, int quantity) =>
            (await (await cashier.SellAsync(OnAccountSale(cashier.ShiftId, customer, rice, quantity, quantity * 100m))).Json()).GetProperty("saleId").GetGuid();
        async Task VoidSale(Guid sale) => (await cashier.Client.PostAsJsonAsync($"/api/sales/{sale}/void",
            new { Reason = "error", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", sale) })).EnsureSuccessStatusCode();
        async Task<JsonElement> Statement(Guid customer) => await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/customers/{customer}/statement");

        // Only that sale: the 400 paid becomes balance in favor; the statement ends at 0 (not −400).
        var only = await CustomerAsync(limit: 5000m);
        var sale = await SellOnAccount(only, 10);
        await PayAsync(cashier, only, 400m);
        await VoidSale(sale);
        var statement = await Statement(only);
        statement.GetProperty("debt").GetDecimal().ShouldBe(0m);
        statement.GetProperty("lines").EnumerateArray().Last().GetProperty("debt").GetDecimal().ShouldBe(0m);
        statement.GetProperty("creditBalance").GetDecimal().ShouldBe(400m);

        // With an older debt of 2,000: the 400 pays it down instead of sitting in favor.
        var other = await CustomerAsync(limit: 5000m);
        await SellOnAccount(other, 20);
        var second = await SellOnAccount(other, 10);
        await PayAsync(cashier, other, 2400m); // pays the 2,000 and 400 of the second sale
        await VoidSale(second);
        (await DebtAsync(other)).ShouldBe(0m);
        (await CreditBalanceAsync(other)).ShouldBe(400m);

        // The voided sale is the oldest, so the payment went to it; voiding it moves that 400 to the newer 2,000.
        var third = await CustomerAsync(limit: 5000m);
        var oldest = await SellOnAccount(third, 10);
        await SellOnAccount(third, 20);
        var payment = await (await PayAsync(cashier, third, 400m)).Json();
        payment.GetProperty("remainingDebt").GetDecimal().ShouldBe(2600m);
        await VoidSale(oldest);
        statement = await Statement(third);
        statement.GetProperty("debt").GetDecimal().ShouldBe(1600m);
        statement.GetProperty("lines").EnumerateArray().Last().GetProperty("debt").GetDecimal().ShouldBe(1600m);
        statement.GetProperty("creditBalance").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    public async Task A_return_to_the_account_pays_the_oldest_debts_first()
    {
        var cashier = await app.CashierWithShiftAsync();
        var customer = await CustomerAsync(limit: 5000m);
        var rice = await ProductAsync();
        await cashier.SellAsync(OnAccountSale(cashier.ShiftId, customer, rice, 20, 2000m));
        var cashSale = (await (await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, CustomerId = customer, Lines = new[] { Line(rice, 5) }, ExpectedTotal = 500m,
            Payments = new[] { new { Method = "Cash", Amount = 500m } },
        })).Json()).GetProperty("saleId").GetGuid();
        var line = (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{cashSale}")).GetProperty("lines")[0].GetProperty("id").GetGuid();

        var returnId = Guid.CreateVersion7();
        (await cashier.Client.SupervisedAsync(cashSale, approval => cashier.Client.PostAsJsonAsync($"/api/sales/{cashSale}/returns", new
        {
            ReturnId = returnId, ShiftId = cashier.ShiftId, Method = "CreditBalance", Reason = "sobró",
            Lines = new[] { new { SaleLineId = line, Quantity = 5m } }, SupervisorApprovalId = approval,
        }))).EnsureSuccessStatusCode();
        (await DebtAsync(customer)).ShouldBe(1500m);
        (await CreditBalanceAsync(customer)).ShouldBe(0m);
    }

    [Fact]
    public async Task A_mistyped_payment_is_voided_with_a_supervisor_and_leaves_the_register()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 0m);
        var customer = await CustomerAsync(limit: 10000m);
        await cashier.SellAsync(OnAccountSale(cashier.ShiftId, customer, await ProductAsync(), 60, 6000m));
        var payment = (await (await PayAsync(cashier, customer, 5000m, "Card")).Json()).GetProperty("customerPaymentId").GetGuid();
        (await app.XReportAsync(cashier.ShiftId)).GetProperty("byMethod").EnumerateArray()
            .Single(m => m.GetProperty("method").GetString() == "Card").GetProperty("customerPayments").GetDecimal().ShouldBe(5000m);

        (await (await cashier.Client.PostAsJsonAsync($"/api/customers/payments/{payment}/void", new { Reason = "era 500" })).ErrorCode()).ShouldBe("Approval.Required");
        (await cashier.Client.PostAsJsonAsync($"/api/customers/payments/{payment}/void",
            new { Reason = "era 500", SupervisorApprovalId = await cashier.Client.ApproveAsync("credit.manage", payment) })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await DebtAsync(customer)).ShouldBe(6000m);
        // TK-174: the cloud learns the debt went back up from the customer's balance event.
        var balance = await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox
            .Where(m => m.AggregateId == customer && m.Type.EndsWith("CustomerBalanceChangedV1")).OrderByDescending(m => m.Sequence).Select(m => m.Payload).FirstAsync());
        JsonDocument.Parse(balance).RootElement.GetProperty("debt").GetDecimal().ShouldBe(6000m);
        var x = await app.XReportAsync(cashier.ShiftId);
        x.GetProperty("customerPayments").GetDecimal().ShouldBe(0m);
        (await (await cashier.Client.PostAsJsonAsync($"/api/customers/payments/{payment}/void",
            new { Reason = "otra vez", SupervisorApprovalId = await cashier.Client.ApproveAsync("credit.manage", payment) })).ErrorCode()).ShouldBe("Customers.PaymentAlreadyVoided");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }
}
