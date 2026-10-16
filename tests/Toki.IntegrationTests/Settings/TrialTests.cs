using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Messaging;
using Toki.IntegrationTests.Infra;
using Toki.BuildingBlocks.Security;
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
        features.GetProperty("enabled").GetArrayLength().ShouldBe(Enum.GetValues<Toki.BuildingBlocks.Security.Feature>().Length); // all on in the trial
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

/// <summary>R9 #13: a register beyond the license cannot sign in, and its open shift is closed from the main PC. Own app.</summary>
public sealed class UnlicensedRegisterShiftTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task The_main_PC_closes_the_open_shift_of_a_register_the_license_no_longer_covers()
    {
        var admin = app.Client(await app.AdminAsync());
        async Task<string> PairAsync()
        {
            app.Clock.Advance(TimeSpan.FromMinutes(1)); // registers count in the order they were paired
            var code = (await (await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja" })).Json()).GetProperty("code").GetString();
            return (await (await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code })).Json()).GetProperty("token").GetString()!;
        }
        await PairAsync();
        var last = await PairAsync();
        (await admin.PostAsJsonAsync("/api/identity/users", new { Name = "Cajera", Code = "c7", Pin = "4321", RoleId = Roles.Cashier })).EnsureSuccessStatusCode();
        var cashier = app.Client(await app.SignInAsync("c7", "4321", last));
        var register = (await (await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja 3" })).Json()).GetProperty("id").GetGuid();
        var shift = (await (await cashier.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 500m })).Json()).GetProperty("shiftId").GetGuid();

        // TK-258: plan Esencial, no register besides the main PC.
        var license = new LicenseData("Carnicería de prueba", "Esencial", [], new Dictionary<string, int> { ["Registers"] = 0 },
            app.Clock.GetUtcNow().UtcDateTime, app.Clock.GetUtcNow().UtcDateTime.AddYears(1));
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(license, app.LicenseKey) })).EnsureSuccessStatusCode();
        var refused = await app.Anonymous(last).PostAsJsonAsync("/api/identity/sessions", new { Code = "c7", Pin = "4321" });
        (await refused.ErrorCode()).ShouldBe("Identity.RegisterNotLicensed");
        (await refused.Content.ReadAsStringAsync()).ShouldContain("Turnos y cuadres");
        admin = app.Client(await app.SignInAsync("admin", TokiApp.AdminPin, (await app.AdminAsync()).Device)); // the main PC still signs in and sells
        var code = (await (await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja" })).Json()).GetProperty("code").GetString();
        (await (await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code })).ErrorCode()).ShouldBe("Identity.RegisterLimitReached");

        var closed = await admin.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", new { CashCount = new[] { new { Denomination = 500m, Quantity = 1 } } });
        closed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await closed.Json()).GetProperty("difference").GetDecimal().ShouldBe(0m);
    }
}

/// <summary>R9 #12: a sale with the sample products or in a DGII test environment does not start the 14 days. Own app.</summary>
public sealed class TrialRealSaleTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Only_a_real_sale_starts_the_trial()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = app.Client(await app.AdminAsync());
        var category = await admin.CategoryAsync();
        var sample = await admin.ProductAsync(category, "Refresco de ejemplo", 59m, stock: 10);
        var real = await admin.ProductAsync(category, "Refresco", 59m, stock: 10);
        await app.InScopeAsync(sp => sp.GetRequiredService<Toki.Inventory.Persistence.InventoryDbContext>().Products.Where(p => p.Id == sample)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsSample, true)));
        async Task<bool> Started()
        {
            await app.InScopeAsync(sp => sp.GetRequiredService<OutboxProcessor>().ProcessAsync(CancellationToken.None));
            return (await admin.GetFromJsonAsync<JsonElement>("/api/settings/features")).GetProperty("trialEndsUtc").ValueKind != JsonValueKind.Null;
        }

        (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(sample, 1)))).EnsureSuccessStatusCode();
        (await Started()).ShouldBeFalse();

        // A sale of the DGII certification (CerteCF).
        var certification = await (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(real, 1)))).Json();
        await app.InScopeAsync(sp => sp.GetRequiredService<Toki.Fiscal.FiscalDbContext>().Documents.Where(d => d.ENcf == certification.GetProperty("eNcf").GetString())
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Environment, Toki.Fiscal.Domain.FiscalEnvironment.CerteCF)));
        (await Started()).ShouldBeFalse();

        (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(real, 1)))).EnsureSuccessStatusCode();
        (await Started()).ShouldBeTrue();
    }
}
