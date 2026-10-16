using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Ingest;
using Abastalia.Host.Cloud.Panel;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Platform;

/// <param name="OwnerEmail">An Owner of the tenant (the first by e-mail).</param>
/// <param name="LastActivityUtc">Newest batch received from any of its PCs.</param>
public sealed record PlatformTenant(Guid Id, string Name, string? OwnerEmail, DateTime CreatedUtc, DateTime? DisabledUtc, int Stores, int Nodes,
    DateTime? LastActivityUtc, string Stage, string? AppVersion, string? LicenseStatus = null, DateTime? LicenseEndsUtc = null);
/// <param name="HeartbeatUtc">When its newest heartbeat was sent; null until it sends one (stores on versions before TK-190).</param>
public sealed record PlatformNode(Guid Id, Guid StoreId, string Name, DateTime CreatedUtc, DateTime? RevokedUtc, DateTime? LastSyncUtc, DateTime? LastEventUtc,
    int PendingEvents, int FailedEvents, DateTime? HeartbeatUtc, string? AppVersion, string? Os, int? UptimeMinutes, int? OutboxBacklog, string? LastErrorCode);
public sealed record PlatformStore(Guid Id, string Name, DateTime CreatedUtc, int Registers, int Users, DateOnly? FirstSaleDay, DateOnly? LastSaleDay,
    IReadOnlyList<PlatformNode> Nodes);
/// <summary>A connection code, without the code itself.</summary>
public sealed record PlatformConnectionCode(Guid StoreId, DateTime IssuedUtc, DateTime ExpiresUtc, DateTime? UsedUtc);
/// <summary>TK-217: a panel member of a tenant.</summary>
public sealed record PlatformMember(string? Email, MemberRole Role, DateTime JoinedUtc);
public sealed record PlatformTenantDetail(Guid Id, string Name, DateTime CreatedUtc, DateTime? DisabledUtc, IReadOnlyList<PlatformMember> Members,
    IReadOnlyList<PlatformStore> Stores, IReadOnlyList<PlatformConnectionCode> ConnectionCodes, IReadOnlyList<PanelIssue> Issues, TenantMetrics Metrics);
/// <param name="SequencesLoaded">The store already issued an e-CF (the cloud learns of e-NCF sequences only through issued documents).</param>
/// <param name="BusinessNameSet">Null until a heartbeat arrives (the cloud gets no business profile event).</param>
public sealed record SetupChecklist(bool Connected, bool HeartbeatReceived, bool? BusinessNameSet, bool? RncSet, bool SequencesLoaded, int? ActiveProducts,
    DateOnly? FirstSaleDay, string? FiscalProvider);
/// <param name="LicenseStatus">TK-196: Trial, Active, Grace or Restricted from the newest heartbeat; null from older versions.</param>
public sealed record StoreFeatures(bool OnAccount, bool? ScaleConfigured, bool? PrinterConfigured, string FiscalProvider, int ActiveRegisters, int OpenShifts,
    string? LicenseStatus = null, DateTime? LicenseEndsUtc = null);
/// <param name="Sales">Completed sales in the replica.</param>
/// <param name="ReportedSales">What the store's heartbeat counted for that day (null without heartbeat).</param>
public sealed record PlatformStoreDay(DateOnly Day, int Sales, decimal Amount, int? ReportedSales, int? ActiveCashiers, int? Voids);
public sealed record PlatformStoreDetail(Guid Id, Guid TenantId, string TenantName, string Name, DateTime CreatedUtc, SetupChecklist Checklist, StoreFeatures? Features,
    IReadOnlyList<PlatformNode> Nodes, IReadOnlyList<PlatformStoreDay> Days, CertificationProgress? Certification);

