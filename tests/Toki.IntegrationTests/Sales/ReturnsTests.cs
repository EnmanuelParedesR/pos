using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Fiscal;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Sales;

/// <summary>Voids and returns: money goes back once and the way it came in (review R3).
/// On SQLite writes run one at a time, so the parallel tests check the final state; on SQL Server (CI) they also race for real.</summary>
public sealed class ReturnsTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<Guid> ProductAsync(string name, decimal price, decimal stock = 100)
    {
        var admin = app.Client(await app.AdminAsync());
        return await admin.ProductAsync(await admin.CategoryAsync(), $"{name} {Guid.NewGuid().ToString()[..4]}", price, stock: stock);
    }

    private static async Task<(Guid SaleId, Guid LineId)> SellAsync(Cashier cashier, object sale)
    {
        var r = await cashier.SellAsync(sale);
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var saleId = (await r.Json()).GetProperty("saleId").GetGuid();
        var detail = await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}");
        return (saleId, detail.GetProperty("lines")[0].GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> ReturnAsync(Cashier cashier, Guid saleId, Guid lineId, decimal quantity, string method = "Cash", Guid? returnId = null) =>
        await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", new
        {
            ReturnId = returnId ?? Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Method = method, Reason = "Devolución",
            Lines = new[] { new { SaleLineId = lineId, Quantity = quantity } },
            SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.return", saleId),
        });

    private static async Task<HttpResponseMessage> VoidAsync(Cashier cashier, Guid saleId) =>
        await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/void",
            new { Reason = "Error", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", saleId) });

    [Fact]
    public async Task A_return_goes_to_the_outbox_as_V2_with_its_lines()
    {
        var cashier = await app.CashierWithShiftAsync();
        var steak = await ProductAsync("Bistec", 250m);
        var (saleId, line) = await SellAsync(cashier, Sale(cashier.ShiftId, 500m, 500m, Line(steak, 2m)));
        var returnId = Guid.CreateVersion7();
        (await ReturnAsync(cashier, saleId, line, 1m, returnId: returnId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var payload = await app.InScopeAsync(sp => sp.GetRequiredService<Toki.BuildingBlocks.Persistence.InfraDbContext>().Outbox
            .Where(m => m.Type == typeof(Toki.Sales.Contracts.ReturnRecordedV2).FullName).Select(m => m.Payload).ToListAsync());
        var e = payload.Select(p => JsonSerializer.Deserialize<Toki.Sales.Contracts.ReturnRecordedV2>(p, Toki.BuildingBlocks.Persistence.Json.Options)!)
            .Single(x => x.ReturnId == returnId);
        e.Total.ShouldBe(250m);
        var l = e.Lines.ShouldHaveSingleItem();
        (l.ProductId, l.Quantity, l.Total, l.Itbis).ShouldBe((steak, 1m, 250m, e.Itbis));
        l.Description.ShouldStartWith("Bistec");
    }

    [Fact]
    public async Task A_card_sale_is_not_refunded_in_cash()
    {
        var cashier = await app.CashierWithShiftAsync();
        var whisky = await ProductAsync("Whisky", 5000m);
        var (saleId, line) = await SellAsync(cashier, new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Lines = new[] { Line(whisky, 1m) }, ExpectedTotal = 5000m,
            Payments = new[] { new { Method = "Card", Amount = 5000m, Reference = "AUT-1" } },
        });

        (await (await ReturnAsync(cashier, saleId, line, 1m, "Cash")).ErrorCode()).ShouldBe("Sales.MethodExceeded");
        (await ReturnAsync(cashier, saleId, line, 1m, "Card")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.XReportAsync(cashier.ShiftId)).GetProperty("expectedCash").GetDecimal().ShouldBe(2000m);
    }

    [Fact]
    public async Task The_last_piece_squares_amount_and_itbis_and_each_note_meets_price_times_quantity_minus_discount()
    {
        var cashier = await app.CashierWithShiftAsync();
        var cheese = await ProductAsync("Queso", 50m);
        // 3 × 50 = 150 with RD$ 50 off: the line is 100.00, which does not split evenly in three.
        var saleId = Guid.CreateVersion7();
        var (_, line) = await SellAsync(cashier, new
        {
            SaleId = saleId, ShiftId = cashier.ShiftId, ExpectedTotal = 100m, Payments = new[] { new { Method = "Cash", Amount = 100m } },
            Lines = new[] { new { ProductId = cheese, Quantity = 3m, Discount = new { IsPercentage = false, Value = 50m } } },
            SupervisorApprovals = new[] { await cashier.Client.ApproveAsync("sales.large-discount", saleId) },
        });

        var returns = new List<JsonElement>();
        for (var i = 0; i < 3; i++) returns.Add(await (await ReturnAsync(cashier, saleId, line, 1m)).Json());
        returns.Select(d => d.GetProperty("total").GetDecimal()).ShouldBe([33.33m, 33.33m, 33.34m]);
        var detail = await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}");
        returns.Sum(d => d.GetProperty("itbis").GetDecimal()).ShouldBe(detail.GetProperty("lines")[0].GetProperty("itbis").GetDecimal());

        var notes = returns.Select(d => d.GetProperty("creditNoteENcf").GetString()!).ToList();
        foreach (var item in await ItemsOfAsync(notes))
        {
            decimal Value(string name) => decimal.Parse(item.Element(name)!.Value, CultureInfo.InvariantCulture);
            Math.Abs(Value("PrecioUnitarioItem") * Value("CantidadItem") - Value("DescuentoMonto") - Value("MontoItem")).ShouldBeLessThanOrEqualTo(0.01m);
        }
    }

    private Task<List<XElement>> ItemsOfAsync(List<string> encfs) => app.InScopeAsync(async sp =>
    {
        var docs = await sp.GetRequiredService<FiscalDbContext>().Documents.Where(d => encfs.Contains(d.ENcf)).ToListAsync();
        docs.Count.ShouldBe(encfs.Count);
        return docs.SelectMany(d =>
        {
            using var gzip = new GZipStream(new MemoryStream(d.SignedXml), CompressionMode.Decompress);
            return XDocument.Load(gzip).Descendants("Item");
        }).ToList();
    });

    [Fact]
    public async Task Repeating_the_same_return_returns_the_same_response()
    {
        var cashier = await app.CashierWithShiftAsync();
        var soda = await ProductAsync("Refresco", 59m);
        var (saleId, line) = await SellAsync(cashier, Sale(cashier.ShiftId, 118m, 118m, Line(soda, 2m)));
        var id = Guid.CreateVersion7();

        var first = await (await ReturnAsync(cashier, saleId, line, 1m, returnId: id)).Json();
        var second = await ReturnAsync(cashier, saleId, line, 1m, returnId: id);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Json()).GetProperty("creditNoteENcf").GetString().ShouldBe(first.GetProperty("creditNoteENcf").GetString());
        (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}")).GetProperty("lines")[0].GetProperty("returnedQuantity").GetDecimal().ShouldBe(1m);
    }

    [Fact]
    public async Task Two_voids_at_once_give_the_money_back_only_once()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var soda = await ProductAsync("Refresco", 59m, stock: 10);
        var (saleId, _) = await SellAsync(cashier, Sale(cashier.ShiftId, 118m, 118m, Line(soda, 2m)));

        var responses = await Task.WhenAll(VoidAsync(cashier, saleId), VoidAsync(cashier, saleId));
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        (await responses.Single(r => r.StatusCode != HttpStatusCode.OK).ErrorCode()).ShouldBe("Sales.AlreadyVoided");
        (await app.XReportAsync(cashier.ShiftId)).GetProperty("expectedCash").GetDecimal().ShouldBe(1000m);
        (await app.Client(await app.AdminAsync()).StockAsync(soda)).ShouldBe(10m);
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_void_and_a_return_at_once_do_not_pay_twice()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var otherRegister = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var soda = await ProductAsync("Refresco", 59m, stock: 10);
        var (saleId, line) = await SellAsync(cashier, Sale(cashier.ShiftId, 177m, 177m, Line(soda, 3m)));

        var responses = await Task.WhenAll(VoidAsync(cashier, saleId), ReturnAsync(otherRegister, saleId, line, 1m));
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var refunded = 1000m * 2 + 177m - (await app.XReportAsync(cashier.ShiftId)).GetProperty("expectedCash").GetDecimal()
                       - (await app.XReportAsync(otherRegister.ShiftId)).GetProperty("expectedCash").GetDecimal();
        refunded.ShouldBeOneOf(177m, 59m);
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_returns_of_the_same_line_at_once_add_up_correctly()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var otherRegister = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var soda = await ProductAsync("Refresco", 59m, stock: 10);
        var (saleId, line) = await SellAsync(cashier, Sale(cashier.ShiftId, 177m, 177m, Line(soda, 3m)));

        var responses = await Task.WhenAll(ReturnAsync(cashier, saleId, line, 1m), ReturnAsync(otherRegister, saleId, line, 1m));
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}")).GetProperty("lines")[0].GetProperty("returnedQuantity").GetDecimal().ShouldBe(2m);
        (await app.Client(await app.AdminAsync()).StockAsync(soda)).ShouldBe(9m);
        // Only one unit is left: a third return of 2 is rejected.
        (await (await ReturnAsync(cashier, saleId, line, 2m)).ErrorCode()).ShouldBe("Sales.ReturnQuantityExceeded");
    }
}
