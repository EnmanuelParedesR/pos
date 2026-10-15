using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Toki.Host.Cloud.Ingest;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Panel;

/// <summary>What a panel or assistant query covers. The tenant comes from the caller's identity; days are store days (UTC−4).</summary>
public sealed record PanelFilter(Guid TenantId, Guid? StoreId, DateOnly From, DateOnly To);

public sealed record HourSales(int Hour, int Count, decimal Total);
public sealed record PaymentMix(string Method, int Count, decimal Amount);
public sealed record PanelIssue(string Kind, Guid StoreId, string StoreName, Guid? ReferenceId, decimal? Amount, DateTime? OccurredUtc, string Message);
public sealed record Summary(
    DateOnly Date, decimal SalesTotal, int SalesCount, decimal AverageTicket, decimal DiscountTotal, decimal ItbisTotal, decimal ReturnsTotal, decimal NetTotal,
    int VoidedCount, IReadOnlyList<HourSales> ByHour, IReadOnlyList<PaymentMix> PaymentMix, IReadOnlyList<PanelIssue> Issues, DateTime? LastEventUtc);
public sealed record DaySales(DateOnly Date, int Count, decimal Total, decimal Discount, decimal Itbis, decimal Returns, decimal Net, int VoidedCount);
public sealed record ProductSales(Guid ProductId, string Description, decimal Quantity, decimal Total, decimal Profit);
public sealed record ShiftDifference(
    Guid ShiftId, Guid StoreId, string StoreName, Guid RegisterId, Guid CashierId, int ZNumber, decimal ExpectedCash, decimal CountedCash, decimal Difference, DateTime ClosedUtc);
public sealed record CashierSales(Guid CashierId, int Count, decimal Total, int VoidedCount);
public sealed record NodeStatus(Guid NodeId, string Name, DateTime? LastEventUtc, DateTime? LastSyncUtc);
public sealed record StoreSyncStatus(Guid StoreId, string StoreName, DateTime? LastEventUtc, DateTime? LastSyncUtc, int PendingEvents, int FailedEvents, IReadOnlyList<NodeStatus> Nodes);

/// <summary>Read-only queries over the replica for the panel and the assistant. Every query filters by the tenant first.
/// Voided sales are left out; returns are subtracted on the day they happened (same rules as the store's reports).</summary>
/// <remarks>shortcut: aggregates in memory over at most a year of one-row-per-sale data (Sqlite cannot sum decimals); move to SQL GROUP BY when a tenant has many stores.</remarks>
public sealed class PanelQueries(CloudDbContext db, TimeProvider clock)
{
    /// <summary>A store with no batch for this long shows up as an issue.</summary>
    public static readonly TimeSpan SyncStaleAfter = TimeSpan.FromHours(24);

    public DateOnly Today => BusinessDay.Of(clock.GetUtcNow().UtcDateTime);