/// <summary>Cross-tenant reads for Abastalia staff (TK-189). Read-only; every call is audited by the endpoint filter.</summary>
public sealed class PlatformQueries(CloudDbContext db, PanelQueries panel, TimeProvider clock)
{
    public async Task<IReadOnlyList<PlatformTenant>> TenantsAsync(string? search, CancellationToken ct)
    {
        var metrics = await MetricsAsync(ct);
        var tenants = await db.Tenants.AsNoTracking().OrderByDescending(t => t.CreatedUtc).ToListAsync(ct);
        var stores = await db.Stores.AsNoTracking().ToListAsync(ct);
        var nodes = await db.Nodes.AsNoTracking().ToListAsync(ct);
        var owners = await MembersAsync(null, ct);
        var versions = await db.NodeHeartbeats.AsNoTracking().Select(h => new { h.TenantId, h.SentUtc, h.AppVersion, h.LicenseStatus, h.LicenseEndsUtc }).ToListAsync(ct);

        var term = search?.Trim() ?? "";
        bool Matches(Tenant t) => term.Length == 0
            || Contains(t.Name) || (owners.GetValueOrDefault(t.Id) ?? []).Any(m => Contains(m.Email))
            || stores.Any(s => s.TenantId == t.Id && (Contains(s.Name) || s.Id.ToString() == term))
            || nodes.Any(n => n.TenantId == t.Id && (Contains(n.Name) || n.Id.ToString() == term))
            || t.Id.ToString() == term;
        bool Contains(string? value) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

        return [.. tenants.Where(Matches).Select(t => new PlatformTenant(t.Id, t.Name, owners.GetValueOrDefault(t.Id)?.FirstOrDefault(m => m.Role == MemberRole.Owner)?.Email, t.CreatedUtc, t.DisabledUtc,
            stores.Count(s => s.TenantId == t.Id), nodes.Count(n => n.TenantId == t.Id && n.RevokedUtc is null),
            nodes.Where(n => n.TenantId == t.Id).Max(n => n.LastSyncUtc), metrics.Stage(t.Id),
            versions.Where(v => v.TenantId == t.Id).MaxBy(v => v.SentUtc)?.AppVersion,
            versions.Where(v => v.TenantId == t.Id && v.LicenseStatus != null).MaxBy(v => v.SentUtc)?.LicenseStatus,
            versions.Where(v => v.TenantId == t.Id && v.LicenseStatus != null).MaxBy(v => v.SentUtc)?.LicenseEndsUtc))];
    }

