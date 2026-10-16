using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Messaging;
using Toki.IntegrationTests.Infra;
using Toki.Settings;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Settings;

/// <summary>TK-196 / TK-220: the 14-day trial, its warnings, Restricted and buying the license. Own app: it moves the clock weeks ahead.</summary>
public sealed class TrialTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task The_trial_starts_with_the_first_sale_warns_on_days_10_and_13_and_then_restricts_without_stopping_the_sale()
    {
        await app.LoadSequencesAsync();
        var device = (await app.AdminAsync()).Device;
        var admin = app.Client(await app.AdminAsync());
        var soda = await admin.ProductAsync(await admin.CategoryAsync("Bebidas"), "Refresco", 59m, stock: 100);
        async Task<JsonElement> Features() => await admin.GetFromJsonAsync<JsonElement>("/api/settings/features");

        // Installed without selling: everything on, and no days used even a month later.
        var features = await Features();
        (features.GetProperty("status").GetString(), features.GetProperty("warning").ValueKind).ShouldBe(("Trial", JsonValueKind.Null));
        features.GetProperty("trialEndsUtc").ValueKind.ShouldBe(JsonValueKind.Null);
        features.GetProperty("enabled").GetArrayLength().ShouldBe(4);
        app.Clock.Advance(TimeSpan.FromMinutes(1)); // registers pair after the main PC, in order
        var registerA = await PairAsync(admin);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var registerB = await PairAsync(admin);
        app.Clock.Advance(TimeSpan.FromDays(30));
        admin = app.Client(await app.SignInAsync("admin", TokiApp.AdminPin, device));
        (await Features()).GetProperty("trialEndsUtc").ValueKind.ShouldBe(JsonValueKind.Null);

        // The first sale starts the 14 days (once the outbox delivers it).
        var firstSale = app.Clock.GetUtcNow().UtcDateTime;
        var shift = await OpenShiftAsync(admin);
        (await admin.PostAsJsonAsync("/api/sales", Sale(shift, 59m, 59m, Line(soda, 1)))).EnsureSuccessStatusCode();
        await ProcessOutboxAsync();
        features = await Features();
        (features.GetProperty("trialEndsUtc").GetDateTime() - firstSale.Add(LicenseService.TrialPeriod)).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));
        (features.GetProperty("trialDaysLeft").GetInt32(), features.GetProperty("warning").ValueKind).ShouldBe((14, JsonValueKind.Null));
        // A later sale does not move it.
        app.Clock.Advance(TimeSpan.FromHours(1));
        (await admin.PostAsJsonAsync("/api/sales", Sale(shift, 59m, 59m, Line(soda, 1)))).EnsureSuccessStatusCode();
        await ProcessOutboxAsync();
        (await Features()).GetProperty("trialDaysLeft").GetInt32().ShouldBe(14);

        // Day 10 and day 13 warn.
        app.Clock.Advance(TimeSpan.FromDays(9) - TimeSpan.FromHours(1));
        admin = app.Client(await app.SignInAsync("admin", TokiApp.AdminPin, device));
        features = await Features();
        (features.GetProperty("status").GetString(), features.GetProperty("trialDaysLeft").GetInt32(), features.GetProperty("warning").GetString()).ShouldBe(("Trial", 5, "Soon"));
        app.Clock.Advance(TimeSpan.FromDays(3));
        admin = app.Client(await app.SignInAsync("admin", TokiApp.AdminPin, device));
        features = await Features();
        (features.GetProperty("trialDaysLeft").GetInt32(), features.GetProperty("warning").GetString()).ShouldBe((2, "Last"));

        // Day 15: Restricted. Paid functions off, the main PC and one register left; the sale and its e-CF go on.
        app.Clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromMinutes(1));
        admin = app.Client(await app.SignInAsync("admin", TokiApp.AdminPin, device));
        features = await Features();
        (features.GetProperty("status").GetString(), features.GetProperty("warning").GetString(), features.GetProperty("registers").GetInt32())
            .ShouldBe(("Restricted", "Ended", LicenseService.RestrictedRegisters));
        features.GetProperty("enabled").GetArrayLength().ShouldBe(0);
        (await app.SignInAsync("admin", TokiApp.AdminPin, registerA)).Token.ShouldNotBeNullOrEmpty(); // the oldest register still sells
        (await SignInAsync(registerB)).ShouldBe("Identity.RegisterNotLicensed");
        (await (await PairRawAsync(admin)).ErrorCode()).ShouldBe("Identity.RegisterLimitReached");

        var sold = await admin.PostAsJsonAsync("/api/sales", Sale(shift, 59m, 59m, Line(soda, 1)));
        sold.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await sold.Json()).GetProperty("eNcf").GetString().ShouldNotBeNullOrEmpty();
        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        (await admin.GetAsync($"/api/reports/dgii/607?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}")).StatusCode.ShouldBe(HttpStatusCode.OK); // exports go on

        // Buying: the license Toki staff send, installed here; no reinstall, every sale kept, the waiting register back.
        var license = new LicenseData("Carnicería de prueba", "Negocio", [], new Dictionary<string, int> { ["Registers"] = 3 },
            app.Clock.GetUtcNow().UtcDateTime, app.Clock.GetUtcNow().UtcDateTime.AddYears(1));
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(license, app.LicenseKey) })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        features = await Features();
        (features.GetProperty("status").GetString(), features.GetProperty("warning").ValueKind, features.GetProperty("registers").GetInt32())
            .ShouldBe(("Active", JsonValueKind.Null, 3));
        (await app.SignInAsync("admin", TokiApp.AdminPin, registerB)).Token.ShouldNotBeNullOrEmpty();
        (await admin.GetFromJsonAsync<JsonElement>($"/api/reports/sales/by-day?from={today.AddDays(-30):yyyy-MM-dd}&to={today:yyyy-MM-dd}"))
            .EnumerateArray().Sum(d => d.GetProperty("saleCount").GetInt32()).ShouldBe(3);
    }

    /// <summary>The owner sells too (a cashier's 12-hour session would expire as the clock jumps days).</summary>
    private static async Task<Guid> OpenShiftAsync(HttpClient admin)
    {
        var register = (await (await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja " + Guid.NewGuid().ToString()[..4] })).Json()).GetProperty("id").GetGuid();
        var shift = await admin.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 500m });
        shift.EnsureSuccessStatusCode();
        return (await shift.Json()).GetProperty("shiftId").GetGuid();
    }

    private async Task<HttpResponseMessage> PairRawAsync(HttpClient admin)
    {
        var code = (await (await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja" })).Json()).GetProperty("code").GetString();
        return await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code });
    }

    /// <returns>The register's device secret.</returns>
    private async Task<string> PairAsync(HttpClient admin)
    {
        var r = await PairRawAsync(admin);
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("token").GetString()!;
    }

    private async Task<string?> SignInAsync(string device) =>
        await (await app.Anonymous(device).PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin = TokiApp.AdminPin })).ErrorCode();

    private Task<int> ProcessOutboxAsync() => app.InScopeAsync(sp => sp.GetRequiredService<OutboxProcessor>().ProcessAsync(CancellationToken.None));
}