    public async Task<Summary> SummaryAsync(Guid tenantId, Guid? storeId, DateOnly date, CancellationToken ct)
    {
        var f = new PanelFilter(tenantId, storeId, date, date);
        var (from, to) = BusinessDay.UtcRange(date, date);
        var sales = await CompletedSales(f).ToListAsync(ct);
        var returns = await Rows(db.Returns, f).Where(r => r.ReturnedUtc >= from && r.ReturnedUtc < to).Select(r => r.Total).ToListAsync(ct);
        var voided = await VoidedSales(f).CountAsync(ct);
        var total = sales.Sum(s => s.Total);

        var shifts = await ShiftsAsync(f, onlyDifferences: true, ct);
        var issues = shifts.Select(s => new PanelIssue("ShiftDifference", s.StoreId, s.StoreName, s.ShiftId, s.Difference, s.ClosedUtc,
            $"Z #{s.ZNumber}: {(s.Difference < 0 ? "faltante" : "sobrante")} de {Rd(Math.Abs(s.Difference))}")).ToList();
        var sync = await SyncStatusAsync(tenantId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        issues.AddRange(sync.Where(s => (storeId is null || s.StoreId == storeId) && s.Nodes.Count > 0 && (s.LastSyncUtc is null || now - s.LastSyncUtc > SyncStaleAfter))
            .Select(s => new PanelIssue("SyncStale", s.StoreId, s.StoreName, null, null, s.LastSyncUtc, "La tienda no ha enviado datos en más de 24 horas.")));
        issues.AddRange(sync.Where(s => (storeId is null || s.StoreId == storeId) && s.FailedEvents > 0)
            .Select(s => new PanelIssue("EventsFailed", s.StoreId, s.StoreName, null, s.FailedEvents, null, $"{s.FailedEvents} datos recibidos no se pudieron procesar.")));

        return new Summary(date, total, sales.Count, sales.Count == 0 ? 0 : Money.Round(total / sales.Count), sales.Sum(s => s.Discount), sales.Sum(s => s.Itbis),
            returns.Sum(), total - returns.Sum(), voided,
            [.. sales.GroupBy(s => (s.SoldUtc + BusinessDay.Offset).Hour).OrderBy(g => g.Key).Select(g => new HourSales(g.Key, g.Count(), g.Sum(s => s.Total)))],
            await PaymentMixAsync(f, ct), issues,
            sync.Where(s => storeId is null || s.StoreId == storeId).Max(s => s.LastEventUtc));
    }

    /// <summary>Every day of the range, also the ones without sales (for charts).</summary>
    public async Task<IReadOnlyList<DaySales>> SalesByDayAsync(PanelFilter f, CancellationToken ct)
    {
        var (from, to) = BusinessDay.UtcRange(f.From, f.To);
        var sales = await CompletedSales(f).Select(s => new { s.SoldUtc, s.Total, s.Discount, s.Itbis }).ToListAsync(ct);
        var voided = await VoidedSales(f).Select(s => s.SoldUtc).ToListAsync(ct);
        var returns = await Rows(db.Returns, f).Where(r => r.ReturnedUtc >= from && r.ReturnedUtc < to).Select(r => new { r.ReturnedUtc, r.Total }).ToListAsync(ct);

        var days = new List<DaySales>();
        for (var day = f.From; day <= f.To; day = day.AddDays(1))
        {
            var d = day;
            var ofDay = sales.Where(s => BusinessDay.Of(s.SoldUtc) == d).ToList();
            var returned = returns.Where(r => BusinessDay.Of(r.ReturnedUtc) == d).Sum(r => r.Total);
            var total = ofDay.Sum(s => s.Total);
            days.Add(new DaySales(d, ofDay.Count, total, ofDay.Sum(s => s.Discount), ofDay.Sum(s => s.Itbis), returned, total - returned,
                voided.Count(v => BusinessDay.Of(v) == d)));
        }
        return days;
    }

    /// <remarks>shortcut: returns carry no lines in ReturnRecordedV1, so products are ranked by what was sold (voids excluded, returns not subtracted).</remarks>
    public async Task<IReadOnlyList<ProductSales>> TopProductsAsync(PanelFilter f, int limit, CancellationToken ct)
    {
        var lines = await (from l in Rows(db.SaleLines, f)
                           join s in CompletedSales(f) on l.SaleId equals s.Id
                           select new { l.ProductId, l.Description, l.Quantity, l.Total, l.Itbis, l.UnitCost, s.SoldUtc }).ToListAsync(ct);
        return [.. lines.GroupBy(l => l.ProductId)
            .Select(g => new ProductSales(g.Key, g.MaxBy(l => l.SoldUtc)!.Description, g.Sum(l => l.Quantity), g.Sum(l => l.Total),
                Money.Round(g.Sum(l => l.Total - l.Itbis - l.UnitCost * l.Quantity))))
            .OrderByDescending(p => p.Total).Take(Math.Clamp(limit, 1, 50))];
    }

    public async Task<IReadOnlyList<ShiftDifference>> ShiftsAsync(PanelFilter f, bool onlyDifferences, CancellationToken ct)
    {
        var (from, to) = BusinessDay.UtcRange(f.From, f.To);
        var stores = await StoreNamesAsync(f.TenantId, ct);
        var shifts = await Rows(db.ClosedShifts, f).Where(s => s.ClosedUtc >= from && s.ClosedUtc < to).ToListAsync(ct);
        return [.. shifts.Where(s => !onlyDifferences || s.Difference != 0).OrderByDescending(s => s.ClosedUtc)
            .Select(s => new ShiftDifference(s.ShiftId, s.StoreId, stores.GetValueOrDefault(s.StoreId, "?"), s.RegisterId, s.CashierId, s.ZNumber,
                s.ExpectedCash, s.CountedCash, s.Difference, s.ClosedUtc))];
    }

    /// <remarks>shortcut: events carry the cashier's id only; names need a user event from the store (or the panel shows the id).</remarks>
    public async Task<IReadOnlyList<CashierSales>> SalesByCashierAsync(PanelFilter f, CancellationToken ct)
    {
        var sales = await CompletedSales(f).Select(s => new { s.CashierId, s.Total }).ToListAsync(ct);
        var voided = await VoidedSales(f).Select(s => s.CashierId).ToListAsync(ct);
        return [.. sales.GroupBy(s => s.CashierId).Select(g => new CashierSales(g.Key, g.Count(), g.Sum(s => s.Total), voided.Count(v => v == g.Key)))
            .Concat(voided.Distinct().Where(v => sales.All(s => s.CashierId != v)).Select(v => new CashierSales(v, 0, 0, voided.Count(x => x == v))))
            .OrderByDescending(c => c.Total)];
    }

    public async Task<IReadOnlyList<PaymentMix>> PaymentMixAsync(PanelFilter f, CancellationToken ct)
    {
        var payments = await (from p in Rows(db.SalePayments, f)
                              join s in CompletedSales(f) on p.SaleId equals s.Id
                              select new { p.Method, p.Amount }).ToListAsync(ct);
        return [.. payments.GroupBy(p => p.Method).Select(g => new PaymentMix(g.Key, g.Count(), g.Sum(p => p.Amount))).OrderByDescending(p => p.Amount)];
    }

    public async Task<IReadOnlyList<StoreSyncStatus>> SyncStatusAsync(Guid tenantId, CancellationToken ct)
    {
        var stores = await db.Stores.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.Name).ToListAsync(ct);
        var nodes = await db.Nodes.AsNoTracking().Where(n => n.TenantId == tenantId).ToListAsync(ct);
        var types = Projectors.ByType.Keys.ToList();
        var waiting = await db.Inbox.AsNoTracking().Where(i => i.TenantId == tenantId && i.AppliedUtc == null && types.Contains(i.Type))
            .GroupBy(i => new { i.StoreId, Failed = i.Attempts >= InboxApplier.MaxAttempts }).Select(g => new { g.Key.StoreId, g.Key.Failed, Count = g.Count() })
            .ToListAsync(ct);
        return [.. stores.Select(s =>
        {
            var own = nodes.Where(n => n.StoreId == s.Id).OrderBy(n => n.CreatedUtc).ToList();
            return new StoreSyncStatus(s.Id, s.Name, own.Max(n => n.LastEventUtc), own.Max(n => n.LastSyncUtc),
                waiting.Where(w => w.StoreId == s.Id && !w.Failed).Sum(w => w.Count), waiting.Where(w => w.StoreId == s.Id && w.Failed).Sum(w => w.Count),
                [.. own.Select(n => new NodeStatus(n.Id, n.Name, n.LastEventUtc, n.LastSyncUtc))]);
        })];
    }

