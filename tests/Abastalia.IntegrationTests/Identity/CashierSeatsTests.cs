using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Abastalia.BuildingBlocks.Security;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Identity;

/// <summary>TK-277: the license's cashiers cap only enabling one more; signing in and selling never stop. Own app: it installs licenses.</summary>
public sealed class CashierSeatsTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static async Task<HttpResponseMessage> CreateAsync(HttpClient admin, string code, Guid role) =>
        await admin.PostAsJsonAsync("/api/identity/users", new { Name = "Persona " + code, Code = code, Pin = "4321", RoleId = role });

    private async Task InstallAsync(HttpClient admin, int? cashiers, DateTime expires)
    {
        var limits = new Dictionary<string, int> { ["Registers"] = 2 };
        if (cashiers is { } n) limits["Cashiers"] = n;
        var now = app.Clock.GetUtcNow().UtcDateTime;
        app.Clock.Advance(TimeSpan.FromSeconds(1)); // each license issued after the last
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(new LicenseData("Colmado", "Estándar", [], limits, now, expires), app.LicenseKey) }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Only_enabling_one_cashier_too_many_is_stopped()
    {
        var admin = app.Client(await app.AdminAsync());
        var year = app.Clock.GetUtcNow().UtcDateTime.AddYears(1);

        // The trial has no limit; a license that does not say (issued before TK-277) neither.
        foreach (var code in new[] { "c1", "c2", "c3" }) (await CreateAsync(admin, code, Roles.Cashier)).EnsureSuccessStatusCode();
        await InstallAsync(admin, null, year);
        (await CreateAsync(admin, "c4", Roles.CashierWithoutOpening)).EnsureSuccessStatusCode();

        // Plan with 4 cashiers: the owner (Administrador) and someone who cannot sell (Contador) do not count.
        await InstallAsync(admin, 4, year);
        var full = await CreateAsync(admin, "c5", Roles.Cashier);
        full.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await full.ErrorCode()).ShouldBe("Identity.CashierLimitReached");
        (await full.Content.ReadAsStringAsync()).ShouldContain("Plan y facturación");
        (await CreateAsync(admin, "cont", Roles.Accountant)).EnsureSuccessStatusCode();
        var seats = await admin.GetFromJsonAsync<JsonElement>("/api/identity/cashier-seats"); // Configuración → Usuarios shows it
        (seats.GetProperty("used").GetInt32(), seats.GetProperty("limit").GetInt32()).ShouldBe((4, 4));

        var users = (await admin.GetFromJsonAsync<List<JsonElement>>("/api/identity/users"))!;
        Guid Id(string code) => users.Single(u => string.Equals(u.GetProperty("code").GetString(), code, StringComparison.OrdinalIgnoreCase)).GetProperty("id").GetGuid();
        Task<HttpResponseMessage> UpdateAsync(string code, Guid role, bool active) =>
            admin.PutAsJsonAsync($"/api/identity/users/{Id(code)}", new { Name = "Persona " + code, RoleId = role, IsActive = active });

        // The accountant made a cashier, or a role that starts selling: also one too many.
        (await (await UpdateAsync("cont", Roles.Cashier, true)).ErrorCode()).ShouldBe("Identity.CashierLimitReached");
        var role = (await (await admin.PostAsJsonAsync("/api/identity/roles", new { Name = "Ayudante", Permissions = new[] { Permissions.ReportsView } })).Json()).GetProperty("id").GetGuid();
        (await UpdateAsync("cont", role, true)).EnsureSuccessStatusCode();
        (await (await admin.PutAsJsonAsync($"/api/identity/roles/{role}", new { Name = "Ayudante", Permissions = new[] { Permissions.ReportsView, Permissions.SalesCreate } }))
            .ErrorCode()).ShouldBe("Identity.CashierLimitReached");

        // Disabling one frees the seat (its history stays); enabling it again needs one.
        (await UpdateAsync("c1", Roles.Cashier, false)).EnsureSuccessStatusCode();
        (await CreateAsync(admin, "c5", Roles.Cashier)).EnsureSuccessStatusCode();
        (await (await UpdateAsync("c1", Roles.Cashier, true)).ErrorCode()).ShouldBe("Identity.CashierLimitReached");
        (await UpdateAsync("c2", Roles.Cashier, true)).EnsureSuccessStatusCode(); // editing an enabled cashier is fine

        // Over the limit (a smaller plan): everyone still signs in and sells.
        await InstallAsync(admin, 1, year);
        (await app.SignInAsync("c3", "4321", (await app.AdminAsync()).Device)).Token.ShouldNotBeNullOrEmpty();

        // Restricted (the license ran out long ago): the smallest plan's 2.
        await InstallAsync(admin, 50, app.Clock.GetUtcNow().UtcDateTime.AddDays(-30));
        admin = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, (await app.AdminAsync()).Device));
        var restricted = await CreateAsync(admin, "c6", Roles.Cashier);
        (await restricted.ErrorCode()).ShouldBe("Identity.CashierLimitReached");
        (await restricted.Content.ReadAsStringAsync()).ShouldContain("Sin licencia vigente, Abastalia permite 2 cajeros");
    }
}

/// <summary>TK-277: one PIN used at two registers at once is a warning (Resumen and Salud), never a block. Own app: it moves the clock.</summary>
public sealed class SharedPinTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task Working_at_two_registers_at_once_is_flagged_and_moving_between_them_is_not()
    {
        var admin = app.Client(await app.AdminAsync());
        var main = (await app.AdminAsync()).Device;
        var code = (await (await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja 2" })).Json()).GetProperty("code").GetString();
        var register = (await (await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code })).Json()).GetProperty("token").GetString()!;
        foreach (var c in new[] { "ana", "luis" })
            (await admin.PostAsJsonAsync("/api/identity/users", new { Name = c, Code = c, Pin = "4321", RoleId = Roles.Cashier })).EnsureSuccessStatusCode();
        async Task<List<JsonElement>> FlaggedAsync() => (await admin.GetFromJsonAsync<List<JsonElement>>("/api/identity/shared-pins"))!;

        // Luis moves from the main PC to the register: the first session is not used again.
        await app.SignInAsync("luis", "4321", main);
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await app.SignInAsync("luis", "4321", register);

        // Ana's PIN is used at the register while she keeps working at the main PC.
        var ana = app.Client(await app.SignInAsync("ana", "4321", main));
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await app.SignInAsync("ana", "4321", register);
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        (await ana.GetAsync("/api/identity/me")).EnsureSuccessStatusCode();

        admin = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, main));
        var flagged = await FlaggedAsync();
        flagged.Select(f => (f.GetProperty("name").GetString(), f.GetProperty("times").GetInt32())).ShouldBe([("ana", 1)]);
        var health = await admin.GetFromJsonAsync<List<JsonElement>>("/api/settings/health");
        var check = health!.Single(c => c.GetProperty("name").GetString() == "shared-pin");
        (check.GetProperty("status").GetString(), check.GetProperty("title").GetString()).ShouldBe(("Degraded", "PIN compartido"));

        // A week later it is no longer news.
        app.Clock.Advance(TimeSpan.FromDays(8));
        admin = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, main));
        (await FlaggedAsync()).ShouldBeEmpty();
    }
}
