using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Ingest;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Panel;

/// <summary>What a panel or assistant query covers. The tenant comes from the caller's identity; days are store days (UTC−4).</summary>
public sealed record PanelFilter(Guid TenantId, Guid? StoreId, DateOnly From, DateOnly To);
/// <summary>TK-256: a store's DGII certification for the assistant. CurrentStep is 1-based; null once certified.</summary>
public sealed record StoreCertificationStatus(string StoreName, bool Certified, int StepsDone, int TotalSteps, int? CurrentStep, string? StepTitle,
    string? Who, string? NextAction, string? Help, int? DaysOnStep);

public sealed record HourSales(int Hour, int Count, decimal Total);
public sealed record PaymentMix(string Method, int Count, decimal Amount);
/// <param name="CashierName">Shift issues only, once the store sent the user's name (UserProfileChangedV1); otherwise null.</param>
public sealed record PanelIssue(string Kind, Guid StoreId, string StoreName, Guid? ReferenceId, decimal? Amount, DateTime? OccurredUtc, string Message,
    string? CashierName = null, string? RegisterName = null);
public sealed record Summary(
    DateOnly Date, decimal SalesTotal, int SalesCount, decimal AverageTicket, decimal DiscountTotal, decimal ItbisTotal, decimal ReturnsTotal, decimal NetTotal,
    int VoidedCount, IReadOnlyList<HourSales> ByHour, IReadOnlyList<PaymentMix> PaymentMix, IReadOnlyList<PanelIssue> Issues, DateTime? LastEventUtc);
public sealed record DaySales(DateOnly Date, int Count, decimal Total, decimal Discount, decimal Itbis, decimal Returns, decimal Net, int VoidedCount);
/// <param name="Unit">Unit of <paramref name="Quantity"/>, "Pound" or "Unit", from the newest sale with one (SaleRecordedV2, TK-180); null before that.</param>
/// <param name="Profit">Null for a member who may not see costs (TK-201).</param>
public sealed record ProductSales(Guid ProductId, string Description, decimal Quantity, decimal Total, decimal? Profit, string? Unit = null);
/// <param name="CashierName">Null until the store sent the name (names come from UserProfileChangedV1 and RegisterChangedV1).</param>
public sealed record ShiftDifference(
    Guid ShiftId, Guid StoreId, string StoreName, Guid RegisterId, Guid CashierId, int ZNumber, decimal ExpectedCash, decimal CountedCash, decimal Difference, DateTime ClosedUtc,
    string? RegisterName = null, string? CashierName = null);
/// <param name="OpeningFloat">Null when the store never sent the opening (ShiftOpenedV1).</param>
public sealed record ShiftReport(ShiftDifference Shift, decimal? OpeningFloat, DateTime? OpenedUtc, int SalesCount, decimal SalesTotal, int VoidedCount,
    IReadOnlyList<PaymentMix> Payments, IReadOnlyList<CashMovementTotal> CashMovements);
