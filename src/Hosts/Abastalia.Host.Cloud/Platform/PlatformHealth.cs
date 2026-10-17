using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Quotas;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Platform;

/// <param name="PerIpRejections15Min">Per-IP 429s of the node gate in 15 minutes that alert (<c>Watch:PerIpRejectionsPer15Minutes</c>).</param>
/// <param name="StoresBehindIp">Stores behind one IP getting the per-IP 429 that alert (<c>Watch:StoresBehindIp</c>).</param>
public sealed record WatchThresholds(int PerIpRejections15Min, int StoresBehindIp, int SilentStoreHours, int QueueStaleHours, int QuotaNearPercent);
public sealed record WatchReason(string Reason, string Detail, int Count, int RetryAfter);
/// <param name="Peak15Min">The most per-IP 429s of the node gate in any 15 minutes of the window.</param>
/// <param name="Stores">The most distinct stores seen behind it in one minute.</param>
public sealed record WatchIp(string Network, string IpKey, int Count, int Peak15Min, int Stores, IReadOnlyList<string> Reasons);
public sealed record WatchQuota(Guid TenantId, string TenantName, Meter Meter, int Used, int Limit);
/// <param name="QueueHours">How long the oldest e-CF has waited for the DGII, by the newest heartbeat; null when nothing waits.</param>
public sealed record WatchStore(Guid TenantId, string TenantName, Guid StoreId, string StoreName, DateTime LastHeartbeatUtc, double? QueueHours);
public sealed record PlatformWatchReport(int Days, DateTime SinceUtc, WatchThresholds Thresholds, IReadOnlyList<WatchReason> Reasons, IReadOnlyList<WatchIp> Ips,
    IReadOnlyList<WatchQuota> Quotas, IReadOnlyList<WatchStore> SilentStores, IReadOnlyList<WatchStore> EcfQueues);

/// <summary>TK-311: "Salud de la plataforma" for Abastalia staff, and what the alerts need from the database (silent stores), logged while it
/// is awake anyway. Bounded reads: rows of the window by their index, one heartbeat per node, this month's usage.</summary>
public sealed class PlatformHealth(CloudDbContext db, LimitWatch watch, PlanLimits limits, IConfiguration config, TimeProvider clock, ILogger<PlatformHealth> log)
{
    public const int TopRows = 20;
    private static readonly string[] PerIp429 = [LimitReasons.PerIpConcurrency, LimitReasons.PerIpRate];

    public WatchThresholds Thresholds => new(config.GetValue("Watch:PerIpRejectionsPer15Minutes", 20), config.GetValue("Watch:StoresBehindIp", 3),
        config.GetValue("Watch:SilentStoreHours", 6), (int)Alerts.AlertNotifier.QueueStaleAfter.TotalHours, config.GetValue("Watch:QuotaNearPercent", 80));

    /// <summary>After the inbox applier's run: saves the watch rows and logs each store silent past <c>Watch:SilentStoreHours</c> (the alert
    /// reads the line). Stores silent over 7 days are left out: a closed store would alert forever.</summary>
    public async Task WhileAwakeAsync(CancellationToken ct)
    {
        await watch.SaveAsync(db, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var s in (await StoresAsync(ct)).Where(s => now - s.LastHeartbeatUtc > TimeSpan.FromHours(Thresholds.SilentStoreHours) && now - s.LastHeartbeatUtc < TimeSpan.FromDays(7)))
            log.LogWarning("Store silent: tenant {TenantId} store {StoreId}, last heartbeat {Hours} h ago", s.TenantId, s.StoreId, (int)(now - s.LastHeartbeatUtc).TotalHours);
    }

