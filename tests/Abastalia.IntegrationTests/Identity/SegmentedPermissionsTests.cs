using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Identity;

/// <summary>M16 (TK-212..216, docs/permisos.md): each new permission opens its door and the old wide one no longer does; the cashier's own closing;
/// nobody hands out what they do not have. Own app: the tests change the store's approval policy.</summary>
public sealed class SegmentedPermissionsTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private const string Period = "from=2026-10-01&to=2026-10-15";
    private static readonly object[] OneThousand = [new { Denomination = 1000m, Quantity = 1 }];

    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    private async Task<Guid> RoleAsync(params string[] permissions)
    {
        var r = await (await Admin()).PostAsJsonAsync("/api/identity/roles", new { Name = "Rol " + Guid.NewGuid().ToString()[..6], Permissions = permissions });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> WithAsync(params string[] permissions) => app.Client(await app.UserAsync(await RoleAsync(permissions)));

    private async Task<string> RegisterNameAsync(Guid register) =>
        (await (await Admin()).GetFromJsonAsync<JsonElement>("/api/cash/registers")).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == register)
        .GetProperty("name").GetString()!;

    private async Task<Guid> RegisterAsync() =>
        (await (await (await Admin()).PostAsJsonAsync("/api/cash/registers", new { Name = "Caja " + Guid.NewGuid().ToString()[..4] })).Json()).GetProperty("id").GetGuid();

    private async Task SetPolicyAsync(params string[] required) =>
        (await (await Admin()).PutAsJsonAsync("/api/settings/approvals", new { Required = required })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private static async Task ShouldNeedAsync(HttpResponseMessage r, string permission)
    {
        var problem = await r.Json();
        problem.GetProperty("code").GetString().ShouldBe("Approval.Required");
        problem.GetProperty("errors").GetProperty("permission")[0].GetString().ShouldBe(permission);
    }

    private async Task<(HttpClient Cashier, Guid Shift)> OpenAsync(Guid role, decimal openingFloat = 1000m)
    {
        var cashier = app.Client(await app.UserAsync(role));
        var register = await RegisterAsync();
        var r = await cashier.SupervisedAsync(register,
            approval => cashier.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = openingFloat, SupervisorApprovalId = approval }));
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (cashier, (await r.Json()).GetProperty("shiftId").GetGuid());
    }

    public static TheoryData<string, string, string> Doors() => new()
    {
        { "/api/backups", "backups.manage", "settings.manage" },
        { "/api/audit/verify", "audit.view", "settings.manage" },
        { $"/api/cash/shifts?{Period}", "shifts.view", "reports.view" },
        { $"/api/reports/shifts?{Period}", "shifts.view", "reports.view" },
        { $"/api/reports/dgii/607?{Period}", "fiscal.reports", "reports.view" },
        { $"/api/reports/dgii/606?{Period}", "fiscal.reports+costs.view", "fiscal.manage" }, // R11 A12: the 606 is what purchases cost
        { $"/api/reports/sales/by-day?{Period}", "reports.view", "fiscal.reports" },
        { $"/api/purchasing/expenses?{Period}", "payables.pay", "purchasing.manage" },
    };

    [Theory]
    [MemberData(nameof(Doors))]
    public async Task Each_new_permission_opens_its_page_and_the_old_wide_one_no_longer_does(string url, string permission, string other)
    {
        (await (await WithAsync(permission.Split('+'))).GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await WithAsync(other)).GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Registers_take_registers_manage_and_paying_a_supplier_takes_payables_pay()
    {
        (await (await WithAsync("registers.manage")).PostAsJsonAsync("/api/cash/registers", new { Name = "Caja nueva" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await WithAsync("settings.manage")).PostAsJsonAsync("/api/cash/registers", new { Name = "Caja otra" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var payment = new { SupplierId = Guid.NewGuid(), Amount = 100m, Method = "Transfer", Date = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4)) };
        (await (await WithAsync("purchasing.manage")).PostAsJsonAsync("/api/purchasing/payments", payment)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await WithAsync("payables.pay")).PostAsJsonAsync("/api/purchasing/payments", payment)).StatusCode.ShouldBe(HttpStatusCode.NotFound); // past the door: no such supplier
    }

    [Fact]
    public async Task Collecting_a_customer_payment_takes_credit_collect()
    {
        var body = new { CustomerPaymentId = Guid.CreateVersion7(), ShiftId = Guid.NewGuid(), Amount = 100m, Method = "Cash" };
        (await (await WithAsync("register.open", "register.close", "register.cash-in", "customers.manage")).PostAsJsonAsync($"/api/customers/{Guid.NewGuid()}/payments", body))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await WithAsync("credit.collect")).PostAsJsonAsync($"/api/customers/{Guid.NewGuid()}/payments", body)).StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_cashier_without_register_open_needs_a_supervisor_and_the_shift_is_still_theirs()
    {
        var cashier = app.Client(await app.UserAsync(Roles.CashierWithoutOpening));
        var register = await RegisterAsync();
        await ShouldNeedAsync(await cashier.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 500m }), "register.open");

        var opened = await cashier.PostAsJsonAsync("/api/cash/shifts",
            new { RegisterId = register, OpeningFloat = 500m, SupervisorApprovalId = await cashier.ApproveAsync("register.open", register, $"Abrir {await RegisterNameAsync(register)} con un fondo de RD$ 500.00") });
        opened.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await cashier.GetFromJsonAsync<JsonElement>("/api/cash/shifts/current")).GetProperty("registerId").GetGuid().ShouldBe(register);
    }

    [Fact]
    public async Task Cash_in_takes_register_cash_in_or_a_supervisor()
    {
        var (cashier, shift) = await OpenAsync(await RoleAsync("sales.create", "register.open", "register.close"));
        var cashIn = new { MovementId = Guid.CreateVersion7(), MovementType = "CashIn", Amount = 100m, Reason = "ExtraChange" };
        await ShouldNeedAsync(await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", cashIn), "register.cash-in");
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/movements", new
        {
            cashIn.MovementId, cashIn.MovementType, cashIn.Amount, cashIn.Reason,
            SupervisorApprovalId = await cashier.ApproveAsync("register.cash-in", shift, "Entrada de efectivo de RD$ 100.00 · cambio"),
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_cashier_closes_their_own_shift_alone_when_the_count_matches()
    {
        await SetPolicyAsync("register.approve-close");
        var (cashier, shift) = await OpenAsync(Roles.CashierWithoutOpening);

        var close = await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", new { CashCount = OneThousand });
        close.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await close.Json()).GetProperty("difference").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    public async Task A_closing_with_a_difference_needs_approve_close_only_when_the_policy_asks()
    {
        await SetPolicyAsync("register.approve-close");
        var (cashier, shift) = await OpenAsync(Roles.CashierWithoutOpening);
        var short500 = new[] { new { Denomination = 500m, Quantity = 1 } };
        await ShouldNeedAsync(await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", new { CashCount = short500 }), "register.approve-close");
        var approved = await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close",
            new { CashCount = short500, SupervisorApprovalId = await cashier.ApproveAsync("register.approve-close", shift, "Cerrar el turno con RD$ 500.00 contados en efectivo") });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await approved.Json()).GetProperty("difference").GetDecimal().ShouldBe(-500m);

        await SetPolicyAsync();
        var (alone, other) = await OpenAsync(Roles.CashierWithoutOpening);
        (await alone.PostAsJsonAsync($"/api/cash/shifts/{other}/close", new { CashCount = short500 })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_register_close_a_supervisor_authorizes_the_closing()
    {
        await SetPolicyAsync("register.approve-close");
        var (cashier, shift) = await OpenAsync(await RoleAsync("sales.create", "register.open"));
        await ShouldNeedAsync(await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", new { CashCount = OneThousand }), "register.close");
        (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close",
            new { CashCount = OneThousand, SupervisorApprovalId = await cashier.ApproveAsync("register.close", shift, "Cerrar el turno con RD$ 1,000.00 contados en efectivo") }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Nobody_hands_out_a_permission_they_do_not_have()
    {
        var manager = await WithAsync("users.manage", "sales.create", "reports.view");
        var tooMuch = await manager.PostAsJsonAsync("/api/identity/roles", new { Name = "Encargado", Permissions = new[] { "sales.create", "costs.view" } });
        tooMuch.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await tooMuch.ErrorCode()).ShouldBe("Identity.PermissionsNotHeld");
        var mine = await manager.PostAsJsonAsync("/api/identity/roles", new { Name = "Vendedor", Permissions = new[] { "sales.create" } });
        mine.StatusCode.ShouldBe(HttpStatusCode.Created);

        // A role the owner gave costs.view: the manager may rename it and keep it, but not add settings.manage.
        var owners = await RoleAsync("sales.create", "costs.view");
        (await manager.PutAsJsonAsync($"/api/identity/roles/{owners}", new { Name = "Vendedor con costos", Permissions = new[] { "sales.create", "costs.view" } }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await manager.PutAsJsonAsync($"/api/identity/roles/{owners}", new { Name = "Otro", Permissions = new[] { "costs.view", "settings.manage" } }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Nor a user with a role above them, nor a new PIN for one (that would let them sign in as that user).
        (await manager.PostAsJsonAsync("/api/identity/users", new { Name = "Sup", Code = "sup" + Guid.NewGuid().ToString()[..4], Pin = "1111", RoleId = Roles.Supervisor }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await manager.PostAsJsonAsync("/api/identity/users", new { Name = "Vende", Code = "ven" + Guid.NewGuid().ToString()[..4], Pin = "1111", RoleId = (await mine.Json()).GetProperty("id").GetGuid() }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var supervisor = await app.UserAsync(Roles.Supervisor);
        (await manager.PutAsJsonAsync($"/api/identity/users/{supervisor.UserId}", new { Name = "Supervisor", RoleId = Roles.Supervisor, IsActive = true, Pin = "2222" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_role_in_use_is_not_deleted_and_changes_are_audited_with_before_and_after()
    {
        var admin = await Admin();
        var used = await RoleAsync("sales.create");
        var user = await app.UserAsync(used);
        (await (await admin.DeleteAsync($"/api/identity/roles/{used}")).ErrorCode()).ShouldBe("Identity.RoleInUse");
        (await admin.DeleteAsync($"/api/identity/roles/{Roles.Administrator}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await admin.PutAsJsonAsync($"/api/identity/users/{user.UserId}", new { Name = "Cambiada", RoleId = Roles.Cashier, IsActive = true }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"/api/identity/roles/{used}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetFromJsonAsync<JsonElement>("/api/identity/roles")).GetProperty("roles").EnumerateArray().ShouldNotContain(r => r.GetProperty("id").GetGuid() == used);

        var records = await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.AsNoTracking()
            .Where(r => (r.Action == "identity.update-user" && r.ResourceId == user.UserId) || (r.Action == "identity.delete-role" && r.ResourceId == used)).ToListAsync());
        var update = JsonDocument.Parse(records.Single(r => r.Action == "identity.update-user").Data).RootElement;
        update.GetProperty("before").GetProperty("roleId").GetGuid().ShouldBe(used);
        update.GetProperty("after").GetProperty("roleId").GetGuid().ShouldBe(Roles.Cashier);
        JsonDocument.Parse(records.Single(r => r.Action == "identity.delete-role").Data).RootElement.GetProperty("before").GetProperty("permissions")[0].GetString().ShouldBe("sales.create");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task The_template_roles_are_seeded()
    {
        var roles = (await (await Admin()).GetFromJsonAsync<JsonElement>("/api/identity/roles")).GetProperty("roles").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList());
        roles[Roles.CashierWithoutOpening].ShouldNotContain("register.open");
        roles[Roles.CashierWithoutOpening].ShouldContain("register.close");
        roles[Roles.Cashier].ShouldContain("register.open");
        roles[Roles.StockKeeper].ShouldContain("purchasing.manage");
        roles[Roles.StockKeeper].ShouldNotContain("payables.pay");
        roles[Roles.Accountant].ShouldBe(["reports.view", "fiscal.reports", "costs.view", "shifts.view"], ignoreOrder: true);
    }
}
