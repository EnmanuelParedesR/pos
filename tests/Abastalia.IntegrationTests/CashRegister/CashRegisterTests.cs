using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.CashRegister;

public sealed class CashRegisterTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
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

    private static Task<Guid> ApproveAsync(HttpClient c, string permission, Guid resource, string summary = "") => c.ApproveAsync(permission, resource, summary);

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

        var withoutApproval = await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementId = Guid.NewGuid(), MovementType = "Withdrawal", Amount = 500m, Reason = "CashPickup" });
        var problem = await withoutApproval.Json();
        problem.GetProperty("code").GetString().ShouldBe("Approval.Required");
        // The register reads the permission from this field, not from the text.
        problem.GetProperty("errors").GetProperty("permission")[0].GetString().ShouldBe("register.withdraw");

        // No "not enough cash" answer: it would let the cashier probe the expected cash and defeat the blind count.
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements",
            new { MovementId = Guid.NewGuid(), MovementType = "Withdrawal", Amount = 600m, Reason = "CashPickup",
                SupervisorApprovalId = await ApproveAsync(cashier, "register.withdraw", shift, "Retiro de efectivo de RD$ 600.00 · recogida de efectivo") }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementId = Guid.NewGuid(), MovementType = "CashIn", Amount = 100m, Reason = "ExtraChange" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var other = new { MovementId = Guid.NewGuid(), MovementType = "Expense", Amount = 10m, Reason = "Other" };
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", other)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // "Other" needs a note
        // TK-251: no coins under RD$ 1, so drawer cash moves in whole pesos (like customer payments).
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementId = Guid.NewGuid(), MovementType = "CashIn", Amount = 100.50m, Reason = "ExtraChange" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_opening_float_is_in_whole_pesos()
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = await RegisterAsync(), OpeningFloat = 1000.25m })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Opening_the_drawer_without_a_sale_needs_a_reason_and_the_permission_and_shows_on_the_Z()
    {
        // TK-239: the Cajero role lacks register.open-drawer, so a supervisor approves; the opening never changes the expected cash.
        var (cashier, shift) = await ShiftAsync(openingFloat: 1000m);
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/drawer-openings", new { Reason = "" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var refused = await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/drawer-openings", new { Reason = "Cambio para un cliente" })).Json();
        refused.GetProperty("errors").GetProperty("permission")[0].GetString().ShouldBe("register.open-drawer");

        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/drawer-openings",
            new { Reason = "Cambio para un cliente", SupervisorApprovalId = await ApproveAsync(cashier, "register.open-drawer", shift) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var admin = app.Client(await app.AdminAsync());
        (await admin.PostAsJsonAsync($"/api/cash/shifts/{shift}/drawer-openings", new { Reason = "Revisar billetes" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var x = await admin.GetFromJsonAsync<JsonElement>($"/api/cash/shifts/{shift}/z");
        x.GetProperty("drawerOpenings").GetInt32().ShouldBe(2);
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(1000m);
        var today = Abastalia.SharedKernel.BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime);
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/audit?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}&action=cash.open-drawer");
        audit.EnumerateArray().Count(r => r.GetProperty("resourceId").GetGuid() == shift).ShouldBe(2);
    }

    [Fact]
    public async Task A_movement_sent_twice_with_the_same_id_is_recorded_once()
    {
        var (cashier, shift) = await ShiftAsync(openingFloat: 1000m);
        var cashIn = new { MovementId = Guid.CreateVersion7(), MovementType = "CashIn", Amount = 300m, Reason = "ExtraChange" };
        var first = await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", cashIn)).Json();
        var retry = await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", cashIn)).Json();
        retry.GetProperty("movementId").GetGuid().ShouldBe(first.GetProperty("movementId").GetGuid());
        first.GetProperty("movementId").GetGuid().ShouldBe(cashIn.MovementId);

        // A withdrawal retried after a timeout: the supervisor approves again, but the cash leaves the drawer once.
        var drop = Guid.CreateVersion7();
        foreach (var _ in new[] { 1, 2 })
            (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements",
                new { MovementId = drop, MovementType = "Withdrawal", Amount = 500m, Reason = "CashPickup",
                    SupervisorApprovalId = await ApproveAsync(cashier, "register.withdraw", shift, "Retiro de efectivo de RD$ 500.00 · recogida de efectivo") }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

        var x = await app.Client(await app.AdminAsync()).GetFromJsonAsync<JsonElement>($"/api/cash/shifts/{shift}/z");
        x.GetProperty("cashIn").GetDecimal().ShouldBe(300m);
        x.GetProperty("withdrawals").GetDecimal().ShouldBe(500m);
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(800m);
    }

    [Fact]
    public async Task Closing_is_blind_a_supervisor_approves_it_and_it_gives_the_Z_report()
    {
        var (cashier, shift) = await ShiftAsync(openingFloat: 2000m);
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementId = Guid.NewGuid(), MovementType = "CashIn", Amount = 500m, Reason = "ExtraChange" }))
            .EnsureSuccessStatusCode();

        // The cashier cannot see the expected cash while the shift is open (blind count).
        (await cashier.GetAsync($"/api/cash/shifts/{shift}/z")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var x = await app.Client(await app.AdminAsync()).GetFromJsonAsync<JsonElement>($"/api/cash/shifts/{shift}/z");
        x.GetProperty("expectedCash").GetDecimal().ShouldBe(2500m);

        var cashCount = new[] { new { Denomination = 1000m, Quantity = 2 }, new { Denomination = 200m, Quantity = 2 }, new { Denomination = 50m, Quantity = 1 } };
        (await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", new { CashCount = cashCount })).ErrorCode()).ShouldBe("Approval.Required");

        var close = await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close",
            new { CashCount = cashCount, SupervisorApprovalId = await ApproveAsync(cashier, "register.approve-close", shift, "Cerrar el turno con RD$ 2,450.00 contados en efectivo") });
        close.StatusCode.ShouldBe(HttpStatusCode.OK);
        var z = await close.Json();
        z.GetProperty("expectedCash").GetDecimal().ShouldBe(2500m);
        z.GetProperty("countedCash").GetDecimal().ShouldBe(2450m);
        z.GetProperty("difference").GetDecimal().ShouldBe(-50m);
        z.GetProperty("zNumber").GetInt32().ShouldBe(1);

        // After closing: nothing else can happen on the shift, and the cashier may read their Z.
        (await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementId = Guid.NewGuid(), MovementType = "CashIn", Amount = 1m, Reason = "ExtraChange" }))
            .ErrorCode()).ShouldBe("CashRegister.ShiftClosed");
        (await cashier.GetAsync($"/api/cash/shifts/{shift}/z")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task After_three_counts_off_every_count_needs_a_supervisor_so_the_blind_count_cannot_be_probed()
    {
        // R8 #19: expected 2000. Without a limit, trying counts until one closes without a supervisor finds the expected cash.
        var (cashier, shift) = await ShiftAsync(openingFloat: 2000m);
        object Count(int thousands) => new { CashCount = new[] { new { Denomination = 1000m, Quantity = thousands } } };
        foreach (var wrong in new[] { 1, 3, 4 })
            (await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", Count(wrong))).ErrorCode()).ShouldBe("Approval.Required");

        (await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", Count(2))).ErrorCode()).ShouldBe("Approval.Required"); // right, but no longer telling
        var close = await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close",
            new { CashCount = new[] { new { Denomination = 1000m, Quantity = 2 } },
                SupervisorApprovalId = await ApproveAsync(cashier, "register.approve-close", shift, "Cerrar el turno con RD$ 2,000.00 contados en efectivo") });
        (await close.Json()).GetProperty("difference").GetDecimal().ShouldBe(0m);

        // Another shift with the right count at once closes alone.
        var (other, otherShift) = await ShiftAsync(openingFloat: 2000m);
        (await other.PostAsJsonAsync($"/api/cash/shifts/{otherShift}/close", Count(2))).StatusCode.ShouldBe(HttpStatusCode.OK);
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
        (await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementId = Guid.NewGuid(), MovementType = "CashIn", Amount = 1m, Reason = "ExtraChange" }))
            .ErrorCode()).ShouldBe("CashRegister.OtherDevice");
    }

    [Fact]
    public async Task Another_cashier_cannot_operate_my_shift()
    {
        var (_, shift) = await ShiftAsync();
        var other = app.Client(await app.UserAsync(Roles.Cashier));
        var r = await other.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new { MovementId = Guid.NewGuid(), MovementType = "CashIn", Amount = 1m, Reason = "ExtraChange" });
        (await r.ErrorCode()).ShouldBe("CashRegister.NotYourShift");
    }

    [Fact]
    public async Task Renaming_a_register_sends_RegisterChangedV1_in_the_same_transaction()
    {
        var register = await RegisterAsync();
        var admin = app.Client(await app.AdminAsync());

        (await admin.PutAsJsonAsync($"/api/cash/registers/{register}", new { Name = "  Caja del frente  " })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await admin.GetFromJsonAsync<JsonElement>("/api/cash/registers")).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == register)
            .GetProperty("name").GetString().ShouldBe("Caja del frente");
        var events = await app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.BuildingBlocks.Persistence.InfraDbContext>().Outbox
            .Where(m => m.Type == typeof(Abastalia.CashRegister.Contracts.RegisterChangedV1).FullName).OrderBy(m => m.Sequence).Select(m => m.Payload).ToListAsync());
        events.Select(p => JsonSerializer.Deserialize<Abastalia.CashRegister.Contracts.RegisterChangedV1>(p, Abastalia.BuildingBlocks.Persistence.Json.Options)!)
            .Where(e => e.RegisterId == register).Select(e => e.Name).Skip(1).ShouldBe(["Caja del frente"]); // the first is the creation
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A_register_cannot_be_renamed_to_nothing_or_by_a_cashier()
    {
        var register = await RegisterAsync();
        (await app.Client(await app.AdminAsync()).PutAsJsonAsync($"/api/cash/registers/{register}", new { Name = " " })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await app.Client(await app.UserAsync(Roles.Cashier)).PutAsJsonAsync($"/api/cash/registers/{register}", new { Name = "Mía" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await app.Client(await app.AdminAsync()).PutAsJsonAsync($"/api/cash/registers/{Guid.NewGuid()}", new { Name = "Caja 9" })).ErrorCode()).ShouldBe("CashRegister.RegisterNotFound");
    }
}
