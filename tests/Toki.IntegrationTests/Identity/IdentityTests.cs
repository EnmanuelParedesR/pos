using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Identity;

public sealed class IdentityTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Setting_up_twice_fails()
    {
        await app.AdminAsync();
        var r = await app.Anonymous().PostAsJsonAsync("/api/identity/setup",
            new { AdministratorName = "Otro", AdministratorCode = "otro", Pin = "9999", DeviceName = "PC" });
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await r.ErrorCode()).ShouldBe("Identity.AlreadySetUp");
    }

    [Fact]
    public async Task The_administrator_has_every_permission()
    {
        var admin = await app.AdminAsync();
        var me = await app.Client(admin).GetFromJsonAsync<MeDto>("/api/identity/me");
        me!.Permissions.ShouldContain("users.manage");
        me.Permissions.ShouldContain("sales.void");
    }

    [Fact]
    public async Task Without_a_device_you_cannot_sign_in()
    {
        await app.AdminAsync();
        var r = await app.Anonymous().PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin = TokiApp.AdminPin });
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await r.ErrorCode()).ShouldBe("Identity.DeviceNotPaired");
    }

    [Fact]
    public async Task Five_wrong_pins_lock_even_if_the_pin_is_then_correct()
    {
        var admin = await app.AdminAsync();
        var cashier = await app.UserAsync(Roles.Cashier, pin: "5555");
        var code = await CodeOf(admin, cashier.UserId);

        for (var i = 0; i < 5; i++)
        {
            var wrong = await app.Anonymous(admin.Device).PostAsJsonAsync("/api/identity/sessions", new { Code = code, Pin = "0000" });
            (await wrong.ErrorCode()).ShouldBe("Identity.InvalidCredentials");
        }

        var right = await app.Anonymous(admin.Device).PostAsJsonAsync("/api/identity/sessions", new { Code = code, Pin = "5555" });
        right.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await right.ErrorCode()).ShouldBe("Identity.UserLocked");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task The_cashier_cannot_manage_users()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        var r = await app.Client(cashier).GetAsync("/api/identity/users");
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_a_session_it_returns_401()
    {
        var r = await app.Anonymous().GetAsync("/api/identity/me");
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pairing_a_register_and_revoking_it_closes_its_session()
    {
        var admin = await app.AdminAsync();
        var code = await (await app.Client(admin).PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja 2" })).Json();
        var pair = await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code.GetProperty("code").GetString() });
        pair.StatusCode.ShouldBe(HttpStatusCode.OK);
        var register2 = await pair.Json();
        var register2Token = register2.GetProperty("token").GetString()!;

        var session = await app.SignInAsync("admin", TokiApp.AdminPin, register2Token);
        (await app.Client(session).GetAsync("/api/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The same code cannot pair twice.
        var other = await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code.GetProperty("code").GetString() });
        other.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var revoke = await app.Client(admin).PostAsync($"/api/identity/devices/{register2.GetProperty("deviceId").GetGuid()}/revoke", null);
        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.Client(session).GetAsync("/api/identity/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_cashier_cannot_approve_as_a_supervisor()
    {
        var admin = await app.AdminAsync();
        var cashier = await app.UserAsync(Roles.Cashier, pin: "7777");
        var code = await CodeOf(admin, cashier.UserId);
        var r = await app.Client(cashier).PostAsJsonAsync("/api/identity/approvals",
            new { Code = code, Pin = "7777", Permission = "sales.void", ResourceId = Guid.NewGuid() });
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await r.ErrorCode()).ShouldBe("Identity.SupervisorLacksPermission");
    }

    [Fact]
    public async Task A_supervisor_approves_with_their_pin()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        var r = await app.Client(cashier).PostAsJsonAsync("/api/identity/approvals",
            new { Code = "admin", Pin = TokiApp.AdminPin, Permission = "sales.void", ResourceId = Guid.NewGuid() });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Json()).GetProperty("approvalId").GetGuid().ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task The_administrator_role_cannot_be_edited_and_unknown_permissions_are_rejected()
    {
        var admin = await app.AdminAsync();
        var c = app.Client(admin);
        (await c.PutAsJsonAsync($"/api/identity/roles/{Roles.Administrator}", new { Name = "X", Permissions = Array.Empty<string>() }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await c.PostAsJsonAsync("/api/identity/roles", new { Name = "Raro", Permissions = new[] { "no.existe" } }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await c.PostAsJsonAsync("/api/identity/roles", new { Name = "Inventarista", Permissions = new[] { "inventory.count" } }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    private async Task<string> CodeOf(UserSession admin, Guid userId)
    {
        var users = await app.Client(admin).GetFromJsonAsync<List<UserDto>>("/api/identity/users");
        return users!.Single(u => u.Id == userId).Code;
    }

    private sealed record MeDto(Guid UserId, string Name, List<string> Permissions);
    private sealed record UserDto(Guid Id, string Code);
}
