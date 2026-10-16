using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Identity;

public sealed class IdentityTests(TokiApp app) : IClassFixture<TokiApp>
{
    /// <summary>TK-251: the anonymous first-run setup only answers the main PC itself.</summary>
    [Fact]
    public async Task Setup_from_another_machine_is_forbidden()
    {
        using var remote = app.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, FromTheNetwork>()));
        var r = await remote.CreateClient().PostAsJsonAsync("/api/identity/setup",
            new { AdministratorName = "Intruso", AdministratorCode = "intruso", Pin = "9999", DeviceName = "PC" });
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private sealed class FromTheNetwork : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, n) => { http.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20"); return n(); });
            next(app);
        };
    }

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
    public async Task One_register_cannot_lock_anyone_out_but_five_wrong_pins_from_two_do_even_if_the_pin_is_then_correct()
    {
        var admin = await app.AdminAsync();
        var cashier = await app.UserAsync(Roles.Cashier, pin: "5555");
        var code = await CodeOf(admin, cashier.UserId);
        Task<HttpResponseMessage> SignIn(string device, string pin) => app.Anonymous(device).PostAsJsonAsync("/api/identity/sessions", new { Code = code, Pin = pin });

        // R11 A14: after four wrong PINs this register gets no more tries for that person (the person's own count stays under the lockout).
        for (var i = 0; i < 4; i++) (await (await SignIn(admin.Device, "0000")).ErrorCode()).ShouldBe("Identity.InvalidCredentials");
        (await (await SignIn(admin.Device, "0000")).ErrorCode()).ShouldBe("Identity.TooManyAttempts");
        (await (await SignIn(admin.Device, "5555")).ErrorCode()).ShouldBe("Identity.TooManyAttempts");

        var pairing = await (await app.Client(admin).PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja 5" })).Json();
        var register5 = (await (await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = pairing.GetProperty("code").GetString() })).Json())
            .GetProperty("token").GetString()!;
        (await (await SignIn(register5, "0000")).ErrorCode()).ShouldBe("Identity.InvalidCredentials");

        var right = await SignIn(register5, "5555");
        right.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await right.ErrorCode()).ShouldBe("Identity.UserLocked");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task R11_C12_one_pairing_code_at_a_time_and_few_wrong_codes()
    {
        await using var store = new TokiApp();
        var admin = await store.AdminAsync();
        async Task<string> NewCode() => (await (await store.Client(admin).PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja" }))
            .Json()).GetProperty("code").GetString()!;
        async Task<string?> Pair(string code) => await (await store.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code })).ErrorCode();

        var first = await NewCode();
        var second = await NewCode();
        (await Pair(first)).ShouldBe("Identity.InvalidPairingCode"); // a new code ends the one before
        for (var i = 0; i < 4; i++) (await Pair("00000000")).ShouldBe("Identity.InvalidPairingCode");
        (await Pair(second)).ShouldBe("Identity.TooManyPairingAttempts"); // five wrong tries end the open code

        await NewCode(); // the owner makes another: tries start again for the code, not for this address
        for (var i = 0; i < 5; i++) (await Pair("00000000")).ShouldBe("Identity.InvalidPairingCode");
        await NewCode();
        (await Pair(await NewCode())).ShouldBe("Identity.TooManyPairingAttempts"); // ten from one address: it waits
        store.Clock.Advance(TimeSpan.FromMinutes(16));
        (await store.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = await NewCode() })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task R11_C12_a_register_that_tries_many_PINs_waits_whoever_it_tries()
    {
        await using var store = new TokiApp();
        var admin = await store.AdminAsync();
        for (var i = 0; i < 10; i++)
            (await (await store.Anonymous(admin.Device).PostAsJsonAsync("/api/identity/sessions", new { Code = $"nadie{i}", Pin = "0000" })).ErrorCode())
                .ShouldBe("Identity.InvalidCredentials");

        var locked = await store.Anonymous(admin.Device).PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin = TokiApp.AdminPin });
        (await locked.ErrorCode()).ShouldBe("Identity.DeviceLocked"); // even the right PIN: no hint of which one it was
        store.Clock.Advance(TimeSpan.FromMinutes(16));
        (await store.Anonymous(admin.Device).PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin = TokiApp.AdminPin }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
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
        // R11 A14: the same answer as a wrong PIN, so it never confirms that this PIN is right.
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await r.ErrorCode()).ShouldBe("Identity.InvalidCredentials");
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
