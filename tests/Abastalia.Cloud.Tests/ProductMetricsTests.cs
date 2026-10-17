using Shouldly;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Platform;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-190: the metric definitions of docs/metricas-producto.md over a fixed fixture.</summary>
public sealed class ProductMetricsTests
{
    // Thursday 2026-10-15, 11:00 in the store.
    private static readonly DateTime Now = new(2026, 10, 15, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 15);

    private static readonly Tenant Loyal = new() { Id = Guid.NewGuid(), Name = "loyal@example.com", CreatedUtc = Now.AddDays(-20) };
    private static readonly Tenant Quiet = new() { Id = Guid.NewGuid(), Name = "quiet@example.com", CreatedUtc = Now.AddDays(-20) };
    private static readonly Tenant Fresh = new() { Id = Guid.NewGuid(), Name = "fresh@example.com", CreatedUtc = Now.AddDays(-1) };
    private static readonly Tenant Lead = new() { Id = Guid.NewGuid(), Name = "lead@example.com", CreatedUtc = Now.AddHours(-2) };

    private static readonly Store LoyalStore = new() { Id = Guid.NewGuid(), TenantId = Loyal.Id, Name = "La Esquina", CreatedUtc = Loyal.CreatedUtc };
    private static readonly Store QuietStore = new() { Id = Guid.NewGuid(), TenantId = Quiet.Id, Name = "El Cruce", CreatedUtc = Quiet.CreatedUtc };
    private static readonly Store FreshStore = new() { Id = Guid.NewGuid(), TenantId = Fresh.Id, Name = "Nueva", CreatedUtc = Fresh.CreatedUtc };

    private static readonly Node LoyalPc = new() { Id = Guid.NewGuid(), TenantId = Loyal.Id, StoreId = LoyalStore.Id, Name = "PC", CreatedUtc = Now.AddDays(-19), LastSyncUtc = Now.AddMinutes(-5) };
    private static readonly Node QuietPc = new() { Id = Guid.NewGuid(), TenantId = Quiet.Id, StoreId = QuietStore.Id, Name = "PC", CreatedUtc = Now.AddDays(-19), LastSyncUtc = Now.AddDays(-4) };
    private static readonly Node FreshPc = new() { Id = Guid.NewGuid(), TenantId = Fresh.Id, StoreId = FreshStore.Id, Name = "PC", CreatedUtc = Now.AddHours(-20) };

    private static ProductMetrics Metrics(IEnumerable<ReplicaDailyUsage>? usage = null)
    {
        // Loyal sold on each of the last 10 days; Quiet sold 12 and 11 days ago and stopped; Fresh has not sold.
        var sales = Enumerable.Range(0, 10).Select(i => new SaleFact(Loyal.Id, LoyalStore.Id, Today.AddDays(-i), 100m))
            .Concat([new SaleFact(Quiet.Id, QuietStore.Id, Today.AddDays(-12), 250m), new SaleFact(Quiet.Id, QuietStore.Id, Today.AddDays(-11), 50m)])
            .ToList();
        ReplicaNodeHeartbeat Beat(Node n, string version, bool onAccount, string? error, int daysAgo = 0, string fiscal = "Simulated") => new()
        {
            TenantId = n.TenantId, NodeId = n.Id, StoreId = n.StoreId, SentUtc = Now.AddHours(-1).AddDays(-daysAgo), AppVersion = version, OnAccount = onAccount, LastErrorCode = error,
            FiscalProvider = fiscal, ActiveRegisters = 1,
        };
        return new ProductMetrics([Loyal, Quiet, Fresh, Lead], [LoyalStore, QuietStore, FreshStore], [LoyalPc, QuietPc, FreshPc], sales,
            [Beat(LoyalPc, "1.4.0", true, null, fiscal: "eCF"), Beat(FreshPc, "1.3.0", false, "unreachable"),
             Beat(QuietPc, "1.2.0", false, null, daysAgo: 8)], // too old to count
            [.. usage ?? []], failedEvents: 2, Now);
    }

