using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Identity.Persistence;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Identity;

/// <summary>Session lifecycle and user rules. Own app because one test moves the clock 12 hours.</summary>
public sealed class SessionsTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private async Task<string> CodeOf(Guid userId)
    {
        var users = await app.Client(await app.AdminAsync()).GetFromJsonAsync<List<JsonElement>>("/api/identity/users");
        return users!.Single(u => u.GetProperty("id").GetGuid() == userId).GetProperty("code").GetString()!;
    }

    private static async Task<HttpStatusCode> Me(HttpClient c) => (await c.GetAsync("/api/identity/me")).StatusCode;

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        var c = app.Client(cashier);
        (await c.DeleteAsync("/api/identity/sessions/current")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Me(c)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Deactivating_a_user_cuts_their_session_at_once()
    {
        var admin = app.Client(await app.AdminAsync());
        var cashier = await app.UserAsync(Roles.Cashier);
        (await admin.PutAsJsonAsync($"/api/identity/users/{cashier.UserId}", new { Name = "Ex cajero", RoleId = Roles.Cashier, IsActive = false }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Me(app.Client(cashier))).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_administrator_cannot_deactivate_themselves()
    {
        var admin = await app.AdminAsync();
        var r = await app.Client(admin).PutAsJsonAsync($"/api/identity/users/{admin.UserId}", new { Name = "Dueña", RoleId = Roles.Administrator, IsActive = false });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_supervisor_cannot_grant_the_administrator_role()
    {
        var admin = app.Client(await app.AdminAsync());
        var role = await (await admin.PostAsJsonAsync("/api/identity/roles", new { Name = "Encargado", Permissions = new[] { "users.manage" } })).Json();
        var manager = app.Client(await app.UserAsync(role.GetProperty("id").GetGuid()));
        var r = await manager.PostAsJsonAsync("/api/identity/users", new { Name = "Nuevo jefe", Code = "jefe", Pin = "1111", RoleId = Roles.Administrator });
        (await r.ErrorCode()).ShouldBe("Identity.AdministratorOnly");
    }

    [Fact]
    public async Task Changing_the_role_takes_effect_on_the_next_request()
    {
        var admin = app.Client(await app.AdminAsync());
        var cashier = await app.UserAsync(Roles.StockKeeper); // has inventory.view (the seeded Cajero no longer does, R11 A9)
        var c = app.Client(cashier);
        (await c.GetAsync("/api/inventory/stock")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var role = await (await admin.PostAsJsonAsync("/api/identity/roles", new { Name = "Solo vende", Permissions = new[] { "sales.create" } })).Json();
        (await admin.PutAsJsonAsync($"/api/identity/users/{cashier.UserId}", new { Name = "Cajero", RoleId = role.GetProperty("id").GetGuid(), IsActive = true }))
            .EnsureSuccessStatusCode();
        (await c.GetAsync("/api/inventory/stock")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_code_is_case_insensitive()
    {
        var admin = await app.AdminAsync();
        (await app.SignInAsync("ADMIN", AbastaliaApp.AdminPin, admin.Device)).Token.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Wrong_pins_in_parallel_are_not_lost_and_the_audit_stays_intact()
    {
        var admin = await app.AdminAsync();
        var cashier = await app.UserAsync(Roles.Cashier, pin: "8642");
        var code = await CodeOf(cashier.UserId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            app.Anonymous(admin.Device).PostAsJsonAsync("/api/identity/sessions", new { Code = code, Pin = "0000" })));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Unauthorized || r.StatusCode == HttpStatusCode.Conflict);
        var counted = 0;
        foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.Unauthorized))
            if (await r.ErrorCode() == "Identity.InvalidCredentials") counted++;

        var attempts = await app.InScopeAsync(sp => sp.GetRequiredService<IdentityDbContext>().Users
            .Where(u => u.Id == cashier.UserId).Select(u => u.FailedAttempts).SingleAsync());
        attempts.ShouldBe(counted); // every reported failure was counted: no lost updates
        (await app.VerifyAuditAsync()).ShouldBeNull();
        var sequences = await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.Select(a => a.Sequence).OrderBy(s => s).ToListAsync());
        sequences.ShouldBe(Enumerable.Range(1, sequences.Count).Select(i => (long)i));
        app.Clock.Advance(TimeSpan.FromMinutes(16)); // R11 C12: after so many wrong PINs this register waits; the other tests use it
    }

    [Fact]
    public async Task A_pairing_code_used_in_parallel_pairs_only_one_register()
    {
        var admin = app.Client(await app.AdminAsync());
        var code = (await (await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja X" })).Json()).GetProperty("code").GetString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code })));
        responses.Count(r => r.IsSuccessStatusCode).ShouldBe(1);
    }

    [Fact]
    public async Task Pairing_without_a_code_is_400() =>
        (await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

}

/// <summary>Own app: it moves the clock 12 hours.</summary>
public sealed class SessionExpiryTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static async Task<HttpStatusCode> Me(HttpClient c) => (await c.GetAsync("/api/identity/me")).StatusCode;

    [Fact]
    public async Task The_session_expires_after_12_hours()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        var c = app.Client(cashier);
        app.Clock.Advance(TimeSpan.FromHours(11));
        (await Me(c)).ShouldBe(HttpStatusCode.OK);
        app.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        (await Me(c)).ShouldBe(HttpStatusCode.Unauthorized);
    }
}

/// <summary>Setup runs once even when two requests race on a new database.</summary>
public sealed class SetupTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task Two_setups_at_once_create_a_single_administrator()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => app.Anonymous().PostAsJsonAsync("/api/identity/setup",
            new { AdministratorName = "Dueño " + i, AdministratorCode = "a" + i, Pin = "1234", DeviceName = "PC" })));
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
    }
}
