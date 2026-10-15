using System.Net.Http.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Identity;

/// <summary>TK-014: a supervisor approval is single-use and bound to its action, resource, device and two minutes.
/// Own app because one test moves the clock.</summary>
public sealed class SupervisorApprovalTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<Guid> ApproveAsync(UserSession cashier, Guid resource, string permission = "sales.void")
    {
        var r = await app.Client(cashier).PostAsJsonAsync("/api/identity/approvals",
            new { Code = "admin", Pin = TokiApp.AdminPin, Permission = permission, ResourceId = resource });
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("approvalId").GetGuid();
    }

    [Fact]
    public async Task Without_approval_a_supervisor_is_required()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        var r = await app.RunAsync(cashier, new SupervisedAction(Guid.NewGuid(), null));
        r.Error!.Code.ShouldBe("Approval.Required");
    }

    [Fact]
    public async Task It_is_used_only_once_and_lands_in_the_audit()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        var resource = Guid.NewGuid();
        var id = await ApproveAsync(cashier, resource);

        (await app.RunAsync(cashier, new SupervisedAction(resource, id))).IsSuccess.ShouldBeTrue();
        (await app.RunAsync(cashier, new SupervisedAction(resource, id))).Error!.Code.ShouldBe("Identity.InvalidApproval");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task It_does_not_work_for_another_resource_or_another_action()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        var resource = Guid.NewGuid();
        var id = await ApproveAsync(cashier, resource, permission: "register.withdraw");

        (await app.RunAsync(cashier, new SupervisedAction(resource, id))).Error!.Code.ShouldBe("Identity.InvalidApproval");
        var other = await ApproveAsync(cashier, resource);
        (await app.RunAsync(cashier, new SupervisedAction(Guid.NewGuid(), other))).Error!.Code.ShouldBe("Identity.InvalidApproval");
    }

    [Fact]
    public async Task It_does_not_work_from_another_device()
    {
        var admin = await app.AdminAsync();
        var cashier = await app.UserAsync(Roles.Cashier);
        var resource = Guid.NewGuid();
        var id = await ApproveAsync(cashier, resource);

        var code = await (await app.Client(admin).PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja 3" })).Json();
        var register3 = await (await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code.GetProperty("code").GetString() })).Json();
        var users = await app.Client(admin).GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/identity/users");
        var cashierCode = users!.Single(u => u.GetProperty("id").GetGuid() == cashier.UserId).GetProperty("code").GetString()!;
        var onRegister3 = await app.SignInAsync(cashierCode, "4321", register3.GetProperty("token").GetString()!);

        (await app.RunAsync(onRegister3, new SupervisedAction(resource, id))).Error!.Code.ShouldBe("Identity.InvalidApproval");
    }

    [Fact]
    public async Task It_expires_after_two_minutes()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        var resource = Guid.NewGuid();
        var id = await ApproveAsync(cashier, resource);

        app.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        (await app.RunAsync(cashier, new SupervisedAction(resource, id))).Error!.Code.ShouldBe("Identity.InvalidApproval");
    }
}
