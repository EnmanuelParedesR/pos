using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.IntegrationTests.Infra;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>e-NCF ranges: oldest usable range first, expired ranges skipped, no range means nothing is saved. Own app: it loads its own ranges.</summary>
public sealed class SequencesTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task Ranges_are_used_in_order_expired_ones_are_skipped_and_without_a_range_nothing_is_saved()
    {
        await app.SetUpBusinessAsync();
        var admin = app.Client(await app.AdminAsync());
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        async Task LoadRange(int type, long from, long to, DateOnly expiresOn) =>
            (await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = type, From = from, To = to, ExpiresOn = expiresOn })).EnsureSuccessStatusCode();
        await LoadRange(31, 1, 1, new DateOnly(2030, 12, 31));
        await LoadRange(31, 2, 5, new DateOnly(2030, 12, 31));
        await LoadRange(32, 1, 10, today.AddDays(-1));
        await LoadRange(32, 11, 20, new DateOnly(2030, 12, 31));
        // An expired credit-note range: the type was loaded, so Simulated does not load one by itself (TK-363).
        await LoadRange(34, 1, 1, today.AddDays(-1));

        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 10);
        async Task<JsonElement> Sell(string type)
        {
            var id = Guid.CreateVersion7();
            return await (await cashier.SellSupervisedAsync(id, approvals => new
            {
                SaleId = id, ShiftId = cashier.ShiftId, Lines = new[] { Line(soda, 1m) }, Payments = new[] { new { Method = "Cash", Amount = 59m } },
                ExpectedTotal = 59m, DocumentType = type, CustomerRnc = "131245678", CustomerName = "Colmado El Sol SRL", SupervisorApprovals = approvals,
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

        var alerts = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/alerts");
        bool Alert(string type) => alerts.EnumerateArray().Single(a => a.GetProperty("type").GetString() == type).GetProperty("alert").GetBoolean();
        Alert("CreditNote34").ShouldBeTrue(); // nothing usable
        Alert("Consumer32").ShouldBeFalse();

        // A range that expires within 30 days raises the alert; the credit note points to the sale it cancels.
        await LoadRange(34, 2, 100, today.AddDays(20));
        (await VoidSale()).EnsureSuccessStatusCode();
        alerts = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/alerts");
        Alert("CreditNote34").ShouldBeTrue();
        var modified = await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents
            .Where(d => d.ENcf == "E340000000002").Select(d => d.ModifiedENcf).SingleAsync());
        modified.ShouldBe("E320000000011");
    }

    [Fact]
    public async Task The_store_signs_its_own_e_CF_so_the_device_routes_do_not_exist()
    {
        // TK-323: only the Lite host (Fiscal:Signer = Device) leaves documents for a device.
        var admin = app.Client(await app.AdminAsync());
        (await admin.GetAsync("/api/fiscal/device/documents")).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        (await admin.PostAsJsonAsync($"/api/fiscal/device/documents/{Guid.NewGuid()}", new { SignedXml = "<ECF/>" })).StatusCode
            .ShouldBe(System.Net.HttpStatusCode.NotFound);
    }
}

/// <summary>TK-363: a new store in Simulated sells and voids without loading anything; the owner is not asked for ranges it does not need yet.</summary>
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
    }
}
