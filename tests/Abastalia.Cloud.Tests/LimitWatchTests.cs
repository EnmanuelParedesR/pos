using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Platform;
using Abastalia.Host.Cloud.Quotas;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-311 (D22): what the limits refuse, counted for "Salud de la plataforma" and the alerts.</summary>
public sealed class LimitWatchTests(CloudApp app) : IClassFixture<CloudApp>
{
    private DateTime Now => app.Clock.GetUtcNow().UtcDateTime;

    [Fact]
    public void TK311_an_ip_is_kept_as_its_network_and_a_salted_hash_never_as_the_address()
    {
        var (network, key) = LimitWatch.Mask(IPAddress.Parse("190.80.12.34"));
        network.ShouldBe("190.80.12.0/24");
        key.Length.ShouldBe(16);
        key.ShouldNotContain("190");
        LimitWatch.Mask(IPAddress.Parse("190.80.12.34")).IpKey.ShouldBe(key);
        LimitWatch.Mask(IPAddress.Parse("190.80.12.35")).ShouldBe(("190.80.12.0/24", LimitWatch.Mask(IPAddress.Parse("190.80.12.35")).IpKey));
        LimitWatch.Mask(IPAddress.Parse("190.80.12.35")).IpKey.ShouldNotBe(key); // two PCs of one /24 stay apart
        LimitWatch.Mask(IPAddress.Parse("::ffff:190.80.12.34")).ShouldBe((network, key));
        LimitWatch.Mask(IPAddress.Parse("2001:db8:abcd:12::1")).Network.ShouldBe("2001:db8:abcd::/48");
    }

