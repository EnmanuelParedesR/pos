using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>A store that asks the DGII public pages (answered by <see cref="TokiApp.DgiiPublic"/>, recorded HTML).</summary>
public sealed class DgiiPublicApp : TokiApp
{
    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string> { ["Fiscal:DgiiPublic:Enabled"] = "true" };
}

/// <summary>TK-252: RNC status from the DGII consultation with the registry as fallback, and supplier NCFs checked before they are saved.</summary>
public sealed class DgiiPublicLookupTests(DgiiPublicApp app) : IClassFixture<DgiiPublicApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    private static async Task<Guid> SupplierAsync(HttpClient c, string rnc) =>
        (await (await c.PostAsJsonAsync("/api/purchasing/suppliers", new { Name = "Suplidor " + Guid.NewGuid().ToString()[..4], Rnc = rnc, CreditDays = 0 })).Json())
        .GetProperty("id").GetGuid();

    [Fact]
    public async Task The_RNC_comes_with_its_DGII_status_and_without_an_answer_the_registry_is_used()
    {
        var c = await Admin();
        app.DgiiPublic.Rnc("401506254", "rnc-found").Rnc("130000001", "rnc-not-registered");

        var dgii = await c.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayers/401-50625-4");
        dgii.GetProperty("name").GetString().ShouldBe("DIRECCION GENERAL DE IMPUESTOS INTERNOS");
        dgii.GetProperty("status").GetString().ShouldBe("ACTIVO");
        dgii.GetProperty("fromDgii").GetBoolean().ShouldBeTrue();
        (await (await c.GetAsync("/api/fiscal/taxpayers/130000001")).ErrorCode()).ShouldBe("Fiscal.RncNotFound");
        app.DgiiPublic.Requests.ShouldAllBe(r => r.UserAgent!.StartsWith("TokiPOS/")); // never pretends to be a browser

        // The DGII is down (the fake answers 503 to what nobody programmed): the loaded registry answers, here empty.
        (await (await c.GetAsync("/api/fiscal/taxpayers/101999999")).ErrorCode()).ShouldBe("Fiscal.RegistryNotLoaded");
        app.Clock.Advance(TimeSpan.FromMinutes(3)); // past the pause after a failure, for the next test
    }

    [Fact]
    public async Task An_NCF_the_DGII_refuses_is_saved_only_with_a_reason_that_goes_to_the_audit()
    {
        var c = await Admin();
        var supplier = await SupplierAsync(c, "401506254");
        var Today = Toki.SharedKernel.BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        app.DgiiPublic.Ncf("401506254", "B0199999999", "ncf-not-matching").Ncf("401506254", "B0100000001", "ncf-expired");
        object Expense(string ncf, string date, string? reason) => new
        {
            SupplierId = supplier, Ncf = ncf, Date = date, Description = "Papelería", Type = "WorkSuppliesAndServices", Subtotal = 100m, Itbis = 18m,
            Terms = "Cash", Method = "Cash", NcfOverrideReason = reason,
        };

        var refused = await c.PostAsJsonAsync("/api/purchasing/expenses", Expense("B0199999999", Today, null));
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.ErrorCode()).ShouldBe("Purchasing.NcfNotValidAtDgii");
        (await c.PostAsJsonAsync("/api/purchasing/expenses", Expense("B0199999999", Today, "El suplidor confirmó el NCF por teléfono")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords
            .CountAsync(a => a.Action == "purchasing.ncf-not-valid-at-dgii" && a.Data.Contains("B0199999999") && a.Data.Contains("NotMatching") && a.Data.Contains("El suplidor")))).ShouldBe(1);

        // Expired in 2019: fine for an invoice of 2019, not for one of today.
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            SupplierId = supplier, Ncf = "B0100000001", InvoiceDate = "2026-10-10", Terms = "Cash",
            Lines = new[] { new { ProductId = Guid.NewGuid(), Quantity = 1m, UnitCost = 1m } },
        })).ErrorCode()).ShouldBe("Purchasing.NcfNotValidAtDgii");
        // (A purchase: an expense of 2019 is refused anyway, its 606 was filed long ago, R11 A19.)
        var product = await c.ProductAsync(await c.CategoryAsync(), "Papel " + Guid.NewGuid().ToString()[..4], 10m);
        (await c.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            SupplierId = supplier, Ncf = "B0100000001", InvoiceDate = "2019-12-20", Terms = "Cash",
            Lines = new[] { new { ProductId = product, Quantity = 1m, UnitCost = 1m } },
        })).StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
