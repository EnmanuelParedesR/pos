using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Purchasing;

public sealed class PurchasingTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    /// <summary>Expenses and payments are dated in a month whose 606 is still open (R11 A19), so they use today, not a fixed date.</summary>
    private DateOnly Today => DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
    private string Day => Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string NewNcf() => "B01" + Random.Shared.Next(10_000_000, 99_999_999);

    private static async Task<Guid> SupplierAsync(HttpClient c, int days = 30) =>
        (await (await c.PostAsJsonAsync("/api/purchasing/suppliers", new { Name = "Granja " + Guid.NewGuid().ToString()[..4], Rnc = "101234567", CreditDays = days })).Json())
        .GetProperty("id").GetGuid();

    private static async Task<Guid> PurchaseAsync(HttpClient c, Guid supplier, string terms, DateOnly date, params object[] lines)
    {
        var r = await c.PostAsJsonAsync("/api/purchasing/purchases", new { SupplierId = supplier, Ncf = NewNcf(), InvoiceDate = date, Terms = terms, Lines = lines });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await r.Json()).GetProperty("id").GetGuid();
        (await c.PostAsync($"/api/purchasing/purchases/{id}/post", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return id;
    }

    [Fact]
    public async Task Posting_a_purchase_adds_stock_and_recalculates_the_cost()
    {
        var c = await Admin();
        var product = await c.ProductAsync(await c.CategoryAsync(), "Pierna de cerdo", 150m, unit: "Pound", cost: 80m, stock: 20);
        var supplier = await SupplierAsync(c);

        var draft = await (await c.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            SupplierId = supplier, Ncf = NewNcf(), InvoiceDate = "2026-10-15", Terms = "Cash",
            Lines = new[] { new { ProductId = product, Quantity = 60m, UnitCost = 100m, ItbisRate = "Exempt" } },
        })).Json();
        draft.GetProperty("total").GetDecimal().ShouldBe(6000m);
        var id = draft.GetProperty("id").GetGuid();
        (await c.StockAsync(product)).ShouldBe(20m); // a draft moves nothing

        (await c.PostAsync($"/api/purchasing/purchases/{id}/post", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await c.PostAsync($"/api/purchasing/purchases/{id}/post", null)).ErrorCode()).ShouldBe("Purchasing.NotDraft");

        (await c.StockAsync(product)).ShouldBe(80m);
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{product}")).GetProperty("cost").GetDecimal().ShouldBe(95m); // (20×80 + 60×100)/80
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Buying_by_the_case_adds_units_and_the_cost_per_unit()
    {
        var c = await Admin();
        var product = await c.ProductAsync(await c.CategoryAsync("Bebidas"), "Agua 500ml", 25m, cost: 0m);
        var pack = (await (await c.PostAsJsonAsync($"/api/inventory/products/{product}/presentations", new { Name = "Fardo 24", Factor = 24m, Price = 500m })).Json()).GetProperty("id").GetGuid();
        await PurchaseAsync(c, await SupplierAsync(c), "Cash", new DateOnly(2026, 10, 15),
            new { ProductId = product, PresentationId = pack, Quantity = 2m, UnitCost = 312m });

        (await c.StockAsync(product)).ShouldBe(48m);
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{product}")).GetProperty("cost").GetDecimal().ShouldBe(13m);
    }

    [Fact]
    public async Task The_same_NCF_from_the_same_supplier_does_not_go_in_twice()
    {
        var c = await Admin();
        var product = await c.ProductAsync(await c.CategoryAsync(), "Sal", 20m);
        var supplier = await SupplierAsync(c);
        var ncf = NewNcf();
        object Body() => new { SupplierId = supplier, Ncf = ncf, InvoiceDate = "2026-10-15", Terms = "Cash", Lines = new[] { new { ProductId = product, Quantity = 1m, UnitCost = 10m } } };
        (await c.PostAsJsonAsync("/api/purchasing/purchases", Body())).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases", Body())).ErrorCode()).ShouldBe("Purchasing.DuplicateNcf");
    }

    [Fact]
    public async Task Payments_go_to_the_oldest_invoices_and_cannot_exceed_the_debt()
    {
        var c = await Admin();
        var product = await c.ProductAsync(await c.CategoryAsync(), "Pollo entero", 95m, unit: "Pound");
        var supplier = await SupplierAsync(c, days: 15);
        var oldest = await PurchaseAsync(c, supplier, "Credit", new DateOnly(2026, 9, 1), new { ProductId = product, Quantity = 100m, UnitCost = 60m, ItbisRate = "Exempt" });
        await PurchaseAsync(c, supplier, "Credit", new DateOnly(2026, 10, 1), new { ProductId = product, Quantity = 50m, UnitCost = 60m, ItbisRate = "Exempt" });

        var tooMuch = await c.PostAsJsonAsync("/api/purchasing/payments", new { SupplierId = supplier, Amount = 9001m, Method = "Transfer", Date = Day });
        (await tooMuch.ErrorCode()).ShouldBe("Purchasing.PaymentExceedsDebt");

        var payment = await (await c.PostAsJsonAsync("/api/purchasing/payments", new { SupplierId = supplier, Amount = 7000m, Method = "Transfer", Date = Day, Reference = "TRF-1" })).Json();
        var allocations = payment.GetProperty("allocations");
        allocations[0].GetProperty("amount").GetDecimal().ShouldBe(6000m); // the September invoice first, in full
        allocations[1].GetProperty("amount").GetDecimal().ShouldBe(1000m);

        var statement = await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/suppliers/{supplier}/statement");
        statement.GetProperty("balance").GetDecimal().ShouldBe(2000m);
        var aging = await c.GetFromJsonAsync<JsonElement>("/api/reports/payables");
        aging.EnumerateArray().Single(i => i.GetProperty("supplierId").GetGuid() == supplier).GetProperty("total").GetDecimal().ShouldBe(2000m);

        // A purchase with payments cannot be voided.
        (await (await c.PostAsJsonAsync($"/api/purchasing/purchases/{oldest}/void", new { Reason = "error" })).ErrorCode()).ShouldBe("Purchasing.HasPayments");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Voiding_a_purchase_removes_the_stock_but_not_below_zero()
    {
        var c = await Admin();
        var product = await c.ProductAsync(await c.CategoryAsync(), "Longaniza", 200m, unit: "Pound", sellWithoutStock: true);
        var supplier = await SupplierAsync(c);
        var purchase = await PurchaseAsync(c, supplier, "Cash", new DateOnly(2026, 10, 15), new { ProductId = product, Quantity = 10m, UnitCost = 120m });
        (await app.CommandAsync(new MoveStock(product, 4m, Inbound: false))).IsSuccess.ShouldBeTrue();

        (await (await c.PostAsJsonAsync($"/api/purchasing/purchases/{purchase}/void", new { Reason = "llegó mal" })).ErrorCode()).ShouldBe("Inventory.OutOfStock");
        (await c.StockAsync(product)).ShouldBe(6m);

        var another = await PurchaseAsync(c, supplier, "Cash", new DateOnly(2026, 10, 15), new { ProductId = product, Quantity = 5m, UnitCost = 120m });
        (await c.PostAsJsonAsync($"/api/purchasing/purchases/{another}/void", new { Reason = "duplicada" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.StockAsync(product)).ShouldBe(6m);
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_expense_on_credit_is_an_account_payable()
    {
        var c = await Admin();
        var supplier = await SupplierAsync(c);
        var r = await c.PostAsJsonAsync("/api/purchasing/expenses", new
        {
            SupplierId = supplier, Ncf = NewNcf(), Date = Day, Description = "Energía eléctrica", Type = "WorkSuppliesAndServices",
            Subtotal = 4000m, Itbis = 720m, Terms = "Credit",
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/suppliers/{supplier}/statement")).GetProperty("balance").GetDecimal().ShouldBe(4720m);

        var bad = await c.PostAsJsonAsync("/api/purchasing/expenses", new
        {
            SupplierId = supplier, Ncf = NewNcf(), Date = Day, Description = "x", Type = "FinancialExpenses", Subtotal = 100m, Itbis = 50m, Terms = "Cash",
        });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Voiding_a_purchase_with_a_typo_restores_the_cost_and_two_voids_remove_once()
    {
        var c = await Admin();
        var chop = await c.ProductAsync(await c.CategoryAsync(), "Chuleta " + Guid.NewGuid().ToString()[..4], 165m, unit: "Pound", rate: "Exempt", cost: 100m, stock: 50);
        // 1,000 typed instead of 100: the average jumps to (50×100 + 100×1,000) / 150 = 700.
        var purchase = await PurchaseAsync(c, await SupplierAsync(c), "Cash", new DateOnly(2026, 10, 15),
            new { ProductId = chop, Quantity = 100m, UnitCost = 1000m, ItbisRate = "Exempt" });
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{chop}")).GetProperty("cost").GetDecimal().ShouldBe(700m);

        var responses = await Task.WhenAll(
            c.PostAsJsonAsync($"/api/purchasing/purchases/{purchase}/void", new { Reason = "costo mal escrito" }),
            c.PostAsJsonAsync($"/api/purchasing/purchases/{purchase}/void", new { Reason = "costo mal escrito" }));
        responses.Count(r => r.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);
        (await responses.Single(r => r.StatusCode != HttpStatusCode.NoContent).ErrorCode()).ShouldBe("Purchasing.AlreadyVoided");

        (await c.StockAsync(chop)).ShouldBe(50m);
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{chop}")).GetProperty("cost").GetDecimal().ShouldBe(100m);
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Invoices_are_not_duplicated_and_a_retry_returns_the_same_draft()
    {
        var c = await Admin();
        var salt = await c.ProductAsync(await c.CategoryAsync(), "Sal " + Guid.NewGuid().ToString()[..4], 20m);
        var supplier = await SupplierAsync(c);
        var ncf = NewNcf();
        var id = Guid.CreateVersion7();
        object Purchase(Guid? key) => new
        {
            Id = key, SupplierId = supplier, Ncf = ncf, InvoiceDate = "2026-10-15", Terms = "Cash",
            Lines = new[] { new { ProductId = salt, Quantity = 1m, UnitCost = 10m } },
        };
        var first = await (await c.PostAsJsonAsync("/api/purchasing/purchases", Purchase(id))).Json();
        var second = await c.PostAsJsonAsync("/api/purchasing/purchases", Purchase(id));
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await second.Json()).GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());

        // The same NCF cannot come back as an expense either.
        var expense = await c.PostAsJsonAsync("/api/purchasing/expenses", new
        {
            SupplierId = supplier, Ncf = ncf, Date = Day, Description = "x", Type = "FinancialExpenses", Subtotal = 100m, Itbis = 0m, Terms = "Cash",
        });
        (await expense.ErrorCode()).ShouldBe("Purchasing.DuplicateNcf");
    }

    [Fact]
    public async Task The_invoice_ITBIS_wins_if_it_is_within_one_peso()
    {
        var c = await Admin();
        var cat = await c.CategoryAsync();
        var (a, b) = (await c.ProductAsync(cat, "Aceite " + Guid.NewGuid().ToString()[..4], 300m), await c.ProductAsync(cat, "Harina " + Guid.NewGuid().ToString()[..4], 60m));
        var supplier = await SupplierAsync(c);
        // Line math: 3 × 33.33 = 99.99 → ITBIS 18.00; 1 × 10.55 → 1.90. Total 130.44. The invoice says ITBIS 19.89, total 130.43.
        object Purchase(decimal itbis, decimal total) => new
        {
            SupplierId = supplier, Ncf = NewNcf(), InvoiceDate = "2026-10-15", Terms = "Credit", InvoiceItbis = itbis, InvoiceTotal = total,
            Lines = new object[] { new { ProductId = a, Quantity = 3m, UnitCost = 33.33m }, new { ProductId = b, Quantity = 1m, UnitCost = 10.55m } },
        };
        var r = await (await c.PostAsJsonAsync("/api/purchasing/purchases", Purchase(19.89m, 130.43m))).Json();
        (r.GetProperty("itbis").GetDecimal(), r.GetProperty("total").GetDecimal()).ShouldBe((19.89m, 130.43m));
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases", Purchase(25m, 136m))).ErrorCode()).ShouldBe("Purchasing.InvoiceTotalMismatch");
    }

    [Fact]
    public async Task Mistyped_expenses_and_payments_are_voided()
    {
        var c = await Admin();
        var supplier = await SupplierAsync(c);
        var ncf = NewNcf();
        object Expense(decimal subtotal) => new
        {
            SupplierId = supplier, Ncf = ncf, Date = Day, Description = "Alquiler", Type = "Leases", Subtotal = subtotal, Itbis = 0m, Terms = "Credit",
        };
        var bad = (await (await c.PostAsJsonAsync("/api/purchasing/expenses", Expense(40000m))).Json()).GetProperty("id").GetGuid();
        (await c.PostAsJsonAsync($"/api/purchasing/expenses/{bad}/void", new { Reason = "era 4,000" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.PostAsJsonAsync("/api/purchasing/expenses", Expense(4000m))).StatusCode.ShouldBe(HttpStatusCode.Created); // the NCF is free again

        async Task<decimal> Balance() => (await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/suppliers/{supplier}/statement")).GetProperty("balance").GetDecimal();
        (await Balance()).ShouldBe(4000m);
        var payment = (await (await c.PostAsJsonAsync("/api/purchasing/payments", new { SupplierId = supplier, Amount = 4000m, Method = "Transfer", Date = Day })).Json())
            .GetProperty("supplierPaymentId").GetGuid();
        (await Balance()).ShouldBe(0m);
        (await c.PostAsJsonAsync($"/api/purchasing/payments/{payment}/void", new { Reason = "era otro suplidor" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Balance()).ShouldBe(4000m);
        (await (await c.PostAsJsonAsync($"/api/purchasing/payments/{payment}/void", new { Reason = "otra vez" })).ErrorCode()).ShouldBe("Purchasing.AlreadyVoided");
    }

    [Fact]
    public async Task Paying_the_supplier_with_register_cash_comes_out_of_the_shift()
    {
        var c = await Admin();
        var cashier = await app.CashierWithShiftAsync(openingFloat: 10000m);
        var supplier = await SupplierAsync(c);
        (await c.PostAsJsonAsync("/api/purchasing/expenses", new
        {
            SupplierId = supplier, Ncf = NewNcf(), Date = Day, Description = "Transporte", Type = "WorkSuppliesAndServices", Subtotal = 5000m, Itbis = 0m, Terms = "Credit",
        })).EnsureSuccessStatusCode();

        var paymentId = Guid.CreateVersion7();
        object Payment() => new { SupplierPaymentId = paymentId, SupplierId = supplier, Amount = 5000m, Method = "Cash", Date = Day, ShiftId = cashier.ShiftId };
        (await c.PostAsJsonAsync("/api/purchasing/payments", Payment())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.PostAsJsonAsync("/api/purchasing/payments", Payment())).StatusCode.ShouldBe(HttpStatusCode.OK); // a retry pays once
        (await app.XReportAsync(cashier.ShiftId)).GetProperty("expectedCash").GetDecimal().ShouldBe(5000m);

        (await c.PostAsJsonAsync($"/api/purchasing/payments/{paymentId}/void", new { Reason = "no era a este" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.XReportAsync(cashier.ShiftId)).GetProperty("expectedCash").GetDecimal().ShouldBe(10000m);
    }

    [Fact]
    public async Task Future_dates_and_suppliers_without_RNC_are_rejected_and_aging_uses_the_due_date()
    {
        var c = await Admin();
        var withoutRnc = (await (await c.PostAsJsonAsync("/api/purchasing/suppliers", new { Name = "Sin RNC " + Guid.NewGuid().ToString()[..4], CreditDays = 0 })).Json())
            .GetProperty("id").GetGuid();
        object Expense(Guid supplier, DateOnly date, int days) => new
        {
            SupplierId = supplier, Ncf = NewNcf(), Date = date, Description = "Luz", Type = "WorkSuppliesAndServices", Subtotal = 100m, Itbis = 0m,
            Terms = "Credit", CreditDays = days,
        };
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        (await (await c.PostAsJsonAsync("/api/purchasing/expenses", Expense(withoutRnc, today, 0))).ErrorCode()).ShouldBe("Purchasing.SupplierWithoutRnc");

        var supplier = await SupplierAsync(c);
        (await c.PostAsJsonAsync("/api/purchasing/expenses", Expense(supplier, today.AddYears(1), 0))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await c.PostAsJsonAsync("/api/purchasing/expenses", Expense(supplier, today, 0))).EnsureSuccessStatusCode();                // due today: not overdue
        (await c.PostAsJsonAsync("/api/purchasing/expenses", Expense(supplier, today.AddDays(-1), 0))).EnsureSuccessStatusCode();    // due yesterday: 1–30
        var row = (await c.GetFromJsonAsync<JsonElement>("/api/reports/payables")).EnumerateArray().Single(i => i.GetProperty("supplierId").GetGuid() == supplier);
        (row.GetProperty("notYetDue").GetDecimal(), row.GetProperty("overdue1To30").GetDecimal()).ShouldBe((100m, 100m));
    }

    [Fact]
    public async Task The_606_has_posted_purchases_and_expenses_and_payables_export()
    {
        var c = await Admin();
        var supplier = await SupplierAsync(c);
        var beef = await c.ProductAsync(await c.CategoryAsync(), "Res " + Guid.NewGuid().ToString()[..4], 250m, unit: "Pound", rate: "Exempt");
        var purchase = await PurchaseAsync(c, supplier, "Credit", Today, new { ProductId = beef, Quantity = 10m, UnitCost = 180m });
        var purchaseNcf = (await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/purchases/{purchase}")).GetProperty("ncf").GetString();
        var expenseNcf = NewNcf();
        (await c.PostAsJsonAsync("/api/purchasing/expenses", new
        {
            SupplierId = supplier, Ncf = expenseNcf, Date = Day, Description = "Hielo", Type = "WorkSuppliesAndServices", Subtotal = 500m, Itbis = 90m, Terms = "Cash",
        })).EnsureSuccessStatusCode();

        var transferNcf = NewNcf();
        (await c.PostAsJsonAsync("/api/purchasing/expenses", new
        {
            SupplierId = supplier, Ncf = transferNcf, Date = Day, Description = "Fumigación", Type = "WorkSuppliesAndServices", Subtotal = 1000m, Itbis = 180m,
            Terms = "Cash", Method = "Transfer",
        })).EnsureSuccessStatusCode();

        var rows = (await c.GetFromJsonAsync<JsonElement>($"/api/reports/dgii/606?from={Day}&to={Day}")).EnumerateArray().ToList();
        // Payment form: transfer → 2 with the invoice date; a credit purchase → 4 with no date; a cash one whose method is unknown → empty.
        var transferred = rows.Single(f => f.GetProperty("ncf").GetString() == transferNcf);
        (transferred.GetProperty("paymentForm").GetInt32(), transferred.GetProperty("paymentDate").GetString()).ShouldBe((2, Day));
        var credit = rows.Single(f => f.GetProperty("ncf").GetString() == purchaseNcf);
        (credit.GetProperty("paymentForm").GetInt32(), credit.GetProperty("paymentDate").ValueKind).ShouldBe((4, JsonValueKind.Null));
        rows.Single(f => f.GetProperty("ncf").GetString() == expenseNcf).GetProperty("paymentForm").ValueKind.ShouldBe(JsonValueKind.Null);
        var fromPurchase = rows.Single(f => f.GetProperty("ncf").GetString() == purchaseNcf);
        (fromPurchase.GetProperty("goodsServicesType").GetInt32(), fromPurchase.GetProperty("goodsAmount").GetDecimal(), fromPurchase.GetProperty("idType").GetInt32()).ShouldBe((9, 1800m, 1));
        rows.Single(f => f.GetProperty("ncf").GetString() == expenseNcf).GetProperty("itbisInvoiced").GetDecimal().ShouldBe(90m);

        var csv = await c.GetStringAsync("/api/reports/payables?format=csv");
        csv.ShouldContain(supplier.ToString());
    }

    [Fact]
    public async Task A_cashier_cannot_record_purchases()
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.GetAsync("/api/purchasing/purchases")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
