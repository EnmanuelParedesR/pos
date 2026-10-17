using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Shouldly;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-217: panel members (Owner, ReadOnly) and single-use e-mail invites.</summary>
public sealed class MembersTests(CloudApp app) : IClassFixture<CloudApp>
{
    private static string NewEmail(string who = "member") => $"{who}{Guid.NewGuid():N}@example.com";

    private static async Task<(Guid Id, string Token)> InviteAsync(Owner owner, string email, string role = "ReadOnly")
    {
        var r = await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await r.Json();
        var link = json.GetProperty("link").GetString()!;
        link.ShouldContain("/invite#"); // R8 #12: after "#", never sent to a server
        return (json.GetProperty("invite").GetProperty("id").GetGuid(), link[(link.IndexOf('#') + 1)..]);
    }

    private Task<HttpResponseMessage> RegisterAsync(string token) =>
        app.CreateClient().PostAsJsonAsync("/api/invites/register", new { token, password = CloudApp.Password });

    /// <summary>A new account that joined <paramref name="owner"/>'s tenant through an invite, signed in.</summary>
    private async Task<Owner> MemberAsync(Owner owner, string role = "ReadOnly")
    {
        var email = NewEmail();
        var (_, token) = await InviteAsync(owner, email, role);
        (await RegisterAsync(token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return await app.SignInAsync(email);
    }

    private static Task<HttpResponseMessage> Preview(HttpClient client, string token) => client.PostAsJsonAsync("/api/invites/preview", new { token });

    private static async Task<JsonElement> MembersAsync(Owner owner) => await owner.Client.GetFromJsonAsync<JsonElement>("/api/members");

    private static async Task<string> UserIdAsync(Owner owner, string email) =>
        (await MembersAsync(owner)).GetProperty("members").EnumerateArray().Single(m => m.GetProperty("email").GetString() == email).GetProperty("userId").GetString()!;

    [Fact]
    public async Task An_invitee_creates_an_account_and_joins_the_tenant_with_the_invited_role()
    {
        var owner = await app.OwnerAsync();
        var email = NewEmail();
        var (_, token) = await InviteAsync(owner, email);
        (await app.InDbAsync(db => db.TenantInvites.SingleAsync(i => i.Email == email))).TokenHash.Length.ShouldBe(32); // only the hash is kept

        var preview = await (await Preview(app.CreateClient(), token)).Json();
        (preview.GetProperty("email").GetString(), preview.GetProperty("role").GetString()).ShouldBe((email, "ReadOnly"));

        (await RegisterAsync(token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // R8 #1: without an e-mail provider the owner saw the link, so the address is not taken as confirmed (R11 B-High1: nor can it sign in yet).
        (await app.InDbAsync(db => db.Users.SingleAsync(u => u.Email == email))).EmailConfirmed.ShouldBeFalse();
        (await app.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = CloudApp.Password })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var member = await app.SignInAsync(email);
        member.TenantId.ShouldBe(owner.TenantId);
        var me = await member.Client.GetFromJsonAsync<JsonElement>("/api/me");
        me.GetProperty("role").GetString().ShouldBe("ReadOnly");
        (await app.InDbAsync(db => db.Tenants.CountAsync(t => t.Name == email))).ShouldBe(0); // no tenant of their own

        var members = (await MembersAsync(owner)).GetProperty("members").EnumerateArray().Select(m => (m.GetProperty("email").GetString(), m.GetProperty("role").GetString())).ToList();
        members.ShouldBe([(owner.Email, "Owner"), (email, "ReadOnly")]);
    }

    [Fact]
    public async Task A_link_works_once()
    {
        var owner = await app.OwnerAsync();
        var (_, token) = await InviteAsync(owner, NewEmail());
        (await RegisterAsync(token)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Preview(app.CreateClient(), token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var again = await RegisterAsync(token);
        (again.StatusCode, await again.ErrorCode()).ShouldBe((HttpStatusCode.NotFound, "Invites.Invalid"));
        (await (await app.OwnerAsync()).Client.PostAsJsonAsync("/api/invites/accept", new { token })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_link_expires_after_7_days_and_a_revoked_one_stops_working()
    {
        await using var own = new CloudApp(); // moves its own clock
        var owner = await own.OwnerAsync();
        var (_, late) = await InviteAsync(owner, NewEmail());
        var (revokedId, revoked) = await InviteAsync(owner, NewEmail());

        (await owner.Client.DeleteAsync($"/api/members/invites/{revokedId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await own.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = revoked, password = CloudApp.Password })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        own.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        (await Preview(own.CreateClient(), late)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await own.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = late, password = CloudApp.Password })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await MembersAsync(await own.SignInAsync(owner.Email))).GetProperty("invites").GetArrayLength().ShouldBe(0); // the old token expired too
    }

    [Fact]
    public async Task An_existing_account_joins_only_with_its_own_email_and_can_switch_between_tenants()
    {
        var owner = await app.OwnerAsync();
        var other = await app.OwnerAsync(); // has its own tenant
        var stranger = await app.OwnerAsync();
        var (_, token) = await InviteAsync(owner, other.Email.ToUpperInvariant());

        var wrong = await stranger.Client.PostAsJsonAsync("/api/invites/accept", new { token });
        (wrong.StatusCode, await wrong.ErrorCode()).ShouldBe((HttpStatusCode.Forbidden, "Invites.WrongEmail"));
        (await app.CreateClient().PostAsJsonAsync("/api/invites/accept", new { token })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RegisterAsync(token)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // the account exists: sign in instead

        (await other.Client.PostAsJsonAsync("/api/invites/accept", new { token })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var joined = await app.SignInAsync(other.Email);
        joined.TenantId.ShouldBe(owner.TenantId);
        var me = await joined.Client.GetFromJsonAsync<JsonElement>("/api/me");
        me.GetProperty("role").GetString().ShouldBe("ReadOnly");
        me.GetProperty("tenants").EnumerateArray().Select(t => t.GetProperty("tenantId").GetGuid()).ShouldBe([owner.TenantId, other.TenantId], ignoreOrder: true);

        (await joined.Client.PostAsJsonAsync("/api/me/tenant", new { tenantId = stranger.TenantId })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await joined.Client.PostAsJsonAsync("/api/me/tenant", new { tenantId = other.TenantId })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var back = await app.SignInAsync(other.Email);
        back.TenantId.ShouldBe(other.TenantId);
        (await back.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("role").GetString().ShouldBe("Owner");
    }

    [Fact]
    public async Task An_owner_cannot_touch_another_tenants_members_or_invites()
    {
        var a = await app.OwnerAsync();
        var b = await app.OwnerAsync();
        var member = await MemberAsync(a);
        var userId = await UserIdAsync(a, member.Email);
        var (inviteId, _) = await InviteAsync(a, NewEmail());

        (await b.Client.DeleteAsync($"/api/members/invites/{inviteId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.PutAsJsonAsync($"/api/members/{userId}/role", new { role = "Owner" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.DeleteAsync($"/api/members/{userId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await MembersAsync(b)).GetProperty("members").GetArrayLength().ShouldBe(1);
        (await MembersAsync(a)).GetProperty("invites").GetArrayLength().ShouldBe(1);
        (await member.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("role").GetString().ShouldBe("ReadOnly");
    }

    [Fact]
    public async Task A_read_only_member_sees_the_panel_but_every_write_is_forbidden()
    {
        var owner = await app.OwnerAsync();
        var store = await app.StoreWithNodeAsync(owner);
        var member = await MemberAsync(owner);
        var ownerId = await UserIdAsync(owner, owner.Email);
        var (inviteId, _) = await InviteAsync(owner, NewEmail());

        foreach (var path in new[] { "/api/me", "/api/stores", "/api/panel/summary", "/api/panel/sales/by-day", "/api/panel/shifts", "/api/panel/sync-status" })
            (await member.Client.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.OK, path);
        (await member.Client.PostAsJsonAsync("/api/assistant/ask", new { question = "¿Cuánto vendí?" })).StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);

        // Every panel write (the endpoint-metadata test below keeps this list complete).
        var writes = new (string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)[]
        {
            ("POST /api/stores", c => c.PostAsJsonAsync("/api/stores", new { name = "Otra" })),
            ("POST /api/stores/{id}/connection-codes", c => c.PostAsync($"/api/stores/{store.StoreId}/connection-codes", null)),
            ("GET /api/members", c => c.GetAsync("/api/members")),
            ("POST /api/members/invites", c => c.PostAsJsonAsync("/api/members/invites", new { email = NewEmail(), role = "Owner" })),
            ("DELETE /api/members/invites/{id}", c => c.DeleteAsync($"/api/members/invites/{inviteId}")),
            ("PUT /api/members/{userId}/role", c => c.PutAsJsonAsync($"/api/members/{ownerId}/role", new { role = "ReadOnly" })),
            ("PUT /api/members/{userId}/costs", c => c.PutAsJsonAsync($"/api/members/{ownerId}/costs", new { canViewCosts = false })),
            ("DELETE /api/members/{userId}", c => c.DeleteAsync($"/api/members/{ownerId}")),
        };
        foreach (var (name, call) in writes)
            (await call(member.Client)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, name);

        (await app.InDbAsync(db => db.Stores.CountAsync(s => s.TenantId == owner.TenantId))).ShouldBe(1);
        (await MembersAsync(owner)).GetProperty("invites").GetArrayLength().ShouldBe(1);
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("role").GetString().ShouldBe("Owner");
    }

    /// <summary>A new panel endpoint that writes must be Owner-only, or be added here on purpose.</summary>
    [Fact]
    public void Every_panel_write_endpoint_requires_an_owner()
    {
        // TK-222: every member chooses their own e-mail alerts.
        // TK-349: every member may rate Talia's answers.
        string[] readOnlyAllowed = ["POST /api/assistant/ask", "POST /api/assistant/feedback", "POST /api/me/tenant", "PUT /api/notifications/"];
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => (
                Name: $"{string.Join(",", e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [])} /{e.RoutePattern.RawText?.TrimStart('/')}",
                Policies: e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList()))
            .Where(e => e.Policies.Contains(CloudAuth.PanelPolicy) && !e.Name.StartsWith("GET ", StringComparison.Ordinal))
            .ToList();

        endpoints.Count.ShouldBeGreaterThan(5);
        endpoints.Where(e => !e.Policies.Contains(CloudAuth.OwnerPolicy)).Select(e => e.Name).Order().ShouldBe(readOnlyAllowed);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted_or_removed()
    {
        var owner = await app.OwnerAsync();
        var ownerId = await UserIdAsync(owner, owner.Email);
        var demote = await owner.Client.PutAsJsonAsync($"/api/members/{ownerId}/role", new { role = "ReadOnly" });
        (demote.StatusCode, await demote.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "Members.LastOwner"));
        (await owner.Client.DeleteAsync($"/api/members/{ownerId}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var second = await MemberAsync(owner);
        var secondId = await UserIdAsync(owner, second.Email);
        (await owner.Client.PutAsJsonAsync($"/api/members/{secondId}/role", new { role = "Owner" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.Client.PutAsJsonAsync($"/api/members/{ownerId}/role", new { role = "ReadOnly" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.Client.GetAsync("/api/members")).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // counts at once, same token

        (await second.Client.DeleteAsync($"/api/members/{secondId}")).StatusCode.ShouldBe(HttpStatusCode.Conflict); // now the only Owner
        (await second.Client.DeleteAsync($"/api/members/{ownerId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.Client.GetAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // removed: the old token is refused
    }

    /// <summary>The TK-217 shortcut: two Owners demoting or removing each other at the same instant could leave the tenant with none.</summary>
    [Fact]
    public async Task Two_owners_demoting_or_removing_each_other_at_once_leave_exactly_one_owner()
    {
        for (var round = 0; round < 6; round++)
        {
            var a = await app.OwnerAsync();
            var b = await MemberAsync(a, "Owner");
            var (aId, bId) = (await UserIdAsync(a, a.Email), await UserIdAsync(a, b.Email));
            var results = round % 2 == 0
                ? await Task.WhenAll(
                    a.Client.PutAsJsonAsync($"/api/members/{bId}/role", new { role = "ReadOnly" }),
                    b.Client.PutAsJsonAsync($"/api/members/{aId}/role", new { role = "ReadOnly" }))
                : await Task.WhenAll(a.Client.DeleteAsync($"/api/members/{bId}"), b.Client.DeleteAsync($"/api/members/{aId}"));

            results.Count(r => r.StatusCode == HttpStatusCode.NoContent).ShouldBe(1, $"round {round}");
            (await app.InDbAsync(db => db.TenantMembers.CountAsync(m => m.TenantId == a.TenantId && m.Role == MemberRole.Owner))).ShouldBe(1, $"round {round}");
        }
    }

    [Fact]
    public async Task A_read_only_member_sees_profit_only_when_an_owner_allows_it()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var member = await MemberAsync(owner);
        (await node.SendAsync(Events.Envelope(PanelTests.Sale(500, Guid.CreateVersion7(), Guid.CreateVersion7(), "Bistec de res"), app.Clock.GetUtcNow().UtcDateTime)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.ApplyAsync();
        static async Task<JsonValueKind> Profit(Owner who) =>
            (await who.Client.GetFromJsonAsync<JsonElement>("/api/panel/sales/top-products")).EnumerateArray().Single().GetProperty("profit").ValueKind;
        static async Task<bool> CanViewCosts(Owner who) => (await who.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("canViewCosts").GetBoolean();

        ((await Profit(owner)), await CanViewCosts(owner)).ShouldBe((JsonValueKind.Number, true));
        ((await Profit(member)), await CanViewCosts(member)).ShouldBe((JsonValueKind.Null, false));

        var memberId = await UserIdAsync(owner, member.Email);
        (await member.Client.PutAsJsonAsync($"/api/members/{memberId}/costs", new { canViewCosts = true })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Client.PutAsJsonAsync($"/api/members/{memberId}/costs", new { canViewCosts = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        ((await Profit(member)), await CanViewCosts(member)).ShouldBe((JsonValueKind.Number, true));
        (await MembersAsync(owner)).GetProperty("members").EnumerateArray().ShouldAllBe(m => m.GetProperty("canViewCosts").GetBoolean());
    }

    [Fact]
    public async Task A_disabled_tenant_blocks_its_members_and_its_invites()
    {
        var owner = await app.OwnerAsync();
        var member = await MemberAsync(owner);
        var (_, token) = await InviteAsync(owner, NewEmail());
        var staff = await app.StaffAsync();
        (await staff.Client.PostAsync($"/api/platform/tenants/{owner.TenantId}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await member.Client.GetAsync("/api/panel/summary")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RegisterAsync(token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await staff.Client.PostAsync($"/api/platform/tenants/{owner.TenantId}/enable", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await member.Client.GetAsync("/api/panel/summary")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_platform_console_lists_a_tenants_members()
    {
        var owner = await app.OwnerAsync();
        var member = await MemberAsync(owner);
        var staff = await app.StaffAsync();
        var detail = await staff.Client.GetFromJsonAsync<JsonElement>($"/api/platform/tenants/{owner.TenantId}");
        detail.GetProperty("members").EnumerateArray().Select(m => (m.GetProperty("email").GetString(), m.GetProperty("role").GetString()))
            .ShouldBe([(owner.Email, "Owner"), (member.Email, "ReadOnly")]);
    }
}

/// <summary>An e-mail provider that delivers (R8 #1): tests read the invite link from the message.</summary>
public sealed class RecordingEmailSender : IEmailSender
{
    public bool Delivers => true;

    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        lock (Sent) Sent.Add((to, subject, body));
        return Task.CompletedTask;
    }
}

public sealed class MailingCloudApp : CloudApp
{
    public RecordingEmailSender Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(Mail));
    }
}

/// <summary>R8 #1, #12, #18.</summary>
public sealed class InviteSecurityTests(CloudApp app) : IClassFixture<CloudApp>
{
    [Fact]
    public async Task With_an_email_provider_the_link_travels_only_by_email_and_confirms_the_address()
    {
        await using var mailing = new MailingCloudApp();
        var owner = await mailing.OwnerAsync();
        var email = $"member{Guid.NewGuid():N}@example.com";
        var r = await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role = "ReadOnly" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Json()).GetProperty("link").ValueKind.ShouldBe(JsonValueKind.Null); // the inviter cannot take over that address

        var body = mailing.Mail.Sent.Single(m => m.To == email && m.Subject.StartsWith("Le invitaron", StringComparison.Ordinal)).Body;
        body.ShouldContain("/invite#");
        var token = body[(body.IndexOf('#') + 1)..];
        (await mailing.CreateClient().PostAsJsonAsync("/api/invites/register", new { token, password = CloudApp.Password })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await mailing.InDbAsync(db => db.Users.SingleAsync(u => u.Email == email))).EmailConfirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task R9_21_an_account_someone_registered_with_the_invitees_email_does_not_block_the_invite_sent_to_that_email()
    {
        await using var mailing = new MailingCloudApp();
        var owner = await mailing.OwnerAsync();
        var email = $"member{Guid.NewGuid():N}@example.com";
        // Registered first, by someone who does not read that inbox (and so never confirmed it).
        (await mailing.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = CloudApp.Password })).EnsureSuccessStatusCode();
        var squatter = new { TenantId = await mailing.InDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.TenantId).SingleAsync()) };
        (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role = "ReadOnly" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = mailing.Mail.Sent.Single(m => m.To == email && m.Subject.StartsWith("Le invitaron", StringComparison.Ordinal)).Body;
        var token = body[(body.IndexOf('#') + 1)..];

        const string newPassword = "Invitada2026";
        (await mailing.CreateClient().PostAsJsonAsync("/api/invites/register", new { token, password = newPassword })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var account = await mailing.InDbAsync(db => db.Users.SingleAsync(u => u.Email == email));
        account.EmailConfirmed.ShouldBeTrue();
        (await mailing.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = CloudApp.Password })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var login = await mailing.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = newPassword });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await mailing.InDbAsync(db => db.TenantMembers.AnyAsync(m => m.TenantId == owner.TenantId && m.UserId == account.Id))).ShouldBeTrue();
        // It may be the person's own earlier sign-up: the account keeps what it had; whoever else knew the old password is out.
        account.Id.ShouldBe(await mailing.InDbAsync(db => db.TenantMembers.Where(m => m.TenantId == squatter.TenantId).Select(m => m.UserId).SingleAsync()));
    }

    [Fact]
    public async Task TK231_an_existing_account_that_accepts_an_emailed_invite_gets_its_email_confirmed()
    {
        await using var mailing = new MailingCloudApp();
        var owner = await mailing.OwnerAsync();
        var member = await mailing.OwnerAsync(); // signed up before TK-230: never confirmed
        (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email = member.Email, role = "ReadOnly" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = mailing.Mail.Sent.Single(m => m.To == member.Email && m.Subject.StartsWith("Le invitaron", StringComparison.Ordinal)).Body;

        (await member.Client.PostAsJsonAsync("/api/invites/accept", new { token = body[(body.IndexOf('#') + 1)..] })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await member.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_copied_invite_link_does_not_confirm_the_email_of_the_account_that_accepts_it()
    {
        var owner = await app.OwnerAsync();
        var member = await app.OwnerAsync();
        // A session from before R11 (sign-in did not need a confirmed e-mail then).
        await app.InDbAsync(db => db.Users.Where(u => u.Email == member.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, false)));
        var link = (await (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email = member.Email, role = "ReadOnly" })).Json()).GetProperty("link").GetString()!;
        (await member.Client.PostAsJsonAsync("/api/invites/accept", new { token = link[(link.IndexOf('#') + 1)..] })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await member.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("emailConfirmed").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task R9_21_a_confirmed_account_is_never_taken_over_by_an_invite()
    {
        await using var mailing = new MailingCloudApp();
        var first = await mailing.OwnerAsync();
        var email = $"member{Guid.NewGuid():N}@example.com";
        await mailing.OwnerAsync(email);
        await mailing.InDbAsync(db => db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true)));
        (await first.Client.PostAsJsonAsync("/api/members/invites", new { email, role = "ReadOnly" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = mailing.Mail.Sent.Single(m => m.To == email && m.Subject.StartsWith("Le invitaron", StringComparison.Ordinal)).Body;
        var r = await mailing.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = body[(body.IndexOf('#') + 1)..], password = "Otra2026x" });
        (await r.ErrorCode()).ShouldBe("Invites.AccountExists");
    }

    [Fact]
    public async Task An_email_in_the_old_Platform_Admins_setting_gets_no_platform_role()
    {
        var account = await app.OwnerAsync(CloudApp.LegacyAdminEmail);
        (await account.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("isPlatformAdmin").GetBoolean().ShouldBeFalse();
        (await account.Client.GetAsync("/api/platform/tenants")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_member_of_several_tenants_signs_in_to_an_active_one_and_is_refused_only_when_all_are_disabled()
    {
        var first = await app.OwnerAsync();
        var second = await app.OwnerAsync();
        var invite = await (await second.Client.PostAsJsonAsync("/api/members/invites", new { email = first.Email, role = "ReadOnly" })).Json();
        var link = invite.GetProperty("link").GetString()!;
        (await first.Client.PostAsJsonAsync("/api/invites/accept", new { token = link[(link.IndexOf('#') + 1)..] })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await first.Client.PostAsJsonAsync("/api/me/tenant", new { tenantId = first.TenantId })).StatusCode.ShouldBe(HttpStatusCode.NoContent); // back to its own

        var staff = await app.StaffAsync();
        (await staff.Client.PostAsync($"/api/platform/tenants/{first.TenantId}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.SignInAsync(first.Email)).TenantId.ShouldBe(second.TenantId);

        (await staff.Client.PostAsync($"/api/platform/tenants/{second.TenantId}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = first.Email, password = CloudApp.Password })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
