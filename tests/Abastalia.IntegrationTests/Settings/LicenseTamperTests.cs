using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Abastalia.BuildingBlocks.Security;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Settings;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>R12 helpers: licenses issued minutes from now and a second LicenseService on the app's database and keys, as after a restart.</summary>
internal static class Licenses
{
    public static LicenseData License(this AbastaliaApp app, int minutes, bool blocked = false, string? rnc = null)
    {
        var now = app.Clock.GetUtcNow().UtcDateTime;
        return new("Colmado de prueba", "Estándar", [], new Dictionary<string, int> { ["Registers"] = 1 }, now.AddMinutes(minutes), now.AddDays(30), Blocked: blocked, Rnc: rnc);
    }

    public static LicenseService Restarted(this AbastaliaApp app, string file) => new(
        new ConfigurationBuilder().AddConfiguration(app.Services.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?> { ["License:File"] = file }).Build(),
        app.Services.GetRequiredService<IHostEnvironment>(), app.Clock, app.Services.GetRequiredService<IServiceScopeFactory>(),
        app.Services.GetRequiredService<ServiceKeys>());

    public static string TempFile() => Path.Combine(Path.GetTempPath(), $"abastalia_license_{Guid.NewGuid():N}.jws");
}

/// <summary>R12 (security M2): an older license never undoes a newer one (a block). Own app: it installs licenses.</summary>
public sealed class OlderLicenseTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private LicenseData License(int minutes, bool blocked = false) => app.License(minutes, blocked);
    private LicenseService Restarted(string file) => app.Restarted(file);

    [Fact]
    public async Task An_older_license_never_undoes_a_block_not_even_after_deleting_the_license_file()
    {
        var admin = app.Client(await app.AdminAsync());
        async Task<HttpResponseMessage> Install(LicenseData l) => await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(l, app.CloudLicenseKey) });
        var older = License(1);
        (await Install(License(2, blocked: true))).EnsureSuccessStatusCode();
        var refused = await Install(older);
        (refused.StatusCode, await refused.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "License.Older"));
        var features = await admin.GetFromJsonAsync<JsonElement>("/api/settings/features");
        (features.GetProperty("status").GetString(), features.GetProperty("blockReason").GetString()).ShouldBe(("Blocked", "Suspended"));
        app.Services.GetRequiredService<LicenseService>().BlockedError.Description.ShouldContain("Abastalia suspendió el servicio");

        // Deleting license.jws and restarting: still Blocked (the newest license seen is kept sealed in the keys folder).
        var license = app.Services.GetRequiredService<LicenseService>();
        var copy = Path.Combine(Path.GetTempPath(), $"abastalia_license_{Guid.NewGuid():N}.jws");
        Restarted(copy).Status.ShouldBe(LicenseStatus.Blocked);
        File.WriteAllText(copy, Jws.Sign(older, app.CloudLicenseKey)); // or putting the older file in its place
        Restarted(copy).Status.ShouldBe(LicenseStatus.Blocked);
        license.Status.ShouldBe(LicenseStatus.Blocked);

        (await Install(License(3))).EnsureSuccessStatusCode(); // a newer one lifts it
        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/features")).GetProperty("status").GetString().ShouldBe("Active");
    }

}

/// <summary>R12 (audit A): a license names its business's RNC. Own app: it sets the RNC and installs licenses.</summary>
public sealed class LicenseRncTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private LicenseData License(int minutes, string? rnc = null) => app.License(minutes, rnc: rnc);
    private LicenseService Restarted(string file) => app.Restarted(file);

    [Fact]
    public async Task A_license_for_another_RNC_is_refused_and_ignored_when_loaded()
    {
        var admin = app.Client(await app.AdminAsync());
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.Rnc, "130000001")));
        var refused = await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(License(10, rnc: "1-30-00000-2"), app.LicenseKey) });
        (refused.StatusCode, await refused.ErrorCode()).ShouldBe((HttpStatusCode.BadRequest, "License.OtherBusiness"));
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(License(11, rnc: "1-30-00000-1"), app.LicenseKey) })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent); // same digits, other format

        var copy = Path.Combine(Path.GetTempPath(), $"abastalia_license_{Guid.NewGuid():N}.jws");
        File.WriteAllText(copy, Jws.Sign(License(12, rnc: "130000002"), app.LicenseKey)); // copied from another business
        Restarted(copy).Data.ShouldBeNull();
        File.WriteAllText(copy, Jws.Sign(License(12), app.LicenseKey)); // without RNC (issued before R12): accepted until it expires
        Restarted(copy).Data.ShouldNotBeNull();
    }

}