/// <param name="Type">As the store sends it (CashMovementRecordedV1): sale change, cash in or out, credit payment...</param>
public sealed record CashMovementTotal(string Type, string Method, int Count, decimal Amount);
public sealed record CashierSales(Guid CashierId, int Count, decimal Total, int VoidedCount, string? CashierName = null);
/// <param name="CustomerName">Null until the store sent the customer's account (CustomerBalanceChangedV1, TK-174).</param>
public sealed record CustomerDebt(Guid CustomerId, Guid StoreId, decimal Debt, string? CustomerName = null);
/// <param name="Total">What all customers owe on credit (fiado) right now.</param>
public sealed record Receivables(decimal Total, int CustomerCount, IReadOnlyList<CustomerDebt> TopDebtors);
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
            $"Z #{s.ZNumber}{(s.CashierName is null ? "" : $" ({s.CashierName})")}: {(s.Difference < 0 ? "faltante" : "sobrante")} de {Rd(Math.Abs(s.Difference))}",
            s.CashierName, s.RegisterName)).ToList();
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

    /// <summary>Sold minus returned in the range (returns on the day they happened, like the store's reports).</summary>
    public async Task<IReadOnlyList<ProductSales>> TopProductsAsync(PanelFilter f, int limit, bool showCosts, CancellationToken ct)
    {
        var (start, end) = BusinessDay.UtcRange(f.From, f.To);
        var sold = await (from l in Rows(db.SaleLines, f)
                          join s in CompletedSales(f) on l.SaleId equals s.Id
                          select new { l.ProductId, l.Description, l.Quantity, l.Unit, l.Total, l.Itbis, l.UnitCost, At = s.SoldUtc }).ToListAsync(ct);
        var returned = await (from l in Rows(db.ReturnLines, f)
                              join r in Rows(db.Returns, f) on l.ReturnId equals r.Id
                              where r.ReturnedUtc >= start && r.ReturnedUtc < end
                              select new { l.ProductId, l.Description, Quantity = -l.Quantity, Unit = (string?)null, Total = -l.Total, Itbis = -l.Itbis, l.UnitCost, At = r.ReturnedUtc })
            .ToListAsync(ct);
        return [.. sold.Concat(returned).GroupBy(l => l.ProductId)
            .Select(g => new ProductSales(g.Key, g.MaxBy(l => l.At)!.Description, g.Sum(l => l.Quantity), g.Sum(l => l.Total),
                showCosts ? Money.Round(g.Sum(l => l.Total - l.Itbis - l.UnitCost * l.Quantity)) : null, g.Where(l => l.Unit != null).MaxBy(l => l.At)?.Unit))
            .OrderByDescending(p => p.Total).Take(Math.Clamp(limit, 1, 50))];
    }

    public async Task<IReadOnlyList<ShiftDifference>> ShiftsAsync(PanelFilter f, bool onlyDifferences, CancellationToken ct)
    {
        var (from, to) = BusinessDay.UtcRange(f.From, f.To);
        var stores = await StoreNamesAsync(f.TenantId, ct);
        var users = await UserNamesAsync(f.TenantId, ct);
        var registers = await db.Registers.AsNoTracking().Where(r => r.TenantId == f.TenantId).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var shifts = await Rows(db.ClosedShifts, f).Where(s => s.ClosedUtc >= from && s.ClosedUtc < to).ToListAsync(ct);
        return [.. shifts.Where(s => !onlyDifferences || s.Difference != 0).OrderByDescending(s => s.ClosedUtc)
            .Select(s => new ShiftDifference(s.ShiftId, s.StoreId, stores.GetValueOrDefault(s.StoreId, "?"), s.RegisterId, s.CashierId, s.ZNumber,
                s.ExpectedCash, s.CountedCash, s.Difference, s.ClosedUtc, registers.GetValueOrDefault(s.RegisterId), users.GetValueOrDefault(s.CashierId)))];
    }

    /// <summary>TK-200 in the panel: one closed shift with what the replica has of its Z (opening float, sales by payment method, cash in and out).</summary>
    public async Task<ShiftReport?> ShiftReportAsync(Guid tenantId, Guid shiftId, CancellationToken ct)
    {
        var s = await db.ClosedShifts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ShiftId == shiftId, ct);
        if (s is null) return null;
        var f = new PanelFilter(tenantId, s.StoreId, default, default);
        var opened = await Rows(db.OpenedShifts, f).SingleOrDefaultAsync(x => x.ShiftId == shiftId, ct);
        var voids = Rows(db.SaleVoids, f).Select(v => v.SaleId);
        var sales = await Rows(db.Sales, f).Where(x => x.ShiftId == shiftId).Select(x => new { x.Total, Voided = voids.Contains(x.Id) }).ToListAsync(ct);
        var payments = await (from p in Rows(db.SalePayments, f)
                              join x in Rows(db.Sales, f) on p.SaleId equals x.Id
                              where x.ShiftId == shiftId && !voids.Contains(x.Id)
                              select new { p.Method, p.Amount }).ToListAsync(ct);
        var cash = await Rows(db.CashMovements, f).Where(m => m.ShiftId == shiftId).Select(m => new { m.Type, m.Method, m.Amount }).ToListAsync(ct);
        var store = await db.Stores.AsNoTracking().Where(x => x.Id == s.StoreId).Select(x => x.Name).SingleOrDefaultAsync(ct);
        var register = await Rows(db.Registers, f).Where(r => r.Id == s.RegisterId).Select(r => r.Name).SingleOrDefaultAsync(ct);
        var cashier = await db.StoreUsers.AsNoTracking().Where(u => u.TenantId == tenantId && u.Id == s.CashierId).Select(u => u.Name).SingleOrDefaultAsync(ct);
        var done = sales.Where(x => !x.Voided).ToList();
        return new ShiftReport(
            new ShiftDifference(s.ShiftId, s.StoreId, store ?? "?", s.RegisterId, s.CashierId, s.ZNumber, s.ExpectedCash, s.CountedCash, s.Difference, s.ClosedUtc,
                register, cashier),
            opened?.OpeningFloat, opened?.OpenedUtc, done.Count, done.Sum(x => x.Total), sales.Count - done.Count,
            [.. payments.GroupBy(p => p.Method).Select(g => new PaymentMix(g.Key, g.Count(), g.Sum(p => p.Amount))).OrderByDescending(p => p.Amount)],
            [.. cash.GroupBy(m => (m.Type, m.Method)).Select(g => new CashMovementTotal(g.Key.Type, g.Key.Method, g.Count(), g.Sum(m => m.Amount))).OrderBy(m => m.Type)]);
    }

    public async Task<IReadOnlyList<CashierSales>> SalesByCashierAsync(PanelFilter f, CancellationToken ct)
    {
        var sales = await CompletedSales(f).Select(s => new { s.CashierId, s.Total }).ToListAsync(ct);
        var voided = await VoidedSales(f).Select(s => s.CashierId).ToListAsync(ct);
        var users = await UserNamesAsync(f.TenantId, ct);
        return [.. sales.GroupBy(s => s.CashierId).Select(g => new CashierSales(g.Key, g.Count(), g.Sum(s => s.Total), voided.Count(v => v == g.Key)))
            .Concat(voided.Distinct().Where(v => sales.All(s => s.CashierId != v)).Select(v => new CashierSales(v, 0, 0, voided.Count(x => x == v))))
            .Select(c => c with { CashierName = users.GetValueOrDefault(c.CashierId) })
            .OrderByDescending(c => c.Total)];
    }

    /// <summary>Credit (fiado) owed right now: the store's own figure per customer (CustomerBalanceChangedV1, TK-174), with their name.</summary>
    public async Task<Receivables> ReceivablesAsync(Guid tenantId, Guid? storeId, int limit, CancellationToken ct)
    {
        var f = new PanelFilter(tenantId, storeId, default, default);
        // Sqlite cannot compare decimals: filtered and sorted in memory (see the class remark).
        var debts = (await Rows(db.Customers, f).Select(c => new CustomerDebt(c.Id, c.StoreId, c.Debt, c.Name)).ToListAsync(ct))
            .Where(d => d.Debt > 0).OrderByDescending(d => d.Debt).ToList();
        return new Receivables(debts.Sum(d => d.Debt), debts.Count, [.. debts.Take(Math.Clamp(limit, 1, 50))]);
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
        var nodes = await db.Nodes.AsNoTracking().Where(n => n.TenantId == tenantId && n.RevokedUtc == null).ToListAsync(ct); // a removed register leaves the list
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

    /// <summary>TK-256: where each of the tenant's stores is in the DGII certification (TK-225 progress) and what to do now (the app's guide).
    /// Only this tenant's rows; a store that never ticked a step is "not started".</summary>
    public async Task<List<StoreCertificationStatus>> CertificationAsync(Guid tenantId, CancellationToken ct)
    {
        var stores = await StoreNamesAsync(tenantId, ct);
        var rows = await db.Certifications.AsNoTracking().Where(c => c.TenantId == tenantId).ToDictionaryAsync(c => c.StoreId, ct);
        var guide = Abastalia.Fiscal.Contracts.CertificationGuide.Steps;
        return [.. stores.Select(s =>
        {
            var row = rows.GetValueOrDefault(s.Key);
            var current = row?.CurrentStep ?? 0;
            var step = row?.Certified == true || current >= guide.Count ? null : guide[current];
            return new StoreCertificationStatus(s.Value, row?.Certified == true, row?.StepsDone ?? 0, guide.Count, step is null ? null : current + 1, step?.Title,
                step?.Who, step?.Next, step?.Help, row is null ? null : (int)(clock.GetUtcNow().UtcDateTime - row.StepSinceUtc).TotalDays);
        })];
    }

    public Task<Dictionary<Guid, string>> StoreNamesAsync(Guid tenantId, CancellationToken ct) =>
        db.Stores.AsNoTracking().Where(s => s.TenantId == tenantId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

    private Task<Dictionary<Guid, string>> UserNamesAsync(Guid tenantId, CancellationToken ct) =>
        db.StoreUsers.AsNoTracking().Where(u => u.TenantId == tenantId).ToDictionaryAsync(u => u.Id, u => u.Name, ct);

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
