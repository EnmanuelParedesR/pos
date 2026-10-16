using Toki.Host.Cloud.Panel;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Platform;

/// <summary>A store's completed (not voided) sales of one store day.</summary>
/// <param name="Count">How many sales (R8 #10: the database sums them by store and day; tests may pass one sale per fact).</param>
public sealed record SaleFact(Guid TenantId, Guid StoreId, DateOnly Day, decimal Total, int Count = 1);

/// <summary>Funnel stages, in order. <c>SevenDays</c> = 7 different store days with sales.</summary>
public static class FunnelStages
{
    public const string Account = "account";
    public const string Store = "store";
    public const string Connected = "connected";
    public const string FirstSale = "firstSale";
    public const string SevenDays = "sevenDays";
    public static readonly string[] All = [Account, Store, Connected, FirstSale, SevenDays];
}

public sealed record FunnelStep(string Stage, int Tenants);
/// <param name="Reporting">Stores whose heartbeat (last 7 days) says whether they use it; the share is Stores / Reporting.</param>
public sealed record FeatureAdoption(string Feature, int Stores, int Reporting);
public sealed record VersionCount(string Version, int Nodes);
/// <param name="StaleNodes">Not revoked and nothing received for 24 hours.</param>
/// <param name="NodesWithErrors">Their newest heartbeat (last 7 days) reports an upload error.</param>
/// <param name="NodesWithBacklog">Their newest heartbeat reports more than <see cref="ProductMetrics.BacklogAlert"/> events waiting to go up.</param>
public sealed record HealthMetrics(int Nodes, int StaleNodes, int NodesWithErrors, int NodesWithBacklog, int FailedEvents, IReadOnlyList<VersionCount> Versions);
/// <param name="Kind"><c>NoSales</c> (sold before, nothing for 7 days) or <c>Disconnected</c> (its PC has sent nothing for 3 days).</param>
public sealed record ChurnSignal(Guid TenantId, string TenantName, Guid StoreId, string StoreName, string Kind, DateOnly? LastSaleDay, DateTime? LastSyncUtc);
public sealed record MetricsOverview(
    DateOnly Today, int Tenants, int Stores, int Nodes, int NodesOnline, int ActiveToday, int ActiveLast7Days, int ActiveLast30Days,
    int SalesLast30Days, decimal AmountLast30Days, IReadOnlyList<FunnelStep> Funnel, IReadOnlyList<FeatureAdoption> Features, HealthMetrics Health,
    IReadOnlyList<ChurnSignal> Churn);
public sealed record DailyActivity(DateOnly Day, int ActiveStores, int Sales, decimal Amount);
/// <param name="Week">Monday of the signup week (store days).</param>
/// <param name="ActiveByWeek">[0] = the signup week, [1] = the next one…: tenants of the cohort with at least one active store that week.</param>
public sealed record RetentionCohort(DateOnly Week, int Tenants, IReadOnlyList<int> ActiveByWeek);
public sealed record TenantMetrics(Guid TenantId, string Stage, int DaysWithSalesLast30, int SalesLast30Days, decimal AmountLast30Days, DateOnly? FirstSaleDay,
    DateOnly? LastSaleDay);

/// <summary>Product metrics (TK-190, docs/metricas-producto.md) over one snapshot of the cloud: accounts, stores, nodes, the replica's sales
/// and the nodes' heartbeats. Pure: tests feed it fixtures. A store is <b>active</b> on a store day when it has a completed sale that day in the
/// replica, or its heartbeat reported sales for that day (the store's own count, in case some sale has not been applied yet).</summary>
/// <remarks>Sales arrive summed by store and day (R8 #10); the rest is one row per tenant, store, node or heartbeat.
/// shortcut: those rows are loaded whole on every call; cache or roll up when there are thousands of stores.</remarks>
public sealed class ProductMetrics
{
    public static readonly TimeSpan DisconnectedAfter = TimeSpan.FromDays(3);
    public const int NoSalesDays = 7;
    public const int BacklogAlert = 500;

    private readonly IReadOnlyList<Tenant> _tenants;
    private readonly IReadOnlyList<Store> _stores;
    private readonly IReadOnlyList<Node> _nodes;
    private readonly IReadOnlyList<SaleFact> _sales;
    private readonly IReadOnlyList<ReplicaNodeHeartbeat> _heartbeats;
    private readonly int _failedEvents;
    private readonly DateTime _now;
    private readonly DateOnly _today;
    private readonly ILookup<Guid, DateOnly> _activeDays; // by store

