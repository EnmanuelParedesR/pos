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

/// <summary>TK-276: a business that came with a referral code gets a 30-day store trial; the cloud says so on the license channel. Own app.
/// R12 (security B3): only with a grant the cloud signed for this business; a bare number, a stranger's key or another business's grant change nothing.</summary>
public sealed class ReferredTrialTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task The_cloud_lengthens_the_trial_to_30_days_only_with_its_signed_grant_and_never_shortens_it()
    {
        await app.ConnectAsync();
        var tenant = (await app.StatusAsync()).GetProperty("tenantId").GetGuid();
        object answer = new { license = (string?)null, trialDays = 30 }; // what the cloud sent before R12: unsigned
        app.Cloud.Respond = path => path == PullLicense.Path ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(answer) } : null;
        Task<bool> PullAsync() => app.InScopeAsync(sp => PullLicense.RunAsync(sp.GetRequiredService<CloudClient>(), sp.GetRequiredService<IStoreLicense>(), CancellationToken.None));
        string Grant(Guid tenantId, int days, ECDsa key) => Jws.Sign(new TrialGrant(tenantId, days, app.Clock.GetUtcNow().UtcDateTime), key, Jws.TrialType);

        var cashier = await app.CashierWithShiftAsync();
        var admin = app.Client(await app.AdminAsync());
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 10);
        (await cashier.SellAsync(Store.Sale(cashier.ShiftId, 59m, 59m, Store.Line(soda, 1)))).EnsureSuccessStatusCode();
        await app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.BuildingBlocks.Messaging.OutboxProcessor>().ProcessAsync(CancellationToken.None));
        async Task<int> DaysLeftAsync() => (await admin.GetFromJsonAsync<JsonElement>("/api/settings/features")).GetProperty("trialDaysLeft").GetInt32();
        (await DaysLeftAsync()).ShouldBe(14);

        await PullAsync();
        (await DaysLeftAsync()).ShouldBe(14); // unsigned: ignored
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        answer = new { trial = Grant(tenant, 30, stranger) };
        await PullAsync();
        answer = new { trial = Grant(Guid.NewGuid(), 30, app.CloudLicenseKey) }; // another business's grant
        await PullAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        answer = new { trial = Jws.Sign(new LicenseData("x", "y", [], new Dictionary<string, int>(), now, now.AddDays(1)), app.CloudLicenseKey) }; // a license is no grant
        await PullAsync();
        (await DaysLeftAsync()).ShouldBe(14);

        answer = new { trial = Grant(tenant, 30, app.CloudLicenseKey) };
        await PullAsync();
        (await DaysLeftAsync()).ShouldBe(30);
        (await app.InScopeAsync(sp => sp.GetRequiredService<IStoreLicense>().ExtendTrialAsync(Grant(tenant, 30, app.CloudLicenseKey), tenant, CancellationToken.None)))
            .ShouldBeFalse(); // nothing new
        answer = new { trial = Grant(tenant, 14, app.CloudLicenseKey) };
        await PullAsync();
        (await DaysLeftAsync()).ShouldBe(30); // never shorter
        (await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.Select(b => b.TrialDays).SingleAsync())).ShouldBe(30); // kept for restarts
    }
}