    public Task<Dictionary<Guid, string>> StoreNamesAsync(Guid tenantId, CancellationToken ct) =>
        db.Stores.AsNoTracking().Where(s => s.TenantId == tenantId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

    /// <summary>"RD$ 1,234.50", the way the owner reads amounts.</summary>
    public static string Rd(decimal amount) => "RD$ " + amount.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static IQueryable<T> Rows<T>(DbSet<T> set, PanelFilter f) where T : class, IReplicaRow =>
        set.AsNoTracking().Where(x => x.TenantId == f.TenantId && (f.StoreId == null || x.StoreId == f.StoreId));

    private IQueryable<ReplicaSale> SalesInRange(PanelFilter f)
    {
        var (from, to) = BusinessDay.UtcRange(f.From, f.To);
        return Rows(db.Sales, f).Where(s => s.SoldUtc >= from && s.SoldUtc < to);
    }

    private IQueryable<ReplicaSale> CompletedSales(PanelFilter f)
    {
        var voids = Rows(db.SaleVoids, f).Select(v => v.SaleId);
        return SalesInRange(f).Where(s => !voids.Contains(s.Id));
    }

    private IQueryable<ReplicaSale> VoidedSales(PanelFilter f)
    {
        var voids = Rows(db.SaleVoids, f).Select(v => v.SaleId);
        return SalesInRange(f).Where(s => voids.Contains(s.Id));
    }
}
