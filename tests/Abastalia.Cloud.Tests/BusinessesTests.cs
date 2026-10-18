using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Abastalia.Host.Cloud.Accounts;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-379: one account owns several businesses, each with its own subscription, plan, quotas and license.</summary>
public sealed class BusinessesTests
{
    [Fact]
    public async Task An_account_opens_another_business_of_its_own_and_moves_to_it()
    {
        await using var app = new CloudApp(); // moves its own clock
        var owner = await app.OwnerAsync();

        var created = await owner.Client.PostAsJsonAsync("/api/tenants", new { name = "  Colmado La Esquina 2  " });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var second = (await created.Json()).GetProperty("tenantId").GetGuid();
        second.ShouldNotBe(owner.TenantId);

        // The token takes the new business at its next sign-in or refresh, as when switching.
        var me = await (await app.SignInAsync(owner.Email)).Client.GetFromJsonAsync<JsonElement>("/api/me");
        me.GetProperty("tenantId").GetGuid().ShouldBe(second);
        me.GetProperty("tenantName").GetString().ShouldBe("Colmado La Esquina 2");
        me.GetProperty("role").GetString().ShouldBe("Owner");
        me.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("tenantId").GetGuid()).ShouldBe([owner.TenantId, second], ignoreOrder: true);

        // Nothing is shared: the new business starts with no plan, add-ons, subscription or stores of its own.
        var tenant = await app.InDbAsync(db => db.Tenants.SingleAsync(t => t.Id == second));
        (tenant.Plan, tenant.AddOns).ShouldBe((null, ""));
        (await app.InDbAsync(db => db.TenantSubscriptions.CountAsync(s => s.TenantId == second))).ShouldBe(0);
        (await app.InDbAsync(db => db.Stores.CountAsync(s => s.TenantId == second))).ShouldBe(0);
    }

    [Fact]
    public async Task A_business_needs_a_name_and_an_account_opens_a_few_per_hour_up_to_its_limit()
    {
        await using var app = new CloudApp();
        var owner = await app.OwnerAsync();

        var blank = await owner.Client.PostAsJsonAsync("/api/tenants", new { name = " " });
        (blank.StatusCode, await blank.ErrorCode()).ShouldBe((HttpStatusCode.BadRequest, "Tenants.InvalidName"));

        // The business made at sign-up counts: two more in the same hour, then wait.
        for (var i = 2; i <= StoreEndpoints.MaxNewTenantsPerHour; i++)
            (await owner.Client.PostAsJsonAsync("/api/tenants", new { name = $"Negocio {i}" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        var fast = await owner.Client.PostAsJsonAsync("/api/tenants", new { name = "Uno más" });
        (fast.StatusCode, await fast.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "Tenants.TooFast"));

        for (var i = StoreEndpoints.MaxNewTenantsPerHour + 1; i <= StoreEndpoints.MaxOwnedTenants; i++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(61));
            owner = await app.SignInAsync(owner.Email); // a fresh token: the old one expired with the clock
            (await owner.Client.PostAsJsonAsync("/api/tenants", new { name = $"Negocio {i}" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        app.Clock.Advance(TimeSpan.FromHours(2));
        owner = await app.SignInAsync(owner.Email);
        var many = await owner.Client.PostAsJsonAsync("/api/tenants", new { name = "Once" });
        (many.StatusCode, await many.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "Tenants.Limit"));
    }

    [Fact]
    public async Task A_read_only_member_of_another_business_opens_their_own()
    {
        await using var app = new CloudApp();
        var owner = await app.OwnerAsync();
        var invite = await (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email = "lectura@example.com", role = "ReadOnly" })).Json();
        var link = invite.GetProperty("link").GetString()!;
        var token = link[(link.IndexOf('#') + 1)..];
        (await app.CreateClient().PostAsJsonAsync("/api/invites/register", new { token, password = CloudApp.Password })).EnsureSuccessStatusCode();
        var member = await app.SignInAsync("lectura@example.com");
        (await member.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("role").GetString().ShouldBe("ReadOnly");

        (await member.Client.PostAsJsonAsync("/api/tenants", new { name = "Mi propio colmado" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        var mine = await (await app.SignInAsync("lectura@example.com")).Client.GetFromJsonAsync<JsonElement>("/api/me");
        mine.GetProperty("role").GetString().ShouldBe("Owner");
        mine.GetProperty("tenants").GetArrayLength().ShouldBe(2);
    }
}
