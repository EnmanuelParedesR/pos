using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Settings;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>TK-210: each store chooses which actions ask for a supervisor. Own app: the tests change the store's policy.</summary>
public sealed class ApprovalPolicyTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    private async Task SetAsync(params string[] required) =>
        (await (await Admin()).PutAsJsonAsync("/api/settings/approvals", new { Required = required })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

    private static async Task<Dictionary<string, bool>> GetAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<JsonElement>>("/api/settings/approvals"))!
            .ToDictionary(r => r.GetProperty("permission").GetString()!, r => r.GetProperty("required").GetBoolean());

    [Fact]
    public async Task Defaults_keep_todays_approvals_and_round_trip()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        // A store that never saved the policy (another test here may have).
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.SupervisorApprovals, (string?)null)));
        var policy = await GetAsync(app.Client(cashier)); // the register reads it with any role
        policy["sales.remove-line"].ShouldBeFalse();
        policy.Where(p => p.Key != "sales.remove-line").ShouldAllBe(p => p.Value);
        policy.Keys.ShouldBe(["sales.remove-line", "sales.void", "sales.return", "sales.large-discount", "sales.price-override",
            "register.withdraw", "register.approve-close", "inventory.adjust", "credit.manage", "credit.sell"]);

        await SetAsync("sales.remove-line", "sales.return");
        var saved = await GetAsync(await Admin());
        saved.Where(p => p.Value).Select(p => p.Key).ShouldBe(["sales.remove-line", "sales.return"]);

        (await app.Client(cashier).PutAsJsonAsync("/api/settings/approvals", new { Required = Array.Empty<string>() })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await Admin()).PutAsJsonAsync("/api/settings/approvals", new { Required = new[] { "purchasing.manage" } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_who_can_do_an_action_turns_its_approval_off()
    {
        // R8 #5: settings.manage alone does not turn off the supervisor of a void (it would let that user void alone).
        var role = (await (await (await Admin()).PostAsJsonAsync("/api/identity/roles",
            new { Name = "Encargado " + Guid.NewGuid().ToString()[..4], Permissions = new[] { "settings.manage", "sales.return" } })).Json()).GetProperty("id").GetGuid();
        var manager = app.Client(await app.UserAsync(role));
        await SetAsync("sales.void", "sales.return");

        var denied = await manager.PutAsJsonAsync("/api/settings/approvals", new { Required = new[] { "sales.return" } });
        (denied.StatusCode, await denied.ErrorCode()).ShouldBe((HttpStatusCode.Forbidden, "Settings.CannotWaiveApproval"));
        (await GetAsync(manager))["sales.void"].ShouldBeTrue();

        // What the role can do it turns off, and anything it turns on.
        (await manager.PutAsJsonAsync("/api/settings/approvals", new { Required = new[] { "sales.void", "inventory.adjust" } })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync(manager)).Where(p => p.Value).Select(p => p.Key).ShouldBe(["sales.void", "inventory.adjust"]);
    }

    [Fact]
    public async Task With_the_policy_off_the_cashier_acts_alone_and_on_it_asks()
    {
        var cashier = await app.UserAsync(Roles.Cashier);
        await SetAsync("sales.return");
        (await app.RunAsync(cashier, new SupervisedAction(Guid.NewGuid(), null))).IsSuccess.ShouldBeTrue(); // sales.void, now off

        await SetAsync("sales.void");
        (await app.RunAsync(cashier, new SupervisedAction(Guid.NewGuid(), null))).Error!.Code.ShouldBe("Approval.Required");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A_large_discount_follows_the_policy_too()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = await Admin();
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco " + Guid.NewGuid().ToString()[..4], 59m, cost: 35m, stock: 24);
        object Body() => new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, Lines = new[] { new { ProductId = soda, Quantity = 2m, Discount = new { IsPercentage = true, Value = 20m } } },
            Payments = new[] { new { Method = "Cash", Amount = 100m } }, ExpectedTotal = 94.40m, SupervisorApprovals = Array.Empty<Guid>(),
        };

        await SetAsync("sales.large-discount");
        (await (await cashier.SellAsync(Body())).ErrorCode()).ShouldBe("Approval.Required");
        await SetAsync();
        (await cashier.SellAsync(Body())).StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