    public ProductMetrics(IReadOnlyList<Tenant> tenants, IReadOnlyList<Store> stores, IReadOnlyList<Node> nodes, IReadOnlyList<SaleFact> sales,
        IReadOnlyList<ReplicaNodeHeartbeat> heartbeats, IReadOnlyList<ReplicaDailyUsage> usage, int failedEvents, DateTime nowUtc)
    {
        (_tenants, _stores, _nodes, _sales, _heartbeats, _failedEvents, _now) = (tenants, stores, nodes, sales, heartbeats, failedEvents, nowUtc);
        _today = BusinessDay.Of(nowUtc);
        _activeDays = sales.Select(s => (s.StoreId, s.Day)).Concat(usage.Where(u => u.Sales > 0).Select(u => (u.StoreId, u.Day)))
            .Distinct().ToLookup(x => x.StoreId, x => x.Day);
    }

    private HashSet<Guid> Disabled => [.. _tenants.Where(t => t.DisabledUtc is not null).Select(t => t.Id)];

    public MetricsOverview Overview()
    {
        var disabled = Disabled;
        var liveNodes = _nodes.Where(n => n.RevokedUtc is null && !disabled.Contains(n.TenantId)).ToList();
        var recent = RecentHeartbeats();
        var month = _sales.Where(s => s.Day > _today.AddDays(-30)).ToList();
        return new MetricsOverview(
            _today, _tenants.Count, _stores.Count, liveNodes.Count, liveNodes.Count(n => !Stale(n.LastSyncUtc)),
            ActiveStores(_today, _today), ActiveStores(_today.AddDays(-6), _today), ActiveStores(_today.AddDays(-29), _today),
            month.Sum(s => s.Count), month.Sum(s => s.Total),
            [.. FunnelStages.All.Select(stage => new FunnelStep(stage, _tenants.Count(t => Reached(Stage(t.Id), stage))))],
            Features(recent),
            new HealthMetrics(liveNodes.Count, liveNodes.Count(n => Stale(n.LastSyncUtc)),
                recent.Count(h => h.LastErrorCode is not null), recent.Count(h => h.OutboxBacklog > BacklogAlert), _failedEvents,
                [.. recent.GroupBy(h => h.AppVersion).Select(g => new VersionCount(g.Key, g.Count())).OrderByDescending(v => v.Nodes).ThenByDescending(v => v.Version)]),
            Churn(disabled));
    }

    public IReadOnlyList<DailyActivity> Daily(int days) =>
        [.. Enumerable.Range(0, days).Select(i => _today.AddDays(i - days + 1)).Select(day =>
        {
            var sold = _sales.Where(s => s.Day == day).ToList();
            return new DailyActivity(day, ActiveStores(day, day), sold.Sum(s => s.Count), sold.Sum(s => s.Total));
        })];

    public IReadOnlyList<RetentionCohort> Retention(int weeks)
    {
        var thisWeek = Monday(_today);
        return [.. Enumerable.Range(0, weeks).Select(i => thisWeek.AddDays(7 * (i - weeks + 1))).Select(week =>
        {
            var cohort = _tenants.Where(t => Monday(BusinessDay.Of(t.CreatedUtc)) == week).Select(t => t.Id).ToList();
            var elapsed = (thisWeek.DayNumber - week.DayNumber) / 7;
            return new RetentionCohort(week, cohort.Count,
                [.. Enumerable.Range(0, elapsed + 1).Select(k => cohort.Count(t => ActiveIn(t, week.AddDays(7 * k), week.AddDays(7 * k + 6))))]);
        })];
    }

    public TenantMetrics? ForTenant(Guid tenantId)
    {
        if (_tenants.All(t => t.Id != tenantId)) return null;
        var stores = _stores.Where(s => s.TenantId == tenantId).Select(s => s.Id).ToHashSet();
        var days = stores.SelectMany(s => _activeDays[s]).Distinct().ToList();
        var month = _sales.Where(s => s.TenantId == tenantId && s.Day > _today.AddDays(-30)).ToList();
        return new TenantMetrics(tenantId, Stage(tenantId), days.Count(d => d > _today.AddDays(-30)), month.Sum(s => s.Count), month.Sum(s => s.Total),
            days.Count == 0 ? null : days.Min(), days.Count == 0 ? null : days.Max());
    }

