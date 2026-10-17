using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Shouldly;

namespace Abastalia.Lite.Tests;

/// <summary>The host with its businesses on another plan (TK-347: every plan, its own caps).</summary>
public sealed class TwoDeviceLiteApp : LiteApp
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Lite:DefaultPlan", "Esencial");
        builder.UseSetting("Lite:Plans:Esencial:SellingDevices", "2");
        builder.UseSetting("Lite:Plans:Esencial:Cashiers", "2");
    }
}

/// <summary>TK-321/TK-347: the caps come from <c>Lite:Plans:&lt;Plan&gt;</c>, not from code.</summary>
public sealed class PlanLimitTests(TwoDeviceLiteApp app) : IClassFixture<TwoDeviceLiteApp>
{
    private static readonly Guid CashierRole = new("0199e000-0000-7000-8000-000000000004");

    [Fact]
    public async Task A_plan_with_two_devices_and_two_cashiers_gets_them()
    {
        var o = await app.BusinessAsync("Dueña");
        var second = await app.PairAsync(await app.CodeAsync(o.TenantId));
        await app.SignInAsync(second, "dueno", LiteApp.Pin); // both sign in
        var third = await app.PairAsync(await app.CodeAsync(o.TenantId), new TestDevice());
        third.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await third.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain("2 dispositivos de venta");

        var owner = app.Client(o);
        foreach (var n in new[] { 1, 2 })
            (await owner.PostAsJsonAsync("/api/identity/users", new { Name = $"Cajera {n}", Code = $"c{n}", Pin = $"432{n}", RoleId = CashierRole })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/identity/users", new { Name = "Cajera 3", Code = "c3", Pin = "4323", RoleId = CashierRole })).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }
}
