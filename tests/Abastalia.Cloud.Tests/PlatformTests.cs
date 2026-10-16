using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Abastalia.Host.Cloud.Platform;
using Abastalia.Sales.Contracts;
using Abastalia.Sync.Contracts;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-189: the super-admin console (Abastalia staff) and TK-190: heartbeat ingestion.</summary>
public sealed class PlatformTests(CloudApp app) : IClassFixture<CloudApp>
{
    private DateTime Now => app.Clock.GetUtcNow().UtcDateTime;

    public static readonly DateTime TrialEnds = new(2030, 1, 15, 14, 0, 0, DateTimeKind.Utc);

    /// <param name="license">Null: a store on a version before TK-196, which does not send it.</param>
    public static NodeHeartbeatV1 Heartbeat(string version = "1.4.0", int sales = 3, string? error = null, NodeLicenseV1? license = null) => new(
        Guid.NewGuid(), version, "Microsoft Windows 10.0.19045", 90, 2, 1, 12, error,
        new NodeFeaturesV1(OnAccount: true, null, null, "Simulated"), new NodeSetupV1(true, false, 140),
        [new DailyUsageV1(CloudApp.Today.AddDays(-1), 40, 12_500m, 2, 1, 0m), new DailyUsageV1(CloudApp.Today, sales, 900m, 1, 0, 50m)], license);