    [Fact]
    public async Task TK311_the_gate_the_node_check_and_the_limiters_are_counted_and_staff_see_them()
    {
        // Per-IP rate of the node gate, with Retry-After (its own app: the limit is 2 a minute).
        await using (var strict = app.WithWebHostBuilder(b => b.UseSetting("Nodes:RequestsPerMinutePerIp", "2")))
        {
            var client = strict.CreateClient();
            for (var i = 0; i < 3; i++) await client.PostAsync("/api/nodes/revoke", new StringContent(""));
            await SaveAsync(strict.Services);
        }
        // A per-user limiter of the panel (rate limiter policy).
        await using (var strict = app.WithWebHostBuilder(b => b.UseSetting("Panel:RequestsPerMinutePerUser", "1")))
        {
            var client = strict.CreateClient();
            var email = $"owner{Guid.NewGuid():N}@example.com";
            (await client.PostAsync("/api/auth/register", JsonContent(new { email, password = CloudApp.Password }))).EnsureSuccessStatusCode();
            await CloudApp.ConfirmEmailAsync(strict, email);
            var token = (await (await client.PostAsync("/api/auth/login", JsonContent(new { email, password = CloudApp.Password }))).Json()).GetProperty("accessToken").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            for (var i = 0; i < 2; i++) await client.GetAsync("/api/panel/sync-status");
            await SaveAsync(strict.Services);
        }

        // An unknown node, a revoked one and a body over the route's limit.
        var unknown = new HttpRequestMessage(HttpMethod.Post, "/api/nodes/revoke") { Content = new StringContent("") };
        unknown.Headers.TryAddWithoutValidation("Authorization", $"Node {Guid.NewGuid()}.1.abcdefghijklmnopqr.c2ln");
        (await app.CreateClient().SendAsync(unknown)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        (await node.Client.PostAsync("/api/nodes/revoke", new StringContent(""))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await node.Client.PostAsync("/api/nodes/revoke", new StringContent(""))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var huge = new ByteArrayContent([1, 2, 3]);
        huge.Headers.ContentLength = 2L * 1024 * 1024 * 1024;
        (await app.CreateClient().PostAsync("/api/nodes/backups/abastalia-20261014-233000.abastalia-backup", huge)).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

        var staff = await app.StaffAsync();
        var report = await (await staff.Client.GetAsync("/api/platform/watch?days=1")).Json();
        int Count(string reason, string? detail = null) => report.GetProperty("reasons").EnumerateArray()
            .Where(r => r.GetProperty("reason").GetString() == reason && (detail is null || r.GetProperty("detail").GetString() == detail)).Sum(r => r.GetProperty("count").GetInt32());

        Count(LimitReasons.PerIpRate).ShouldBe(1);
        report.GetProperty("reasons").EnumerateArray().Single(r => r.GetProperty("reason").GetString() == LimitReasons.PerIpRate).GetProperty("retryAfter").GetInt32().ShouldBe(1);
        Count(LimitReasons.RateLimit, "panel-user").ShouldBe(1);
        Count(LimitReasons.UnknownNode).ShouldBe(1);
        Count(LimitReasons.RevokedNode).ShouldBe(1);
        Count(LimitReasons.TooLarge).ShouldBe(1);
        var ip = report.GetProperty("ips").EnumerateArray().First(i => i.GetProperty("reasons").EnumerateArray().Any(r => r.GetString() == LimitReasons.PerIpRate));
        ip.GetProperty("peak15Min").GetInt32().ShouldBe(1);
        report.GetProperty("thresholds").GetProperty("perIpRejections15Min").GetInt32().ShouldBe(20);
        // Nothing in the rows can be an address: the test server's client has none ("?"), real ones are a /24 and a hash.
        (await app.InDbAsync(db => db.LimitRejections.Select(r => r.Network).Distinct().ToListAsync()))
            .ShouldAllBe(n => n == "?" || n == "-" || n.EndsWith(".0/24") || n.EndsWith("::/48"));
    }

    [Fact]
    public async Task TK311_a_minute_is_logged_and_kept_until_the_database_is_awake_and_5xx_have_no_ip()
    {
        var watch = new LimitWatch(app.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<LimitWatch>.Instance);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("190.80.12.34");
        var store = Guid.NewGuid();
        watch.SeenStore(http, store);
        watch.SeenStore(http, Guid.NewGuid());
        watch.SeenStore(http, store);
        watch.Record(http, LimitReasons.PerIpConcurrency);
        watch.Record(http, LimitReasons.PerIpConcurrency);
        watch.Record(http, LimitReasons.Http5xx, "/api/panel/summary", perIp: false);
        watch.Roll();
        watch.Roll(); // an empty minute adds nothing

        var before = await app.InDbAsync(db => db.LimitRejections.CountAsync());
        await using (var scope = app.Services.CreateAsyncScope())
            await watch.SaveAsync(scope.ServiceProvider.GetRequiredService<CloudDbContext>(), CancellationToken.None);
        var rows = await app.InDbAsync(db => db.LimitRejections.OrderBy(r => r.Id).Skip(before).ToListAsync());
        rows.Count.ShouldBe(2);
        rows.Single(r => r.Reason == LimitReasons.PerIpConcurrency).ShouldSatisfyAllConditions(
            r => r.Count.ShouldBe(2), r => r.Stores.ShouldBe(2), r => r.Network.ShouldBe("190.80.12.0/24"));
        rows.Single(r => r.Reason == LimitReasons.Http5xx).ShouldSatisfyAllConditions(r => r.Detail.ShouldBe("/api/panel/summary"), r => r.IpKey.ShouldBe("-"));
    }

    [Fact]
    public async Task TK311_quotas_near_the_limit_silent_stores_and_the_ecf_queue_show_with_their_thresholds()
    {
        var owner = await app.OwnerAsync();
        var quiet = await app.StoreWithNodeAsync(owner, "Colmado Callado");
        var queued = await app.StoreWithNodeAsync(owner, "Colmado con Cola");
        var month = new DateOnly(2026, 10, 1);
        await app.InDbAsync(async db =>
        {
            await db.TenantUsage.Where(u => u.TenantId == owner.TenantId).ExecuteDeleteAsync(); // the sign-up e-mail counted one
            db.TenantUsage.Add(new TenantUsage { TenantId = owner.TenantId, Month = month, Meter = Meter.Emails, Count = 250 }); // 83 % of 300
            db.TenantUsage.Add(new TenantUsage { TenantId = owner.TenantId, Month = month, Meter = Meter.ReceiverSignatures, Count = 3100 }); // fair use passed
            db.TenantUsage.Add(new TenantUsage { TenantId = owner.TenantId, Month = month, Meter = Meter.Invites, Count = 1 }); // 33 %: not shown
            db.NodeHeartbeats.Add(new ReplicaNodeHeartbeat { TenantId = owner.TenantId, NodeId = quiet.NodeId, StoreId = quiet.StoreId, SentUtc = Now.AddHours(-8) });
            db.NodeHeartbeats.Add(new ReplicaNodeHeartbeat
            {
                TenantId = owner.TenantId, NodeId = queued.NodeId, StoreId = queued.StoreId, SentUtc = Now.AddMinutes(-10), OldestPendingUtc = Now.AddMinutes(-10).AddHours(-30),
            });
            return await db.SaveChangesAsync();
        });

        var report = await (await (await app.StaffAsync()).Client.GetAsync("/api/platform/watch?days=7")).Json();
        report.GetProperty("days").GetInt32().ShouldBe(7);
        var quotas = report.GetProperty("quotas").EnumerateArray().Where(q => q.GetProperty("tenantId").GetGuid() == owner.TenantId).ToList();
        quotas.Select(q => q.GetProperty("meter").GetString()).ShouldBe(["ReceiverSignatures", "Emails"]);
        quotas[0].GetProperty("limit").GetInt32().ShouldBe(3000);
        var silent = report.GetProperty("silentStores").EnumerateArray().Select(s => s.GetProperty("storeName").GetString()).ToList();
        silent.ShouldContain("Colmado Callado");
        silent.ShouldNotContain("Colmado con Cola");
        var queue = report.GetProperty("ecfQueues").EnumerateArray().Single(s => s.GetProperty("storeId").GetGuid() == queued.StoreId);
        queue.GetProperty("queueHours").GetDouble().ShouldBe(30);
        report.GetProperty("thresholds").GetProperty("silentStoreHours").GetInt32().ShouldBe(6);
        report.GetProperty("thresholds").GetProperty("queueStaleHours").GetInt32().ShouldBe(24);

        // A revoked PC's store is not "silent": it is disconnected.
        (await quiet.Client.PostAsync("/api/nodes/revoke", new StringContent(""))).EnsureSuccessStatusCode();
        report = await (await (await app.StaffAsync()).Client.GetAsync("/api/platform/watch")).Json();
        report.GetProperty("silentStores").EnumerateArray().ShouldNotContain(s => s.GetProperty("storeId").GetGuid() == quiet.StoreId);
    }

    [Fact]
    public async Task TK311_owners_cannot_read_the_platform_health()
    {
        var owner = await app.OwnerAsync();
        (await owner.Client.GetAsync("/api/platform/watch")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task SaveAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await services.GetRequiredService<LimitWatch>().SaveAsync(scope.ServiceProvider.GetRequiredService<CloudDbContext>(), CancellationToken.None);
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
}
