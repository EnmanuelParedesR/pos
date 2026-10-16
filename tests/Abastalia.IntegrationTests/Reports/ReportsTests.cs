using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Shouldly;
using Abastalia.IntegrationTests.Infra;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Reports;

/// <summary>A small store day (sales, a void, a return, a closed shift) and the reports that must add up to it. Own app: totals are exact.</summary>
public sealed class ReportsTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>, IAsyncLifetime
{
    // xUnit makes one instance per test but one app per class: the day is sold once.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (Guid PorkChop, Guid Soda)? _day;

    private HttpClient _admin = null!;
    private string _period = "";
    private Guid _porkChop, _soda;

    public async ValueTask InitializeAsync()
    {
        _admin = app.Client(await app.AdminAsync());
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        _period = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        await Gate.WaitAsync();
        try { _day ??= await SellDayAsync(); }
        finally { Gate.Release(); }
        (_porkChop, _soda) = _day.Value;
    }

    private async Task<(Guid, Guid)> SellDayAsync()
    {
        var cat = await _admin.CategoryAsync("Carnes");
        var porkChop = await _admin.ProductAsync(cat, "Chuleta", 165m, unit: "Pound", rate: "Exempt", cost: 110m, stock: 50);
        var soda = await _admin.ProductAsync(cat, "=HYPERLINK(\"x\")", 59m, cost: 35m, stock: 24);
        var cashier = await app.CashierWithShiftAsync(openingFloat: 1000m);

        // A: 2 lb pork chop (330, exempt) + 2 sodas (118, ITBIS 18) = 448.
        (await cashier.SellAsync(Sale(cashier.ShiftId, 448m, 448m, Line(porkChop, 2m), Line(soda, 2m)))).EnsureSuccessStatusCode();
        // B: voided.
        var b = (await (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1m)))).Json()).GetProperty("saleId").GetGuid();
        (await cashier.Client.PostAsJsonAsync($"/api/sales/{b}/void",
            new { Reason = "Error", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", b) })).EnsureSuccessStatusCode();
        // C: 3 sodas (177), one comes back to the shelf.
        var c = (await (await cashier.SellAsync(Sale(cashier.ShiftId, 177m, 177m, Line(soda, 3m)))).Json()).GetProperty("saleId").GetGuid();
        var line = (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{c}")).GetProperty("lines")[0].GetProperty("id").GetGuid();
        (await cashier.Client.SupervisedAsync(c, approval => cashier.Client.PostAsJsonAsync($"/api/sales/{c}/returns", new
        {
            ReturnId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Method = "Cash", Reason = "Sobró",
            Lines = new[] { new { SaleLineId = line, Quantity = 1m } }, SupervisorApprovalId = approval,
        }))).EnsureSuccessStatusCode();

        // Expected cash: 1000 + 448 + 177 − 59 = 1566, counted exactly.
        var cashCount = new[] { 1000m, 500m, 50m, 10m, 5m, 1m }.Select(d => new { Denomination = d, Quantity = 1 });
        (await cashier.Client.PostAsJsonAsync($"/api/cash/shifts/{cashier.ShiftId}/close",
            new { CashCount = cashCount, SupervisorApprovalId = await cashier.Client.ApproveAsync("register.approve-close", cashier.ShiftId) })).EnsureSuccessStatusCode();
        return (porkChop, soda);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<JsonElement> Get(string route) => _admin.GetFromJsonAsync<JsonElement>($"/api/reports/{route}");

    [Fact]
    public async Task Sales_by_day_subtract_voids_and_returns_at_historical_cost()
    {
        var day = (await Get($"sales/by-day?{_period}")).EnumerateArray().Single();
        day.GetProperty("saleCount").GetInt32().ShouldBe(2);
        day.GetProperty("returns").GetDecimal().ShouldBe(59m);
        day.GetProperty("salesWithItbis").GetDecimal().ShouldBe(625m);
        day.GetProperty("netWithoutItbis").GetDecimal().ShouldBe(530m);
        day.GetProperty("itbis").GetDecimal().ShouldBe(36m);
        day.GetProperty("cost").GetDecimal().ShouldBe(360m);
        day.GetProperty("profit").GetDecimal().ShouldBe(170m);
    }

    [Fact]
    public async Task By_product_category_and_cashier()
    {
        var products = (await Get($"sales/by-product?{_period}")).EnumerateArray().ToDictionary(p => p.GetProperty("productId").GetGuid());
        products[_porkChop].GetProperty("profit").GetDecimal().ShouldBe(110m);
        products[_soda].GetProperty("quantity").GetDecimal().ShouldBe(4m);
        products[_soda].GetProperty("netWithoutItbis").GetDecimal().ShouldBe(200m);
        products[_soda].GetProperty("profit").GetDecimal().ShouldBe(60m);

        var category = (await Get($"sales/by-category?{_period}")).EnumerateArray().Single();
        category.GetProperty("category").GetString().ShouldBe("Carnes");
        category.GetProperty("profit").GetDecimal().ShouldBe(170m);

        var cashier = (await Get($"sales/by-cashier?{_period}")).EnumerateArray().Single();
        cashier.GetProperty("saleCount").GetInt32().ShouldBe(2);
        cashier.GetProperty("voidedCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Payment_mix_and_sales_by_hour_come_from_the_sales()
    {
        // TK-179: A (448) and C (177) in cash, B voided; the return gave 59 back in cash.
        var cash = (await Get($"sales/by-payment-method?{_period}")).EnumerateArray().ShouldHaveSingleItem();
        cash.GetProperty("method").GetString().ShouldBe("Cash");
        (cash.GetProperty("saleCount").GetInt32(), cash.GetProperty("paid").GetDecimal(), cash.GetProperty("refunded").GetDecimal(), cash.GetProperty("net").GetDecimal())
            .ShouldBe((2, 625m, 59m, 566m));
        (await _admin.GetStringAsync($"/api/reports/sales/by-payment-method?{_period}&format=csv")).ShouldContain("MedioPago,Ventas,Pagado,Devuelto,Neto");

        var hours = (await Get($"sales/by-hour?{_period}")).EnumerateArray().ToList();
        hours.Sum(h => h.GetProperty("saleCount").GetInt32()).ShouldBe(2);
        hours.Sum(h => h.GetProperty("salesWithItbis").GetDecimal()).ShouldBe(625m);
        var storeHour = app.Clock.GetUtcNow().UtcDateTime.AddHours(-4).Hour;
        hours.ShouldAllBe(h => h.GetProperty("hour").GetInt32() == storeHour);
    }

    [Fact]
    public async Task Shifts_inventory_and_607()
    {
        var shift = (await Get($"shifts?{_period}")).EnumerateArray().Single();
        shift.GetProperty("expectedCash").GetDecimal().ShouldBe(1566m);
        shift.GetProperty("difference").GetDecimal().ShouldBe(0m);

        var value = (await Get("inventory/value")).EnumerateArray().ToDictionary(p => p.GetProperty("productId").GetGuid());
        value[_porkChop].GetProperty("costValue").GetDecimal().ShouldBe(48m * 110m);
        value[_soda].GetProperty("stock").GetDecimal().ShouldBe(20m);

        // Three sales and two credit notes (the void and the return), each pointing to its sale.
        var docs = (await Get($"dgii/607?{_period}")).EnumerateArray().ToList();
        docs.Count.ShouldBe(5);
        docs.Count(d => d.GetProperty("ncf").GetString()!.StartsWith("E34", StringComparison.Ordinal) && d.GetProperty("modifiedNcf").GetString() is not null).ShouldBe(2);
        // Sale A: 448 charged, ITBIS 18 → "Monto facturado" is without ITBIS, and the payment columns add up to what was charged.
        var a = docs.Single(d => d.GetProperty("modifiedNcf").ValueKind == JsonValueKind.Null && d.GetProperty("itbisInvoiced").GetDecimal() == 18m);
        a.GetProperty("invoicedAmount").GetDecimal().ShouldBe(430m);
        (a.GetProperty("cash").GetDecimal() + a.GetProperty("card").GetDecimal() + a.GetProperty("checkTransfer").GetDecimal()).ShouldBe(448m);
        a.GetProperty("incomeType").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task In_CSV_times_are_in_store_time()
    {
        var shift = (await Get($"shifts?{_period}")).EnumerateArray().Single();
        var opened = shift.GetProperty("openedUtc").GetDateTime().ToUniversalTime().AddHours(-4);
        var csv = await _admin.GetStringAsync($"/api/reports/shifts?{_period}&format=csv");
        csv.ShouldContain(",Abierto,");
        csv.ShouldNotContain("OpenedUtc");
        csv.ShouldContain(opened.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Exports_to_CSV_without_formulas_and_to_Excel()
    {
        var csv = await _admin.GetAsync($"/api/reports/sales/by-product?{_period}&format=csv");
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        text.ShouldStartWith("﻿ProductoId,CategoriaId,Producto,");
        text.ShouldContain("\"'=HYPERLINK(\"\"x\"\")\""); // neutralized: opens as text, not as a formula

        var xlsx = await _admin.GetAsync($"/api/reports/inventory/value?format=xlsx");
        xlsx.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var workbook = new XLWorkbook(await xlsx.Content.ReadAsStreamAsync());
        var sheet = workbook.Worksheets.First();
        sheet.Cell(1, 2).GetString().ShouldBe("Producto");
        sheet.RowsUsed().Count().ShouldBe(3); // header + 2 products
    }

    [Fact]
    public async Task Only_who_can_view_reports_and_with_a_valid_period()
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.GetAsync($"/api/reports/sales/by-day?{_period}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await _admin.GetAsync("/api/reports/sales/by-day?from=2026-02-01&to=2026-01-01")).ErrorCode()).ShouldBe("Reports.InvalidPeriod");
        (await (await _admin.GetAsync($"/api/reports/shifts?{_period}&format=pdf")).ErrorCode()).ShouldBe("Reports.InvalidFormat");
    }
}