    [Fact]
    public async Task An_owner_cannot_reach_the_platform_and_staff_can_with_every_call_audited()
    {
        var owner = await app.OwnerAsync();
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("isPlatformAdmin").GetBoolean().ShouldBeFalse();
        (await owner.Client.GetAsync("/api/platform/tenants")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Client.GetAsync("/api/platform/metrics/overview")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Client.PostAsync($"/api/platform/tenants/{owner.TenantId}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.CreateClient().GetAsync("/api/platform/tenants")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var staff = await app.StaffAsync();
        (await staff.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("isPlatformAdmin").GetBoolean().ShouldBeTrue();
        var before = await app.InDbAsync(db => db.PlatformAudit.CountAsync());
        // R11 B-Low1: the search (an e-mail here) goes in the body, never in the URL that telemetry and the audit keep.
        var tenants = await (await staff.Client.PostAsJsonAsync("/api/platform/tenants/search", new { search = owner.Email })).Json();
        tenants.EnumerateArray().ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(owner.TenantId);
        (await staff.Client.GetAsync($"/api/platform/tenants/{owner.TenantId}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var audit = await app.InDbAsync(db => db.PlatformAudit.OrderBy(a => a.Id).Skip(before).ToListAsync());
        audit.Count.ShouldBe(2);
        audit.ShouldAllBe(a => a.Email == CloudApp.StaffEmail && a.UserId != "" && !a.Path.Contains('@'));
        (audit[0].Method, audit[0].Path).ShouldBe(("POST", "/api/platform/tenants/search"));
        (audit[1].Path, audit[1].TenantId).ShouldBe(($"/api/platform/tenants/{owner.TenantId}", owner.TenantId));
    }

    [Fact]
    public async Task The_role_is_granted_only_by_the_console_command_needs_2FA_and_a_revoke_counts_at_once()
    {
        var owner = await app.OwnerAsync();
        (await PlatformCli.RunAsync(app.Services, PlatformCli.Grant, "nobody@example.com")).ShouldBe(1);
        (await PlatformCli.RunAsync(app.Services, PlatformCli.Grant, owner.Email)).ShouldBe(0);
        (await app.InDbAsync(db => db.PlatformAudit.AnyAsync(a => a.Method == "CLI" && a.Path == $"{PlatformCli.Grant} {owner.Email}"))).ShouldBeTrue();

        // R11 B-Med6: without 2FA on the account, and without a sign-in that passed it, the console stays closed.
        (await owner.Client.GetAsync("/api/platform/tenants")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await app.SignInAsync(owner.Email)).Client.GetAsync("/api/platform/tenants")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var key = await CloudApp.EnableTwoFactorAsync(owner);
        (await owner.Client.GetAsync("/api/platform/tenants")).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // that token did not pass the code
        var login = await app.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = owner.Email, password = CloudApp.Password });
        (await login.Json()).GetProperty("detail").GetString().ShouldBe("RequiresTwoFactor");
        var staff = await app.SignInAsync(owner.Email, key);
        (await staff.Client.GetAsync("/api/platform/tenants")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("isPlatformAdmin").GetBoolean().ShouldBeTrue();

        // The role is read from the database on every call: a revoke closes the console for the token already issued.
        (await PlatformCli.RunAsync(app.Services, PlatformCli.Revoke, owner.Email)).ShouldBe(0);
        (await staff.Client.GetAsync("/api/platform/tenants")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await staff.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("isPlatformAdmin").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task R11_health_ready_wakes_the_database_only_for_staff()
    {
        (await app.CreateClient().GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await (await app.OwnerAsync()).Client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await app.StaffAsync()).Client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.CreateClient().GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Disabling_a_tenant_blocks_its_uploads_and_panel_until_enabled()
    {
        var owner = await app.OwnerAsync();
        var store = await app.StoreWithNodeAsync(owner);
        var staff = await app.StaffAsync();

        (await staff.Client.PostAsync($"/api/platform/tenants/{owner.TenantId}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await store.SendAsync(Events.Envelope(Heartbeat(), Now))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await owner.Client.GetAsync("/api/panel/summary")).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // a token issued before
        var login = await app.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = owner.Email, password = CloudApp.Password });
        login.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await login.Json()).GetProperty("detail").GetString().ShouldBe("NotAllowed");
        (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/platform/tenants/{owner.TenantId}")).GetProperty("disabledUtc").ValueKind.ShouldNotBe(JsonValueKind.Null);

        (await staff.Client.PostAsync($"/api/platform/tenants/{owner.TenantId}/enable", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await store.SendAsync(Events.Envelope(Heartbeat(), Now))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Client.GetAsync("/api/panel/summary")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.Client.PostAsync($"/api/platform/tenants/{Guid.NewGuid()}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_revoked_node_can_no_longer_upload()
    {
        var owner = await app.OwnerAsync();
        var store = await app.StoreWithNodeAsync(owner);
        var staff = await app.StaffAsync();

        (await staff.Client.PostAsync($"/api/platform/nodes/{store.NodeId}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await store.SendAsync(Events.Envelope(Heartbeat(), Now))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var node = (await staff.Client.GetFromJsonAsync<JsonElement>($"/api/platform/stores/{store.StoreId}")).GetProperty("nodes").EnumerateArray().Single();
        node.GetProperty("revokedUtc").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Heartbeats_keep_the_newest_per_node_and_day_and_feed_the_store_checklist()
    {
        var owner = await app.OwnerAsync();
        var store = await app.StoreWithNodeAsync(owner);
        var sale = new SaleRecordedV2(Guid.CreateVersion7(), 1, Guid.NewGuid(), Guid.NewGuid(), null, "E320000000001", 900m, 0m, 0m,
            [new SaleLineV2(Guid.NewGuid(), null, "Pollo", 1, "Unit", 900m, 0m, 500m)], [new SalePaymentV1("Cash", 900m)]);

        (await store.SendAsync(Events.Envelope(Heartbeat("1.4.0", sales: 3, license: new NodeLicenseV1("Trial", TrialEnds)), Now), Events.Envelope(sale, Now.AddHours(-1))))
            .EnsureSuccessStatusCode();
        // An older heartbeat that waited offline arrives later: it never undoes the newer one.
        (await store.SendAsync(Events.Envelope(Heartbeat("1.3.0", sales: 1, error: "http_503"), Now.AddHours(-2)))).EnsureSuccessStatusCode();
        await app.ApplyAsync();

        var node = await app.InDbAsync(db => db.NodeHeartbeats.SingleAsync(h => h.NodeId == store.NodeId));
        (node.AppVersion, node.LastErrorCode, node.OutboxBacklog, node.StoreId, node.TenantId).ShouldBe(("1.4.0", null, 12, store.StoreId, owner.TenantId));
        var days = await app.InDbAsync(db => db.DailyUsage.Where(u => u.StoreId == store.StoreId).OrderBy(u => u.Day).ToListAsync());
        days.Select(d => (d.Day, d.Sales)).ShouldBe([(CloudApp.Today.AddDays(-1), 40), (CloudApp.Today, 3)]);

        var detail = await (await app.StaffAsync()).Client.GetFromJsonAsync<JsonElement>($"/api/platform/stores/{store.StoreId}");
        var checklist = detail.GetProperty("checklist");
        (checklist.GetProperty("connected").GetBoolean(), checklist.GetProperty("heartbeatReceived").GetBoolean()).ShouldBe((true, true));
        (checklist.GetProperty("businessNameSet").GetBoolean(), checklist.GetProperty("rncSet").GetBoolean(), checklist.GetProperty("sequencesLoaded").GetBoolean())
            .ShouldBe((true, false, false));
        (checklist.GetProperty("activeProducts").GetInt32(), checklist.GetProperty("fiscalProvider").GetString()).ShouldBe((140, "Simulated"));
        checklist.GetProperty("firstSaleDay").GetString().ShouldBe(CloudApp.Today.ToString("yyyy-MM-dd"));
        detail.GetProperty("features").GetProperty("onAccount").GetBoolean().ShouldBeTrue();
        // TK-196: the trial status per customer, as the newest heartbeat said.
        (detail.GetProperty("features").GetProperty("licenseStatus").GetString(), detail.GetProperty("features").GetProperty("licenseEndsUtc").GetDateTime())
            .ShouldBe(("Trial", TrialEnds));
        var listed = (await (await app.StaffAsync()).Client.GetFromJsonAsync<JsonElement>("/api/platform/tenants")).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == owner.TenantId);
        listed.GetProperty("licenseStatus").GetString().ShouldBe("Trial");
        var today = detail.GetProperty("days")[0];
        (today.GetProperty("sales").GetInt32(), today.GetProperty("amount").GetDecimal(), today.GetProperty("reportedSales").GetInt32()).ShouldBe((1, 900m, 3));
        detail.GetProperty("nodes")[0].GetProperty("appVersion").GetString().ShouldBe("1.4.0");

        var tenant = await (await app.StaffAsync()).Client.GetFromJsonAsync<JsonElement>($"/api/platform/tenants/{owner.TenantId}");
        tenant.GetProperty("metrics").GetProperty("stage").GetString().ShouldBe(FunnelStages.FirstSale);
        tenant.GetProperty("connectionCodes").EnumerateArray().ShouldHaveSingleItem().GetProperty("usedUtc").ValueKind.ShouldNotBe(JsonValueKind.Null);
        tenant.GetProperty("connectionCodes")[0].TryGetProperty("code", out _).ShouldBeFalse();
        var overview = await (await app.StaffAsync()).Client.GetFromJsonAsync<JsonElement>("/api/platform/metrics/overview");
        overview.GetProperty("health").GetProperty("versions").EnumerateArray().ShouldContain(v => v.GetProperty("version").GetString() == "1.4.0");
    }
}