    public async Task<PlatformTenantDetail?> TenantAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null) return null;
        var stores = await db.Stores.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.Name).ToListAsync(ct);
        var nodes = await NodesAsync(tenantId, null, ct);
        var registers = await db.Registers.AsNoTracking().Where(r => r.TenantId == tenantId).GroupBy(r => r.StoreId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var users = await db.StoreUsers.AsNoTracking().Where(u => u.TenantId == tenantId && u.Active).GroupBy(u => u.StoreId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var sold = await CompletedSales(tenantId).GroupBy(s => s.StoreId).Select(g => new { g.Key, First = g.Min(s => s.SoldUtc), Last = g.Max(s => s.SoldUtc) }).ToListAsync(ct);
        var codes = await db.ConnectionCodes.AsNoTracking().Where(c => c.TenantId == tenantId).OrderByDescending(c => c.ExpiresUtc).Take(20)
            .Select(c => new PlatformConnectionCode(c.StoreId, c.ExpiresUtc - Accounts.StoreEndpoints.CodeLifetime, c.ExpiresUtc, c.UsedUtc)).ToListAsync(ct);

        return new PlatformTenantDetail(tenant.Id, tenant.Name, tenant.CreatedUtc, tenant.DisabledUtc, (await MembersAsync(tenantId, ct)).GetValueOrDefault(tenantId) ?? [],
            [.. stores.Select(s =>
            {
                var sales = sold.FirstOrDefault(x => x.Key == s.Id);
                return new PlatformStore(s.Id, s.Name, s.CreatedUtc, registers.FirstOrDefault(r => r.Key == s.Id)?.Count ?? 0, users.FirstOrDefault(u => u.Key == s.Id)?.Count ?? 0,
                    sales is null ? null : BusinessDay.Of(sales.First), sales is null ? null : BusinessDay.Of(sales.Last), [.. nodes.Where(n => n.StoreId == s.Id)]);
            })],
            codes, await IssuesAsync(tenantId, ct), (await MetricsAsync(ct)).ForTenant(tenantId)!);
    }

    public async Task<PlatformStoreDetail?> StoreAsync(Guid storeId, CancellationToken ct)
    {
        var store = await db.Stores.AsNoTracking().SingleOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null) return null;
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == store.TenantId, ct);
        var nodes = await NodesAsync(store.TenantId, storeId, ct);
        var heartbeat = await db.NodeHeartbeats.AsNoTracking().Where(h => h.TenantId == store.TenantId && h.StoreId == storeId).OrderByDescending(h => h.SentUtc).FirstOrDefaultAsync(ct);
        var firstSale = await CompletedSales(store.TenantId).Where(s => s.StoreId == storeId).OrderBy(s => s.SoldUtc).Select(s => (DateTime?)s.SoldUtc).FirstOrDefaultAsync(ct);
        var issued = await db.FiscalDocuments.AsNoTracking().AnyAsync(d => d.TenantId == store.TenantId && d.StoreId == storeId, ct);

        var today = BusinessDay.Of(clock.GetUtcNow().UtcDateTime);
        var from = today.AddDays(-13);
        var (start, _) = BusinessDay.UtcRange(from, today);
        var sales = await CompletedSales(store.TenantId).Where(s => s.StoreId == storeId && s.SoldUtc >= start).Select(s => new { s.SoldUtc, s.Total }).ToListAsync(ct);
        var usage = await db.DailyUsage.AsNoTracking().Where(u => u.TenantId == store.TenantId && u.StoreId == storeId && u.Day >= from).ToListAsync(ct);
        var days = Enumerable.Range(0, 14).Select(i => today.AddDays(-i)).Select(day =>
        {
            var sold = sales.Where(s => BusinessDay.Of(s.SoldUtc) == day).ToList();
            var u = usage.FirstOrDefault(x => x.Day == day);
            return new PlatformStoreDay(day, sold.Count, sold.Sum(s => s.Total), u?.Sales, u?.ActiveCashiers, u?.Voids);
        }).ToList();

        return new PlatformStoreDetail(store.Id, store.TenantId, tenant.Name, store.Name, store.CreatedUtc,
            new SetupChecklist(nodes.Any(n => n.RevokedUtc is null), heartbeat is not null, heartbeat?.BusinessNameSet, heartbeat?.RncSet, issued, heartbeat?.ActiveProducts,
                firstSale is null ? null : BusinessDay.Of(firstSale.Value), heartbeat?.FiscalProvider),
            heartbeat is null ? null : new StoreFeatures(heartbeat.OnAccount, heartbeat.ScaleConfigured, heartbeat.PrinterConfigured, heartbeat.FiscalProvider,
                heartbeat.ActiveRegisters, heartbeat.OpenShifts, heartbeat.LicenseStatus, heartbeat.LicenseEndsUtc),
                        nodes, days, await db.Certifications.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == store.TenantId && c.StoreId == storeId, ct) is { } cert
                ? CertificationProgress.Of(cert, tenant.Name, store.Name, clock.GetUtcNow().UtcDateTime)
                : null);

    }

    /// <summary>One snapshot of everything the product metrics read.</summary>
    public async Task<ProductMetrics> MetricsAsync(CancellationToken ct)
    {
        // R8 #10: summed in SQL by store and store day (UTC−4), one row per store and day instead of one per sale.
        var voided = db.SaleVoids.Select(v => new { v.TenantId, v.SaleId });
        var offsetHours = BusinessDay.Offset.TotalHours;
        var sales = await db.Sales.AsNoTracking().Where(s => !voided.Any(v => v.TenantId == s.TenantId && v.SaleId == s.Id))
            .GroupBy(s => new { s.TenantId, s.StoreId, Day = s.SoldUtc.AddHours(offsetHours).Date })
            .Select(g => new { g.Key.TenantId, g.Key.StoreId, g.Key.Day, Count = g.Count(), Total = g.Sum(s => s.Total) }).ToListAsync(ct);
        var types = Projectors.ByType.Keys.ToList();
        var failed = await db.Inbox.CountAsync(i => i.AppliedUtc == null && i.Attempts >= InboxApplier.MaxAttempts && types.Contains(i.Type), ct);
        return new ProductMetrics(
            await db.Tenants.AsNoTracking().ToListAsync(ct), await db.Stores.AsNoTracking().ToListAsync(ct), await db.Nodes.AsNoTracking().ToListAsync(ct),
            [.. sales.Select(s => new SaleFact(s.TenantId, s.StoreId, DateOnly.FromDateTime(s.Day), s.Total, s.Count))],
            await db.NodeHeartbeats.AsNoTracking().ToListAsync(ct), await db.DailyUsage.AsNoTracking().ToListAsync(ct), failed, clock.GetUtcNow().UtcDateTime,
            await db.Certifications.AsNoTracking().ToListAsync(ct));
    }

    /// <summary>What the owner's panel would flag, plus cash shortages of the last 7 days.</summary>
    private async Task<IReadOnlyList<PanelIssue>> IssuesAsync(Guid tenantId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var issues = (await panel.ShiftsAsync(new PanelFilter(tenantId, null, panel.Today.AddDays(-6), panel.Today), onlyDifferences: true, ct))
            .Where(s => s.Difference < 0)
            .Select(s => new PanelIssue("ShiftDifference", s.StoreId, s.StoreName, s.ShiftId, s.Difference, s.ClosedUtc,
                $"Z #{s.ZNumber}: faltante de {PanelQueries.Rd(-s.Difference)}", s.CashierName, s.RegisterName)).ToList();
        var sync = await panel.SyncStatusAsync(tenantId, ct);
        issues.AddRange(sync.Where(s => s.Nodes.Count > 0 && (s.LastSyncUtc is null || now - s.LastSyncUtc > PanelQueries.SyncStaleAfter))
            .Select(s => new PanelIssue("SyncStale", s.StoreId, s.StoreName, null, null, s.LastSyncUtc, "La tienda no ha enviado datos en más de 24 horas.")));
        issues.AddRange(sync.Where(s => s.FailedEvents > 0)
            .Select(s => new PanelIssue("EventsFailed", s.StoreId, s.StoreName, null, s.FailedEvents, null, $"{s.FailedEvents} datos recibidos no se pudieron procesar.")));
        return issues;
    }

    private async Task<List<PlatformNode>> NodesAsync(Guid tenantId, Guid? storeId, CancellationToken ct)
    {
        var nodes = await db.Nodes.AsNoTracking().Where(n => n.TenantId == tenantId && (storeId == null || n.StoreId == storeId)).OrderBy(n => n.CreatedUtc).ToListAsync(ct);
        var heartbeats = await db.NodeHeartbeats.AsNoTracking().Where(h => h.TenantId == tenantId).ToDictionaryAsync(h => h.NodeId, ct);
        var types = Projectors.ByType.Keys.ToList();
        var waiting = await db.Inbox.AsNoTracking().Where(i => i.TenantId == tenantId && i.AppliedUtc == null && types.Contains(i.Type))
            .GroupBy(i => new { i.NodeId, Failed = i.Attempts >= InboxApplier.MaxAttempts }).Select(g => new { g.Key.NodeId, g.Key.Failed, Count = g.Count() })
            .ToListAsync(ct);
        return [.. nodes.Select(n =>
        {
            var h = heartbeats.GetValueOrDefault(n.Id);
            return new PlatformNode(n.Id, n.StoreId, n.Name, n.CreatedUtc, n.RevokedUtc, n.LastSyncUtc, n.LastEventUtc,
                waiting.Where(w => w.NodeId == n.Id && !w.Failed).Sum(w => w.Count), waiting.Where(w => w.NodeId == n.Id && w.Failed).Sum(w => w.Count),
                h?.SentUtc, h?.AppVersion, h?.Os, h?.UptimeMinutes, h?.OutboxBacklog, h?.LastErrorCode);
        })];
    }

    /// <summary>Panel members by tenant (null = every tenant), Owners first (TK-217).</summary>
    private async Task<Dictionary<Guid, List<PlatformMember>>> MembersAsync(Guid? tenantId, CancellationToken ct) =>
        (await db.TenantMembers.AsNoTracking().Where(m => tenantId == null || m.TenantId == tenantId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.TenantId, Member = new PlatformMember(u.Email, m.Role, m.JoinedUtc) }).ToListAsync(ct))
        .GroupBy(x => x.TenantId).ToDictionary(g => g.Key,
            g => g.Select(x => x.Member).OrderBy(m => m.Role).ThenBy(m => m.Email, StringComparer.OrdinalIgnoreCase).ToList());

    private IQueryable<ReplicaSale> CompletedSales(Guid tenantId)
    {
        var voids = db.SaleVoids.Where(v => v.TenantId == tenantId).Select(v => v.SaleId);
        return db.Sales.AsNoTracking().Where(s => s.TenantId == tenantId && !voids.Contains(s.Id));
    }
}