    public async Task<PlatformWatchReport> ReportAsync(int days, CancellationToken ct)
    {
        await watch.SaveAsync(db, ct); // the page shows the minute that is still in memory too
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now.AddDays(-days);
        var thresholds = Thresholds;
        var window = db.LimitRejections.AsNoTracking().Where(r => r.MinuteUtc >= since);

        var reasons = await window.GroupBy(r => new { r.Reason, r.Detail })
            .Select(g => new WatchReason(g.Key.Reason, g.Key.Detail, g.Sum(r => r.Count), g.Sum(r => r.RetryAfter))).ToListAsync(ct);
        var top = await window.Where(r => r.IpKey != "-").GroupBy(r => new { r.Network, r.IpKey })
            .Select(g => new { g.Key.Network, g.Key.IpKey, Count = g.Sum(r => r.Count), Stores = g.Max(r => r.Stores) })
            .OrderByDescending(g => g.Count).Take(TopRows).ToListAsync(ct);
        var keys = top.Select(t => t.IpKey).ToList();
        var detail = await window.Where(r => keys.Contains(r.IpKey)).Select(r => new { r.IpKey, r.Reason, r.MinuteUtc, r.Count }).Take(50_000).ToListAsync(ct);
        var ips = top.Select(t =>
        {
            var rows = detail.Where(d => d.IpKey == t.IpKey).ToList();
            var peak = rows.Where(d => PerIp429.Contains(d.Reason)).GroupBy(d => d.MinuteUtc.Ticks / (15 * TimeSpan.TicksPerMinute)).Select(g => g.Sum(d => d.Count))
                .DefaultIfEmpty(0).Max();
            return new WatchIp(t.Network, t.IpKey, t.Count, peak, t.Stores, [.. rows.Select(d => d.Reason).Distinct().Order(StringComparer.Ordinal)]);
        }).ToList();

        var stores = await StoresAsync(ct);
        var silentAfter = TimeSpan.FromHours(thresholds.SilentStoreHours);
        return new PlatformWatchReport(days, since, thresholds, [.. reasons.OrderByDescending(r => r.Count)], ips, await QuotasAsync(thresholds, ct),
            [.. stores.Where(s => now - s.LastHeartbeatUtc > silentAfter && now - s.LastHeartbeatUtc < TimeSpan.FromDays(30)).OrderBy(s => s.LastHeartbeatUtc).Take(TopRows)],
            [.. stores.Where(s => s.QueueHours is not null).OrderByDescending(s => s.QueueHours).Take(TopRows)]);
    }

    /// <summary>This month's meters at <see cref="WatchThresholds.QuotaNearPercent"/> of the limit or past it (TK-248); receiver signatures
    /// past it are the fair use of TK-286.</summary>
    private async Task<List<WatchQuota>> QuotasAsync(WatchThresholds thresholds, CancellationToken ct)
    {
        var day = BusinessDay.Of(clock.GetUtcNow().UtcDateTime);
        var month = new DateOnly(day.Year, day.Month, 1);
        var usage = await db.TenantUsage.AsNoTracking().Where(u => u.Month == month && u.Count > 0).ToListAsync(ct);
        var ids = usage.Select(u => u.TenantId).Distinct().ToList();
        var tenants = await db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        return [.. usage.Where(u => tenants.ContainsKey(u.TenantId)).Select(u =>
            {
                var t = tenants[u.TenantId];
                return new WatchQuota(t.Id, t.Name, u.Meter, u.Count,
                    limits.MonthlyLimit(string.IsNullOrEmpty(t.Plan) ? Quota.DefaultPlan : t.Plan, Quota.SplitAddOns(t.AddOns), u.Meter));
            })
            .Where(q => q.Limit <= 0 || q.Used * 100L >= (long)q.Limit * thresholds.QuotaNearPercent)
            .OrderByDescending(q => q.Limit <= 0 ? double.MaxValue : (double)q.Used / q.Limit).Take(TopRows * 2)];
    }

    /// <summary>Each connected store of an enabled business: its newest heartbeat and the e-CF queue it reported then.</summary>
    private async Task<List<WatchStore>> StoresAsync(CancellationToken ct)
    {
        var heartbeats = await db.NodeHeartbeats.AsNoTracking()
            .Where(h => db.Nodes.Any(n => n.Id == h.NodeId && n.RevokedUtc == null) && db.Tenants.Any(t => t.Id == h.TenantId && t.DisabledUtc == null))
            .Select(h => new { h.TenantId, h.StoreId, h.SentUtc, h.OldestPendingUtc }).ToListAsync(ct);
        var storeIds = heartbeats.Select(h => h.StoreId).Distinct().ToList();
        var names = await db.Stores.AsNoTracking().Where(s => storeIds.Contains(s.Id)).Join(db.Tenants, s => s.TenantId, t => t.Id, (s, t) => new { s.Id, Store = s.Name, Tenant = t.Name })
            .ToDictionaryAsync(s => s.Id, ct);
        return [.. heartbeats.GroupBy(h => h.StoreId).Select(g => g.MaxBy(h => h.SentUtc)!).Where(h => names.ContainsKey(h.StoreId)).Select(h =>
            new WatchStore(h.TenantId, names[h.StoreId].Tenant, h.StoreId, names[h.StoreId].Store, h.SentUtc,
                h.OldestPendingUtc is { } pending ? Math.Round(Math.Max(0, (h.SentUtc - pending).TotalHours), 1) : null))];
    }
}
