using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.IntegrationTests.Infra;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>e-NCF ranges in a real environment (CerteCF): oldest usable range first, expired ranges skipped, no range means nothing is saved, and
/// the alerts (TK-376: per-type threshold, critical when none is left). Own app: it loads its own ranges.</summary>
public sealed class SequencesTests(CerteCfAbastaliaApp app) : IClassFixture<CerteCfAbastaliaApp>
{
    [Fact]
    public async Task Ranges_are_used_in_order_expired_ones_are_skipped_and_without_a_range_nothing_is_saved()
    {
        var admin = await app.WithAddressAsync();
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        async Task LoadRange(int type, long from, long to, DateOnly expiresOn) =>
            (await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = type, From = from, To = to, ExpiresOn = expiresOn })).EnsureSuccessStatusCode();
        await LoadRange(31, 1, 1, new DateOnly(2030, 12, 31));
        await LoadRange(31, 2, 5, new DateOnly(2030, 12, 31));
        await LoadRange(32, 1, 10, today.AddDays(-1));
        await LoadRange(32, 11, 20, new DateOnly(2030, 12, 31));
        // An expired credit-note range: CerteCF never loads one by itself (TK-376: only Simulated and TesteCF do).
        await LoadRange(34, 1, 1, today.AddDays(-1));

        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 10);
        async Task<JsonElement> Sell(string type)
        {
            var id = Guid.CreateVersion7();
            return await (await cashier.SellSupervisedAsync(id, approvals => new
            {
                SaleId = id, ShiftId = cashier.ShiftId, Lines = new[] { Line(soda, 1m) }, Payments = new[] { new { Method = "Cash", Amount = 59m } },
                ExpectedTotal = 59m, DocumentType = type, CustomerRnc = "101672919", CustomerName = "Colmado El Sol SRL", SupervisorApprovals = approvals,
            })).Json();
        }

        // The first range runs out and the next one takes over; the expired consumer range is skipped.
        (await Sell("FiscalCredit31")).GetProperty("eNcf").GetString().ShouldBe("E310000000001");
        (await Sell("FiscalCredit31")).GetProperty("eNcf").GetString().ShouldBe("E310000000002");
        var consumer = await Sell("Consumer32");
        consumer.GetProperty("eNcf").GetString().ShouldBe("E320000000011");
        var saleId = consumer.GetProperty("saleId").GetGuid();

        // No usable credit-note range: the void fails whole (sale, stock and cash stay as they were).
        async Task<HttpResponseMessage> VoidSale() => await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/void",
            new { Reason = "Error", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", saleId) });
        (await (await VoidSale()).ErrorCode()).ShouldBe("Fiscal.NoSequence");
        (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}")).GetProperty("status").GetString().ShouldBe("Completed");
        (await admin.StockAsync(soda)).ShouldBe(7m);

        // The register (any signed-in user) reads the alerts.
        async Task<Dictionary<string, (bool Alert, bool Critical, int Percent)>> Alerts() =>
            (await cashier.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/alerts")).EnumerateArray().ToDictionary(a => a.GetProperty("type").GetString()!,
                a => (a.GetProperty("alert").GetBoolean(), a.GetProperty("critical").GetBoolean(), a.GetProperty("thresholdPercent").GetInt32()));
        var alerts = await Alerts();
        alerts["CreditNote34"].ShouldBe((true, true, 20)); // nothing usable: red
        alerts["Consumer32"].ShouldBe((false, false, 20)); // 9 of 10 left
        alerts["FiscalCredit31"].ShouldBe((false, false, 20)); // 3 of 4 left

        // TK-376: the owner raises the consumer threshold to 95 %: 9 of 10 left is under it (amber). A cashier cannot change it.
        (await cashier.Client.PutAsJsonAsync("/api/fiscal/sequences/thresholds/Consumer32", new { Percent = 95 })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.PutAsJsonAsync("/api/fiscal/sequences/thresholds/Consumer32", new { Percent = 0 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync("/api/fiscal/sequences/thresholds/Consumer32", new { Percent = 95 })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Alerts())["Consumer32"].ShouldBe((true, false, 95));
        (await admin.PutAsJsonAsync("/api/fiscal/sequences/thresholds/Consumer32", new { Percent = 50 })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Alerts())["Consumer32"].ShouldBe((false, false, 50));

        // A range that expires within 30 days raises the alert (amber); the credit note points to the sale it cancels.
        await LoadRange(34, 2, 100, today.AddDays(20));
        (await VoidSale()).EnsureSuccessStatusCode();
        (await Alerts())["CreditNote34"].ShouldBe((true, false, 20));
        var modified = await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents
            .Where(d => d.ENcf == "E340000000002").Select(d => d.ModifiedENcf).SingleAsync());
        modified.ShouldBe("E320000000011");

        // The last day is red: the sale still goes out today, tomorrow it stops.
        await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Sequences.Where(s => s.Type == Abastalia.Fiscal.Contracts.EcfType.CreditNote34)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresOn, today)));
        (await Alerts())["CreditNote34"].ShouldBe((true, true, 20));
    }

    [Fact]
    public async Task The_store_signs_its_own_e_CF_so_the_device_routes_do_not_exist()
    {
        // TK-323: only the Lite host (Fiscal:Signer = Device) leaves documents for a device.
        var admin = app.Client(await app.AdminAsync());
        (await admin.GetAsync("/api/fiscal/device/documents")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsJsonAsync($"/api/fiscal/device/documents/{Guid.NewGuid()}", new { SignedXml = "<ECF/>" })).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }
}