    [Fact]
    public void The_funnel_counts_how_far_each_account_got()
    {
        var funnel = Metrics().Overview().Funnel.ToDictionary(f => f.Stage, f => f.Tenants);

        funnel.ShouldBe(new Dictionary<string, int>
        {
            [FunnelStages.Account] = 4, [FunnelStages.Store] = 3, [FunnelStages.Connected] = 3, [FunnelStages.FirstSale] = 2, [FunnelStages.SevenDays] = 1, [FunnelStages.Certified] = 0,
        });
        Metrics().Stage(Lead.Id).ShouldBe(FunnelStages.Account);
        Metrics().Stage(Fresh.Id).ShouldBe(FunnelStages.Connected);
    }

    [Fact]
    public void Active_stores_sales_health_and_adoption()
    {
        var o = Metrics().Overview();

        (o.ActiveToday, o.ActiveLast7Days, o.ActiveLast30Days).ShouldBe((1, 1, 2));
        (o.SalesLast30Days, o.AmountLast30Days).ShouldBe((12, 1300m));
        (o.Nodes, o.NodesOnline).ShouldBe((3, 1)); // Quiet's PC: 4 days ago; Fresh's: never
        (o.Health.StaleNodes, o.Health.NodesWithErrors, o.Health.FailedEvents).ShouldBe((2, 1, 2));
        o.Health.Versions.Select(v => v.Version).ShouldBe(["1.4.0", "1.3.0"]); // 1.2.0's heartbeat is 8 days old
        var fiado = o.Features.Single(f => f.Feature == "onAccount");
        (fiado.Stores, fiado.Reporting).ShouldBe((1, 2));
        o.Features.Single(f => f.Feature == "scale").Reporting.ShouldBe(0); // the register does not report it yet
        o.Features.Single(f => f.Feature == "ecfLive").Stores.ShouldBe(1); // R12 #9: Loyal sends to the DGII in production
    }

    [Fact]
    public void A_store_counts_as_active_from_its_heartbeat_too()
    {
        var usage = new ReplicaDailyUsage { TenantId = Fresh.Id, StoreId = FreshStore.Id, Day = Today, Sales = 4, SentUtc = Now };
        var m = Metrics([usage]);

        m.Overview().ActiveToday.ShouldBe(2);
        m.Stage(Fresh.Id).ShouldBe(FunnelStages.FirstSale);
        m.Daily(2).ShouldBe([new DailyActivity(Today.AddDays(-1), 1, 1, 100m), new DailyActivity(Today, 2, 1, 100m)]);
    }

    [Fact]
    public void Churn_signals_flag_stores_that_stopped_selling_or_went_silent()
    {
        var churn = Metrics().Overview().Churn;

        var quiet = churn.ShouldHaveSingleItem();
        (quiet.StoreId, quiet.Kind, quiet.LastSaleDay).ShouldBe((QuietStore.Id, "Disconnected", Today.AddDays(-11)));
        // Fresh's PC registered 20 hours ago without sending yet: not a signal before 3 days.
    }

    [Fact]
    public void Retention_groups_accounts_by_signup_week()
    {
        var cohorts = Metrics().Retention(4);

        cohorts.Select(c => c.Week).ShouldBe([new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 12)]);
        var september = cohorts.Single(c => c.Week == new DateOnly(2026, 9, 21)); // Loyal and Quiet signed up on 2026-09-25
        september.Tenants.ShouldBe(2);
        september.ActiveByWeek.ShouldBe([0, 1, 1, 1]); // Quiet sold the weekend of 10-03 only; Loyal from 10-06 on
        var thisWeek = cohorts.Single(c => c.Week == new DateOnly(2026, 10, 12)); // Fresh and Lead: no sales yet
        (thisWeek.Tenants, thisWeek.ActiveByWeek.Single()).ShouldBe((2, 0));
    }

    [Fact]
    public void Per_tenant_metrics()
    {
        var loyal = Metrics().ForTenant(Loyal.Id)!;

        (loyal.Stage, loyal.DaysWithSalesLast30, loyal.SalesLast30Days, loyal.AmountLast30Days).ShouldBe((FunnelStages.SevenDays, 10, 10, 1000m));
        (loyal.FirstSaleDay, loyal.LastSaleDay).ShouldBe((Today.AddDays(-9), Today));
        Metrics().ForTenant(Guid.NewGuid()).ShouldBeNull();
    }
}