/// <summary>R12: the trial starts by the license clock, not the PC's (turned back before the first sale). Own app: it moves the time the store saw.</summary>
public sealed class TrialClockTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task With_the_clock_turned_back_the_trial_starts_at_the_latest_time_the_store_saw()
    {
        var seen = app.Clock.GetUtcNow().UtcDateTime.AddDays(5);
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s
            .SetProperty(b => b.ClockSeenUtc, seen).SetProperty(b => b.ClockSeenSeal, LicenseClock.Seal(seen, sp.GetRequiredService<ServiceKeys>().GetOrCreate("license-clock")))));
        var cashier = await app.CashierWithShiftAsync();
        var admin = app.Client(await app.AdminAsync());
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 10);
        (await cashier.SellAsync(Store.Sale(cashier.ShiftId, 59m, 59m, Store.Line(soda, 1)))).EnsureSuccessStatusCode();
        await app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.BuildingBlocks.Messaging.OutboxProcessor>().ProcessAsync(CancellationToken.None));
        var ends = (await admin.GetFromJsonAsync<JsonElement>("/api/settings/features")).GetProperty("trialEndsUtc").GetDateTime();
        ends.ShouldBe(seen + LicenseService.TrialPeriod, TimeSpan.FromMinutes(1));
    }
}

/// <summary>TK-369: the trial's start and length have a sealed twin in the keys folder. Own app: it sells and edits the trial in the database.</summary>
public sealed class TrialSealTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task Clearing_or_stretching_the_trial_in_the_database_does_not_restart_it()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = app.Client(await app.AdminAsync());
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 10);
        (await cashier.SellAsync(Store.Sale(cashier.ShiftId, 59m, 59m, Store.Line(soda, 1)))).EnsureSuccessStatusCode();
        await app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.BuildingBlocks.Messaging.OutboxProcessor>().ProcessAsync(CancellationToken.None));
        var started = app.Services.GetRequiredService<LicenseService>().TrialStartedUtc;
        started.ShouldNotBeNull();

        // Reinstalling over the same data folder with a wiped (or restored older) database, or editing it by hand: the trial goes on.
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s
            .SetProperty(b => b.TrialStartedUtc, (DateTime?)null).SetProperty(b => b.TrialDays, 90)));
        var restarted = app.Restarted(Licenses.TempFile());
        (restarted.TrialStartedUtc, restarted.TrialLength).ShouldBe((started, LicenseService.TrialPeriod));
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s
            .SetProperty(b => b.TrialStartedUtc, started!.Value.AddDays(20))));
        app.Restarted(Licenses.TempFile()).TrialStartedUtc.ShouldBe(started); // the earlier start wins

        // Without the twin (a store from before TK-369), the database is believed, its days at most 90, and sealed from then on.
        File.Delete(Path.Combine(app.Services.GetRequiredService<ServiceKeys>().Folder, LicenseService.TrialFile));
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.TrialDays, 400)));
        app.Restarted(Licenses.TempFile()).TrialLength.ShouldBe(TimeSpan.FromDays(LicenseService.MaxTrialDays));
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.TrialDays, (int?)null)));
        app.Restarted(Licenses.TempFile()).TrialLength.ShouldBe(TimeSpan.FromDays(LicenseService.MaxTrialDays));
    }
}

/// <summary>R12 (security B1): the clock's sealed copy in the keys folder. Own app: it moves the time the store saw.</summary>
public sealed class LicenseClockCopyTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private LicenseService Restarted(string file) => app.Restarted(file);

    [Fact]
    public async Task Wiping_the_sealed_time_in_the_database_falls_back_to_its_copy_in_the_keys_folder()
    {
        var license = app.Services.GetRequiredService<LicenseService>();
        var seen = app.Clock.GetUtcNow().UtcDateTime.AddDays(90);
        license.WriteSealed(LicenseService.SeenFile, seen.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s
            .SetProperty(b => b.ClockSeenUtc, (DateTime?)null).SetProperty(b => b.ClockSeenSeal, (string?)null)));
        var restarted = Restarted(Path.Combine(Path.GetTempPath(), $"abastalia_license_{Guid.NewGuid():N}.jws"));
        restarted.Time.UtcNow.ShouldBe(seen, TimeSpan.FromSeconds(1));
        restarted.Time.Behind.ShouldBeTrue();

        // An edited copy is ignored like the database's.
        var file = Path.Combine(app.Services.GetRequiredService<ServiceKeys>().Folder, LicenseService.SeenFile);
        File.WriteAllText(file, File.ReadAllText(file).Replace(seen.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), seen.AddDays(-80).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        restarted.ReadSealed(LicenseService.SeenFile).ShouldBeNull();
    }
}