/// <summary>TK-363: a new store in Simulated sells and voids without loading anything; the owner is not asked for ranges it does not need yet.
/// TK-376: once a range is used up or expired, the next one loads by itself too, and Simulated never warns.</summary>
public sealed class SelfLoadingSequencesTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task In_simulated_mode_the_first_sale_loads_its_own_range_and_nothing_is_flagged_to_the_owner()
    {
        await app.SetUpBusinessAsync();
        var admin = app.Client(await app.AdminAsync());
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/alerts")).EnumerateArray().ShouldAllBe(a => !a.GetProperty("alert").GetBoolean());

        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 10);
        var sale = await (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1m)))).Json();
        sale.GetProperty("eNcf").GetString().ShouldBe("E320000000001");
        var saleId = sale.GetProperty("saleId").GetGuid();
        (await cashier.Client.PostAsJsonAsync($"/api/sales/{saleId}/void",
            new { Reason = "Error", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", saleId) })).EnsureSuccessStatusCode();
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/alerts")).EnumerateArray().ShouldAllBe(a => !a.GetProperty("alert").GetBoolean());

        // TK-376: the consumer range is used up and a credit-fiscal range expired: no alert, and the next sale of each loads a new range after it.
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        (await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = 31, From = 1, To = 100, ExpiresOn = today.AddDays(-1) })).EnsureSuccessStatusCode();
        await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Sequences.Where(s => s.Type == Abastalia.Fiscal.Contracts.EcfType.Consumer32)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Next, x => x.To + 1)));
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/alerts")).EnumerateArray().ShouldAllBe(a => !a.GetProperty("alert").GetBoolean());

        (await (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1m)))).Json()).GetProperty("eNcf").GetString().ShouldBe("E320050000001");
        var id = Guid.CreateVersion7();
        var credit = await (await cashier.SellSupervisedAsync(id, approvals => new
        {
            SaleId = id, ShiftId = cashier.ShiftId, Lines = new[] { Line(soda, 1m) }, Payments = new[] { new { Method = "Cash", Amount = 59m } },
            ExpectedTotal = 59m, DocumentType = "FiscalCredit31", CustomerRnc = "101672919", CustomerName = "Colmado El Sol SRL", SupervisorApprovals = approvals,
        })).Json();
        credit.GetProperty("eNcf").GetString().ShouldBe("E310000000101");
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/alerts")).EnumerateArray().ShouldAllBe(a => !a.GetProperty("alert").GetBoolean());
    }
}
