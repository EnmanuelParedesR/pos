using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Security;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Settings;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>TK-279: an unpaid license ends Blocked: no new sale, shift or e-CF; what the law and the owner's data need goes on. Own app: it moves the clock.</summary>
public sealed class BlockedLicenseTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task Thirty_days_after_expiry_the_store_is_blocked_keeps_its_duties_and_a_new_license_lifts_it_at_once()
    {
        await app.LoadSequencesAsync();
        var device = (await app.AdminAsync()).Device;
        var admin = app.Client(await app.AdminAsync());
        var soda = await admin.ProductAsync(await admin.CategoryAsync("Bebidas"), "Refresco", 59m, stock: 100);
        var register = (await (await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja 1" })).Json()).GetProperty("id").GetGuid();
        var shift = (await (await admin.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 500m })).Json()).GetProperty("shiftId").GetGuid();
        var sale = (await (await admin.PostAsJsonAsync("/api/sales", Sale(shift, 59m, 59m, Line(soda, 1)))).Json()).GetProperty("saleId").GetGuid();

        var now = app.Clock.GetUtcNow().UtcDateTime;
        LicenseData License(DateTime expires) => new("Colmado de prueba", "Estándar", [], new Dictionary<string, int> { ["Registers"] = 1 }, app.Clock.GetUtcNow().UtcDateTime, expires);
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(License(now.AddDays(1)), app.LicenseKey) })).EnsureSuccessStatusCode();
        async Task<JsonElement> Features()
        {
            admin = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, device)); // the clock jumps past the session
            return await admin.GetFromJsonAsync<JsonElement>("/api/settings/features");
        }

        // Expired: 15 days of grace, then Restricted until 30 days after the expiry, with the date it stops.
        app.Clock.Advance(TimeSpan.FromDays(1 + 16));
        var features = await Features();
        (features.GetProperty("status").GetString(), features.GetProperty("blocksUtc").GetDateTime()).ShouldBe(("Restricted", now.AddDays(1 + LicenseService.DefaultBlockAfterDays)));
        (await admin.PostAsJsonAsync("/api/sales", Sale(shift, 59m, 59m, Line(soda, 1)))).StatusCode.ShouldBe(HttpStatusCode.Created); // still sells

        app.Clock.Advance(TimeSpan.FromDays(LicenseService.DefaultBlockAfterDays - 16) + TimeSpan.FromMinutes(1));
        features = await Features();
        (features.GetProperty("status").GetString(), features.GetProperty("warning").GetString()).ShouldBe(("Blocked", "Blocked"));

        // Refused with a clear code: a new sale, a new shift, a credit note (a new e-CF).
        var refused = await admin.PostAsJsonAsync("/api/sales", Sale(shift, 59m, 59m, Line(soda, 1)));
        (refused.StatusCode, await refused.ErrorCode()).ShouldBe((HttpStatusCode.Forbidden, "License.Blocked"));
        (await refused.Content.ReadAsStringAsync()).ShouldContain("Su licencia está vencida");
        (await (await admin.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 500m })).ErrorCode()).ShouldBe("License.Blocked");
        (await (await admin.PostAsJsonAsync($"/api/sales/{sale}/void", new { Reason = "Error" })).ErrorCode()).ShouldBe("License.Blocked");

        // Still allowed: the reports and the 607, the e-CF queue, a backup and closing the open shift.
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        (await admin.GetAsync($"/api/reports/dgii/607?from={today.AddDays(-60):yyyy-MM-dd}&to={today:yyyy-MM-dd}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync("/api/fiscal/queue")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase = "una frase larga para los respaldos" })).EnsureSuccessStatusCode();
        // A container SQL Server writes the .bak inside itself (BackupsTests skips the same way); the license gate is what matters here.
        if (TestSqlServer.BacksUpOnThisMachine) (await admin.PostAsync("/api/backups", null)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var closed = await admin.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", new { CashCount = new[] { new { Denomination = 500m, Quantity = 1 } } });
        closed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Paid: the new license lifts it at once, every sale kept.
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(License(app.Clock.GetUtcNow().UtcDateTime.AddDays(30)), app.LicenseKey) }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Features()).GetProperty("status").GetString().ShouldBe("Active");
        var reopened = (await (await admin.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 500m })).Json()).GetProperty("shiftId").GetGuid();
        (await admin.PostAsJsonAsync("/api/sales", Sale(reopened, 59m, 59m, Line(soda, 1)))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}

/// <summary>TK-279. Own app (TK-308): with R12's "never an older license" it cannot share one with a test that installs licenses issued earlier.</summary>
public sealed class CloudBlockedLicenseTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task A_license_the_cloud_marked_blocked_blocks_at_once_and_a_later_one_lifts_it()
    {
        var admin = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, (await app.AdminAsync()).Device));
        var now = app.Clock.GetUtcNow().UtcDateTime;
        LicenseData License(bool blocked, int minutes) =>
            new("Colmado de prueba", "Estándar", [], new Dictionary<string, int> { ["Registers"] = 1 }, now.AddMinutes(minutes), now.AddDays(30), Blocked: blocked);
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(License(true, 1), app.CloudLicenseKey) })).EnsureSuccessStatusCode();
        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/features")).GetProperty("status").GetString().ShouldBe("Blocked");
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(License(false, 2), app.CloudLicenseKey) })).EnsureSuccessStatusCode();
        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/features")).GetProperty("status").GetString().ShouldBe("Active");
    }
}

/// <summary>TK-279: turning the PC's clock back does not stretch the license. Own app: it seeds the time the store saw before anything reads it.</summary>
public sealed class LicenseClockRollbackTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task With_the_clock_turned_back_the_license_counts_from_the_latest_time_the_store_saw()
    {
        // The server saw day 90 and was restarted with the PC's clock back at day 0 (the fake clock stands for that PC clock).
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var seen = now.AddDays(90);
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s
            .SetProperty(b => b.ClockSeenUtc, seen).SetProperty(b => b.ClockSeenSeal, LicenseClock.Seal(seen, sp.GetRequiredService<ServiceKeys>().GetOrCreate("license-clock")))));

        var admin = app.Client(await app.AdminAsync());
        var license = new LicenseData("Colmado de prueba", "Estándar", [], new Dictionary<string, int> { ["Registers"] = 1 }, now, now.AddDays(70));
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(license, app.LicenseKey) })).EnsureSuccessStatusCode();
        var features = await admin.GetFromJsonAsync<JsonElement>("/api/settings/features");
        // By the PC's clock it would be Active for 70 days; by the time the store saw it expired 20 days ago.
        (features.GetProperty("status").GetString(), features.GetProperty("clockBehind").GetBoolean()).ShouldBe(("Restricted", true));

        app.Clock.Advance(TimeSpan.FromDays(11)); // working on with the wrong date still ages the license: day 101, 31 days after expiry
        admin = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, (await app.AdminAsync()).Device));
        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/features")).GetProperty("status").GetString().ShouldBe("Blocked");
    }
}
