using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Security;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Settings;
using Abastalia.Settings.Contracts;
using Abastalia.Sync;

namespace Abastalia.IntegrationTests.Sync;

/// <summary>TK-249: the store pulls the license the cloud issued for its subscription, signed with the cloud key it trusts besides the offline one.
/// Own app: it installs licenses and moves the clock.</summary>
public sealed class CloudLicenseTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static LicenseData License(string plan, DateTime issued, DateTime expires) =>
        new("Carnicería La Esquina", plan, ["ButcherModule", "SupplierInvoiceReading"], new Dictionary<string, int> { ["Registers"] = 3 }, issued, expires);

    private void CloudAnswers(string? license, HttpStatusCode status = HttpStatusCode.OK) =>
        app.Cloud.Respond = path => path == PullLicense.Path ? new HttpResponseMessage(status) { Content = JsonContent.Create(new { license }) } : null;

    private Task<bool> PullAsync() => app.InScopeAsync(sp => PullLicense.RunAsync(sp.GetRequiredService<CloudClient>(), sp.GetRequiredService<IStoreLicense>(), CancellationToken.None));

    private async Task<JsonElement> FeaturesAsync() =>
        await app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, (await app.AdminAsync()).Device)).GetFromJsonAsync<JsonElement>("/api/settings/features");

    [Fact]
    public async Task The_store_installs_the_cloud_license_only_when_valid_and_newer_and_it_runs_out_with_grace()
    {
        var now = app.Clock.GetUtcNow().UtcDateTime;
        (await PullAsync()).ShouldBeFalse(); // not connected
        await app.ConnectAsync();
        CloudAnswers(null);
        (await PullAsync()).ShouldBeFalse(); // nothing paid
        CloudAnswers(null, HttpStatusCode.NotFound);
        (await PullAsync()).ShouldBeFalse(); // an older cloud without the endpoint

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CloudAnswers(Jws.Sign(License("Varias cajas", now, now.AddDays(30)), stranger));
        (await PullAsync()).ShouldBeFalse();
        (await FeaturesAsync()).GetProperty("status").GetString().ShouldBe("Trial");

        var paidThrough = now.AddDays(30);
        CloudAnswers(Jws.Sign(License("Varias cajas", now, paidThrough), app.CloudLicenseKey));
        (await PullAsync()).ShouldBeTrue();
        var features = await FeaturesAsync();
        (features.GetProperty("status").GetString(), features.GetProperty("plan").GetString(), features.GetProperty("registers").GetInt32()).ShouldBe(("Active", "Varias cajas", 3));
        features.GetProperty("enabled").EnumerateArray().Select(f => f.GetString()).ShouldContain("SupplierInvoiceReading");
        (await PullAsync()).ShouldBeFalse(); // the same license again: nothing to do
        CloudAnswers(Jws.Sign(License("Una caja", now.AddHours(-1), paidThrough), app.CloudLicenseKey));
        (await PullAsync()).ShouldBeFalse(); // issued before the installed one
        app.Services.GetRequiredService<LicenseService>().Data!.Plan.ShouldBe("Varias cajas");

        // Offline issuance keeps working: the owner's key, pasted in Configuración → Licencia.
        var admin = app.Client(await app.AdminAsync());
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(License("Varias sucursales", now.AddMinutes(1), paidThrough), app.LicenseKey) }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await FeaturesAsync()).GetProperty("plan").GetString().ShouldBe("Varias sucursales");

        // Past due: the cloud stops renewing. The license runs out, 15 days of grace keep everything, then Restricted (Blocked 30 days after expiry, TK-279).
        app.Clock.Advance(TimeSpan.FromDays(31));
        (await FeaturesAsync()).GetProperty("status").GetString().ShouldBe("Grace");
        app.Clock.Advance(LicenseService.GracePeriod);
        features = await FeaturesAsync();
        (features.GetProperty("status").GetString(), features.GetProperty("registers").GetInt32()).ShouldBe(("Restricted", LicenseService.RestrictedRegisters));

        // Paid again: the next pull brings the new period and lifts it at once.
        now = app.Clock.GetUtcNow().UtcDateTime;
        CloudAnswers(Jws.Sign(License("Varias cajas", now, now.AddDays(30)), app.CloudLicenseKey));
        (await PullAsync()).ShouldBeTrue();
        (await FeaturesAsync()).GetProperty("status").GetString().ShouldBe("Active");

        // TK-279: Abastalia blocks the business from the console: the next pull blocks the store at once; unblocking lifts it the same way.
        CloudAnswers(Jws.Sign(License("Varias cajas", now.AddMinutes(1), now.AddMinutes(1)) with { Blocked = true }, app.CloudLicenseKey));
        (await PullAsync()).ShouldBeTrue();
        (await FeaturesAsync()).GetProperty("status").GetString().ShouldBe("Blocked");
        CloudAnswers(Jws.Sign(License("Varias cajas", now.AddMinutes(2), now.AddDays(30)), app.CloudLicenseKey));
        (await PullAsync()).ShouldBeTrue();
        (await FeaturesAsync()).GetProperty("status").GetString().ShouldBe("Active");
        app.Cloud.To(PullLicense.Path).ShouldAllBe(r => app.Cloud.SignedByNode(r)); // asked as this node, signed with its key
    }
}

/// <summary>TK-276: a business that came with a referral code gets a 30-day store trial; the cloud says so on the license channel. Own app.</summary>
public sealed class ReferredTrialTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task The_cloud_lengthens_the_trial_to_30_days_and_never_shortens_it()
    {
        await app.ConnectAsync();
        var trialDays = 30;
        app.Cloud.Respond = path => path == PullLicense.Path ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { license = (string?)null, trialDays }) } : null;
        Task<bool> PullAsync() => app.InScopeAsync(sp => PullLicense.RunAsync(sp.GetRequiredService<CloudClient>(), sp.GetRequiredService<IStoreLicense>(), CancellationToken.None));

        var cashier = await app.CashierWithShiftAsync();
        var admin = app.Client(await app.AdminAsync());
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 10);
        (await cashier.SellAsync(Store.Sale(cashier.ShiftId, 59m, 59m, Store.Line(soda, 1)))).EnsureSuccessStatusCode();
        await app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.BuildingBlocks.Messaging.OutboxProcessor>().ProcessAsync(CancellationToken.None));
        async Task<int> DaysLeftAsync() => (await admin.GetFromJsonAsync<JsonElement>("/api/settings/features")).GetProperty("trialDaysLeft").GetInt32();
        (await DaysLeftAsync()).ShouldBe(14);

        await PullAsync();
        (await DaysLeftAsync()).ShouldBe(30);
        (await app.InScopeAsync(sp => sp.GetRequiredService<IStoreLicense>().ExtendTrialAsync(30, CancellationToken.None))).ShouldBeFalse(); // nothing new
        trialDays = 14;
        await PullAsync();
        (await DaysLeftAsync()).ShouldBe(30); // never shorter
        (await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.Select(b => b.TrialDays).SingleAsync())).ShouldBe(30); // kept for restarts
    }
}
