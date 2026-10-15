using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.CashRegister;

public sealed class CashRegisterTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<Guid> RegisterAsync(string name = "Caja")
    {
        var r = await app.Client(await app.AdminAsync()).PostAsJsonAsync("/api/cash/registers", new { Name = name + " " + Guid.NewGuid().ToString()[..4] });
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    private async Task<(HttpClient Client, Guid Shift)> ShiftAsync(decimal openingFloat = 2000m)
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        var r = await cashier.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = await RegisterAsync(), OpeningFloat = openingFloat });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (cashier, (await r.Json()).GetProperty("shiftId").GetGuid());
    }

    private static async Task<Guid> ApproveAsync(HttpClient c, string permission, Guid resource) =>
        (await (await c.PostAsJsonAsync("/api/identity/approvals", new { Code = "admin", Pin = TokiApp.AdminPin, Permission = permission, ResourceId = resource })).Json())
        .GetProperty("approvalId").GetGuid();

    [Fact]
    public async Task A_register_cannot_have_two_shifts_and_a_cashier_cannot_be_on_two_registers()
    {
        var register = await RegisterAsync();
        var ana = app.Client(await app.UserAsync(Roles.Cashier));
        var luis = app.Client(await app.UserAsync(Roles.Cashier));
        (await ana.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 1000m })).EnsureSuccessStatusCode();

        (await (await luis.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 1000m })).ErrorCode()).ShouldBe("CashRegister.RegisterInUse");
        (await (await ana.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = await RegisterAsync(), OpeningFloat = 1000m })).ErrorCode()).ShouldBe("CashRegister.CashierHasShift");

        var current = await ana.GetFromJsonAsync<JsonElement>("/api/cash/shifts/current");
        current.GetProperty("openingFloat").GetDecimal().ShouldBe(1000m);
    }

    [Fact]
    public async Task A_withdrawal_asks_for_a_supervisor()
    {
        var (cashier, shift) = await ShiftAsync(openingFloat: 1000m);

        var withoutApproval = await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementType = "Withdrawal", Amount = 500m, Reason = "CashPickup" });
        (await withoutApproval.ErrorCode()).ShouldBe("Approval.Required");

        // No "not enough cash" answer: it would let the cashier probe the expected cash and defeat the blind count.
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements",
            new { MovementType = "Withdrawal", Amount = 600m, Reason = "CashPickup", SupervisorApprovalId = await ApproveAsync(cashier, "register.withdraw", shift) }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementType = "CashIn", Amount = 100m, Reason = "ExtraChange" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var other = new { MovementType = "Expense", Amount = 10m, Reason = "Other" };
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", other)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // "Other" needs a note
    }

    [Fact]
    public async Task Closing_is_blind_a_supervisor_approves_it_and_it_gives_the_Z_report()
    {
        var (cashier, shift) = await ShiftAsync(openingFloat: 2000m);
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementType = "CashIn", Amount = 500m, Reason = "ExtraChange" }))
            .EnsureSuccessStatusCode();

        // The cashier cannot see the expected cash while the shift is open (blind count).
        (await cashier.GetAsync($"/api/cash/shifts/{shift}/z")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var x = await app.Client(await app.AdminAsync()).GetFromJsonAsync<JsonElement>($"/api/cash/shifts/{shift}/z");
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(2500m);

        var cashCount = new[] { new { Denomination = 1000m, Quantity = 2 }, new { Denomination = 200m, Quantity = 2 }, new { Denomination = 50m, Quantity = 1 } };
        (await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", new { CashCount = cashCount })).ErrorCode()).ShouldBe("Approval.Required");

        var close = await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close",
            new { CashCount = cashCount, SupervisorApprovalId = await ApproveAsync(cashier, "register.approve-close", shift) });
        close.StatusCode.ShouldBe(HttpStatusCode.OK);
        var z = await close.Json();
        z.GetProperty("expectedCash").GetDecimal().ShouldBe(2500m);
        z.GetProperty("countedCash").GetDecimal().ShouldBe(2450m);
        z.GetProperty("difference").GetDecimal().ShouldBe(-50m);
        z.GetProperty("zNumber").GetInt32().ShouldBe(1);

        // After closing: nothing else can happen on the shift, and the cashier may read their Z.
        (await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementType = "CashIn", Amount = 1m, Reason = "ExtraChange" }))
            .ErrorCode()).ShouldBe("CashRegister.ShiftClosed");
        (await cashier.GetAsync($"/api/cash/shifts/{shift}/z")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A_denomination_that_does_not_exist_is_rejected()
    {
        var (cashier, shift) = await ShiftAsync();
        var r = await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", new { CashCount = new[] { new { Denomination = 300m, Quantity = 1 } } });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_register_stays_bound_to_the_device_where_it_was_opened()
    {
        var admin = app.Client(await app.AdminAsync());
        var code = (await (await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja 2" })).Json()).GetProperty("code").GetString();
        var device2 = await (await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code })).Json();
        var user = $"u{Guid.NewGuid():N}"[..12];
        (await admin.PostAsJsonAsync("/api/identity/users", new { Name = "Luis", Code = user, Pin = "4321", RoleId = Roles.Cashier })).EnsureSuccessStatusCode();
        var onDevice2 = app.Client(await app.SignInAsync(user, "4321", device2.GetProperty("token").GetString()!));

        // The first shift (on the main PC) binds the register; a cashier on another device cannot open it or sell on it.
        var (cashier, shift) = await ShiftAsync();
        var register = (await cashier.GetFromJsonAsync<JsonElement>("/api/cash/shifts/current")).GetProperty("registerId").GetGuid();
        (await (await onDevice2.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 0m })).ErrorCode()).ShouldBe("CashRegister.OtherDevice");

        // Moving the register to the other device: the old one can no longer operate the shift.
        (await admin.PutAsJsonAsync($"/api/cash/registers/{register}/device", new { DeviceId = device2.GetProperty("deviceId").GetGuid() }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementType = "CashIn", Amount = 1m, Reason = "ExtraChange" }))
            .ErrorCode()).ShouldBe("CashRegister.OtherDevice");
    }

    [Fact]
    public async Task Another_cashier_cannot_operate_my_shift()
    {
        var (_, shift) = await ShiftAsync();
        var other = app.Client(await app.UserAsync(Roles.Cashier));
        var r = await other.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementType = "CashIn", Amount = 1m, Reason = "ExtraChange" });
        (await r.ErrorCode()).ShouldBe("CashRegister.NotYourShift");
    }
}