    /// <summary>The furthest funnel stage the tenant reached.</summary>
    public string Stage(Guid tenantId)
    {
        var stores = _stores.Where(s => s.TenantId == tenantId).Select(s => s.Id).ToList();
        var days = stores.SelectMany(s => _activeDays[s]).Distinct().Count();
        return days >= 7 ? FunnelStages.SevenDays
            : days > 0 ? FunnelStages.FirstSale
            : _nodes.Any(n => n.TenantId == tenantId) ? FunnelStages.Connected
            : stores.Count > 0 ? FunnelStages.Store
            : FunnelStages.Account;
    }

    private static bool Reached(string stage, string step) => Array.IndexOf(FunnelStages.All, stage) >= Array.IndexOf(FunnelStages.All, step);

    private bool Stale(DateTime? lastSyncUtc) => lastSyncUtc is null || _now - lastSyncUtc > PanelQueries.SyncStaleAfter;

    private int ActiveStores(DateOnly from, DateOnly to) => _stores.Count(s => _activeDays[s.Id].Any(d => d >= from && d <= to));

    private bool ActiveIn(Guid tenantId, DateOnly from, DateOnly to) => _stores.Any(s => s.TenantId == tenantId && _activeDays[s.Id].Any(d => d >= from && d <= to));

    /// <summary>The newest heartbeat of each live node, if it came in the last 7 days.</summary>
    private List<ReplicaNodeHeartbeat> RecentHeartbeats()
    {
        var live = _nodes.Where(n => n.RevokedUtc is null).Select(n => n.Id).ToHashSet();
        var disabled = Disabled;
        return [.. _heartbeats.Where(h => live.Contains(h.NodeId) && !disabled.Contains(h.TenantId) && _now - h.SentUtc <= TimeSpan.FromDays(7))];
    }

    /// <summary>Per store (its newest heartbeat), not per node: a store with two PCs counts once.</summary>
    private static List<FeatureAdoption> Features(List<ReplicaNodeHeartbeat> recent)
    {
        var stores = recent.GroupBy(h => h.StoreId).Select(g => g.MaxBy(h => h.SentUtc)!).ToList();
        FeatureAdoption Of(string feature, Func<ReplicaNodeHeartbeat, bool?> uses) =>
            new(feature, stores.Count(h => uses(h) == true), stores.Count(h => uses(h) is not null));
        return
        [
            Of("onAccount", h => h.OnAccount),
            Of("multipleRegisters", h => h.ActiveRegisters > 1),
            Of("ecfRelay", h => h.FiscalProvider == "Relay"),
            Of("scale", h => h.ScaleConfigured),
            Of("printer", h => h.PrinterConfigured),
        ];
    }

    private List<ChurnSignal> Churn(HashSet<Guid> disabled)
    {
        var tenants = _tenants.ToDictionary(t => t.Id, t => t.Name);
        var signals = new List<ChurnSignal>();
        foreach (var store in _stores.Where(s => !disabled.Contains(s.TenantId)))
        {
            var days = _activeDays[store.Id].ToList();
            DateOnly? lastSale = days.Count == 0 ? null : days.Max();
            var nodes = _nodes.Where(n => n.StoreId == store.Id && n.RevokedUtc is null).ToList();
            var lastSync = nodes.Max(n => n.LastSyncUtc);
            // A PC registered today that has not sent anything yet gets the same 3 days.
            if (nodes.Count > 0 && _now - nodes.Max(n => n.LastSyncUtc ?? n.CreatedUtc) > DisconnectedAfter)
                signals.Add(new ChurnSignal(store.TenantId, tenants.GetValueOrDefault(store.TenantId, ""), store.Id, store.Name, "Disconnected", lastSale, lastSync));
            else if (lastSale is { } last && last <= _today.AddDays(-NoSalesDays))
                signals.Add(new ChurnSignal(store.TenantId, tenants.GetValueOrDefault(store.TenantId, ""), store.Id, store.Name, "NoSales", lastSale, lastSync));
        }
        return [.. signals.OrderBy(s => s.LastSaleDay ?? DateOnly.MinValue)];
    }

    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
