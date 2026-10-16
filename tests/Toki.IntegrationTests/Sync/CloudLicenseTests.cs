using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Security;
using Toki.IntegrationTests.Infra;
using Toki.Settings;
using Toki.Settings.Contracts;
using Toki.Sync;

namespace Toki.IntegrationTests.Sync;

/// <summary>TK-249: the store pulls the license the cloud issued for its subscription, signed with the cloud key it trusts besides the offline one.
/// Own app: it installs licenses and moves the clock.</summary>
public sealed class CloudLicenseTests(TokiApp app) : IClassFixture<TokiApp>
{
    private static LicenseData License(string plan, DateTime issued, DateTime expires) =>
        new("Carnicería La Esquina", plan, ["ButcherModule", "SupplierInvoiceReading"], new Dictionary<string, int> { ["Registers"] = 3 }, issued, expires);

    private void CloudAnswers(string? license, HttpStatusCode status = HttpStatusCode.OK) =>
        app.Cloud.Respond = path => path == PullLicense.Path ? new HttpResponseMessage(status) { Content = JsonContent.Create(new { license }) } : null;

    private Task<bool> PullAsync() => app.InScopeAsync(sp => PullLicense.RunAsync(sp.GetRequiredService<CloudClient>(), sp.GetRequiredService<IStoreLicense>(), CancellationToken.None));

    private async Task<JsonElement> FeaturesAsync() =>
        await app.Client(await app.SignInAsync("admin", TokiApp.AdminPin, (await app.AdminAsync()).Device)).GetFromJsonAsync<JsonElement>("/api/settings/features");

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

        // Past due: the cloud stops renewing. The license runs out, 15 days of grace keep everything, then Restricted (selling never stops).
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
        app.Cloud.To(PullLicense.Path).ShouldAllBe(r => app.Cloud.SignedByNode(r)); // asked as this node, signed with its key
    }
}
