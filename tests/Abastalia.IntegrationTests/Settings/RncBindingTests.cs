using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Fiscal;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Settings;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>TK-368: once a license names the RNC, the business RNC is fixed: only support changes it (a license that says which RNC it replaces),
/// and no receipt goes out under another RNC. Own app: it installs licenses and edits the RNC.</summary>
public sealed class RncBindingTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private const string Rnc = "131245678", Other = "101672919";

    private static object Business(string? rnc) => new { Name = "Carnicería de prueba", Rnc = rnc, DiscountCapPercent = 10m };

    private async Task<HttpResponseMessage> InstallAsync(HttpClient admin, int minutes, string? rnc, string? replaces = null) =>
        await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(app.License(minutes, rnc: rnc) with { ReplacesRnc = replaces }, app.LicenseKey) });

    [Fact]
    public async Task The_RNC_a_license_names_cannot_be_changed_here_and_support_moves_it_with_a_new_license()
    {
        await app.LoadSequencesAsync(); // RNC 131245678, e-NCF ranges
        var admin = app.Client(await app.AdminAsync());
        (await InstallAsync(admin, 1, Rnc)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/business")).GetProperty("licensedRnc").GetString().ShouldBe(Rnc);

        // Configuración → Negocio: another RNC (or none) is refused; the same one in another format and the other fields still save.
        var refused = await admin.PutAsJsonAsync("/api/settings/business", Business(Other));
        (refused.StatusCode, await refused.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "Settings.RncBound"));
        (await (await admin.PutAsJsonAsync("/api/settings/business", Business(null))).ErrorCode()).ShouldBe("Settings.RncBound");
        (await admin.PutAsJsonAsync("/api/settings/business", Business("1-31-24567-8"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A license of another business is still refused; one from support that says it replaces this RNC moves the business to the new one.
        (await (await InstallAsync(admin, 2, Other)).ErrorCode()).ShouldBe("License.OtherBusiness");
        (await (await InstallAsync(admin, 2, Other, replaces: "999999999")).ErrorCode()).ShouldBe("License.OtherBusiness");
        (await InstallAsync(admin, 3, Other, replaces: Rnc)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var business = await admin.GetFromJsonAsync<JsonElement>("/api/settings/business");
        (business.GetProperty("rnc").GetString(), business.GetProperty("licensedRnc").GetString()).ShouldBe((Other, Other));
        app.Restarted(app.Services.GetRequiredService<LicenseService>().FilePath).Data.ShouldNotBeNull(); // still valid after a restart
    }
}

/// <summary>TK-368: one license, one business. Own app: it installs a license and edits the RNC behind the API.</summary>
public sealed class RncNotLicensedTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private async Task<HttpResponseMessage> InstallAsync(HttpClient admin, int minutes, string? rnc) =>
        await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(app.License(minutes, rnc: rnc), app.LicenseKey) });

    [Fact]
    public async Task No_receipt_goes_out_under_an_RNC_the_installed_license_does_not_name()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = app.Client(await app.AdminAsync());
        var current = (await admin.GetFromJsonAsync<JsonElement>("/api/settings/business")).GetProperty("rnc").GetString();
        (await InstallAsync(admin, 10, current)).EnsureSuccessStatusCode();
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 10);
        (await cashier.SellAsync(Store.Sale(cashier.ShiftId, 59m, 59m, Store.Line(soda, 1)))).EnsureSuccessStatusCode();

        // The RNC changed behind the API's back (the database edited, a backup of another business restored).
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.Rnc, "130000009")));
        var refused = await cashier.SellAsync(Store.Sale(cashier.ShiftId, 59m, 59m, Store.Line(soda, 1)));
        (await refused.ErrorCode()).ShouldBe("Fiscal.RncNotLicensed");
    }
}

/// <summary>TK-368: without a license naming it, the RNC is fixed once a real receipt (eCF environment or paper B) carries it. Own app.</summary>
public sealed class RncAfterFirstReceiptTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task The_RNC_is_fixed_after_the_first_real_receipt()
    {
        await app.SetUpBusinessAsync();
        var admin = app.Client(await app.AdminAsync());
        var body = new { Name = "Carnicería de prueba", Rnc = "101672919", DiscountCapPercent = 10m };
        (await admin.PutAsJsonAsync("/api/settings/business", body)).StatusCode.ShouldBe(HttpStatusCode.NoContent); // nothing issued yet: free

        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            var contingency = new Abastalia.Fiscal.Domain.EcfContingency { Id = Guid.CreateVersion7(), StartedUtc = DateTime.UtcNow, Reason = "Prueba" };
            db.Add(contingency);
            db.Add(new Abastalia.Fiscal.Domain.PaperDocument
            {
                Id = Guid.CreateVersion7(), Ncf = "B0200000001", Type = Abastalia.Fiscal.Contracts.EcfType.Consumer32, ReferenceId = Guid.CreateVersion7(),
                ContingencyId = contingency.Id, IssuedUtc = DateTime.UtcNow, Total = 59m, Data = "{}",
            });
            return await db.SaveChangesAsync();
        });
        var refused = await admin.PutAsJsonAsync("/api/settings/business", body with { Rnc = "131245678" });
        (refused.StatusCode, await refused.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "Settings.RncInUse"));
        (await admin.PutAsJsonAsync("/api/settings/business", body with { Name = "Carnicería Nueva" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