/// <summary>TK-308: the store sends its business RNC with each license pull (the cloud signs it into the license) and a fresh nonce; the cloud's signed
/// time for that nonce brings a license clock that ran ahead back down, like the DGII's. Own app: it moves the license clock.</summary>
public sealed class CloudTimeTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task The_pull_sends_the_RNC_and_only_the_cloud_signed_time_for_this_pull_corrects_the_clock()
    {
        await app.ConnectAsync();
        var tenant = (await app.StatusAsync()).GetProperty("tenantId").GetGuid();
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.Rnc, "131-24567-8")));
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var clock = app.Services.GetRequiredService<LicenseService>().Time;
        clock.Observe(now.AddDays(10)); // the PC once booted ten days ahead and the store saw that date

        Func<string, ECDsa, Guid, string> time = (nonce, key, tenantId) => "";
        app.Cloud.Respond = path => path == PullLicense.Path ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { license = (string?)null, time = time(app.Cloud.Requests.Last().Body.GetProperty("nonce").GetString()!, app.CloudLicenseKey, tenant) }),
        } : null;
        Task PullAsync() => app.InScopeAsync(sp => PullLicense.RunAsync(sp.GetRequiredService<CloudClient>(), sp.GetRequiredService<IStoreLicense>(), CancellationToken.None));
        string Signed(ServerTime t, ECDsa key) => Jws.Sign(t, key, Jws.TimeType);

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var replayed = Signed(new ServerTime(tenant, "an-older-pull", now), app.CloudLicenseKey);
        foreach (var refused in new Func<string, ECDsa, Guid, string>[]
        {
            (n, _, t) => Signed(new ServerTime(t, n, now), stranger),                                  // not Abastalia's key
            (n, k, _) => Signed(new ServerTime(Guid.NewGuid(), n, now), k),                            // another business's
            (_, _, _) => replayed,                                                                     // another pull's nonce
            (n, k, _) => Jws.Sign(new LicenseData("x", "y", [], new Dictionary<string, int>(), now, now.AddDays(1)), k), // a license is no time
        })
        {
            time = refused;
            await PullAsync();
            clock.UtcNow.ShouldBeGreaterThan(now.AddDays(9));
        }
        app.Cloud.To(PullLicense.Path).ShouldAllBe(r => r.Body.GetProperty("rnc").GetString() == "131-24567-8");

        time = (n, k, t) => Signed(new ServerTime(t, n, now), k);
        await PullAsync();
        clock.UtcNow.ShouldBeLessThan(now.AddMinutes(1));
    }
}

/// <summary>TK-287: a block or an unblock from the console reaches the store with its next upload (the cloud's <c>licenseUtc</c> moved), not at its
/// next 4-hour heartbeat; a blocked store, which uploads little, asks every few minutes. Own app: it installs licenses and moves the clock.</summary>
public sealed class LicenseSignalTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task The_store_pulls_its_license_when_the_cloud_stamp_moves_and_every_few_minutes_while_blocked()
    {
        var now = app.Clock.GetUtcNow().UtcDateTime;
        LicenseData License(int minutes, DateTime expires) =>
            new("Carnicería La Esquina", "Varias cajas", [], new Dictionary<string, int> { ["Registers"] = 3 }, now.AddMinutes(minutes), expires);
        await app.ConnectAsync();
        var (stamp, license) = ((DateTime?)null, Jws.Sign(License(1, now.AddMinutes(1)) with { Blocked = true }, app.CloudLicenseKey));
        app.Cloud.Respond = path => path switch
        {
            CloudUploader.IngestPath => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                accepted = app.Cloud.Requests.Last().Body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList(), licenseUtc = stamp,
            }) },
            PullLicense.Path => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { license }) },
            _ => null,
        };
        var pulls = () => app.Cloud.To(PullLicense.Path).Count();
        Task Heartbeat() => app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.BuildingBlocks.Messaging.ICommandHandler<SendHeartbeat.Command, Abastalia.BuildingBlocks.Messaging.Ok>>()
            .Handle(new SendHeartbeat.Command(), CancellationToken.None));
        Task Upload() => app.InScopeAsync(sp => sp.GetRequiredService<CloudUploader>().UploadAsync(CancellationToken.None));
        string Status() => app.Services.GetRequiredService<IFeatureService>().Status.ToString();

        await Heartbeat();
        await Upload();
        pulls().ShouldBe(0); // no stamp (an older cloud, or no subscription): nothing to ask

        stamp = now; // the console blocked the business
        await Heartbeat();
        await Upload();
        (pulls(), Status()).ShouldBe((1, "Blocked"));
        await Heartbeat();
        await Upload();
        pulls().ShouldBe(1); // the same stamp: asked once

        // Blocked: it asks again every few minutes, uploading or not; the unblock lands then.
        license = Jws.Sign(License(2, now.AddDays(30)), app.CloudLicenseKey);
        await Upload();
        pulls().ShouldBe(1);
        app.Clock.Advance(LicenseSignal.WhileBlocked);
        await Upload();
        (pulls(), Status()).ShouldBe((2, "Active"));
        app.Clock.Advance(LicenseSignal.WhileBlocked);
        await Upload();
        pulls().ShouldBe(2); // not blocked: no more asking
    }
}
