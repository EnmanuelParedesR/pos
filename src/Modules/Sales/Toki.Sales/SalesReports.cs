using Microsoft.EntityFrameworkCore;
using Toki.CashRegister.Contracts;
using Toki.SharedKernel;
using Toki.Sales.Contracts;
using Toki.Sales.Domain;

namespace Toki.Sales;

/// <summary>Sales figures for reports. Voided sales are left out; returns are subtracted on the day they happened, in every report,
/// so a closed period never changes and the reports agree with each other.
/// Profit = revenue without ITBIS − historical cost (returned goods put back on the shelf give their cost back).</summary>
/// <remarks>shortcut: aggregates in memory over at most a year of one-row-per-sale projections; move to SQL GROUP BY if stores keep years online.</remarks>
internal sealed class SalesReports(SalesDbContext db, Toki.Fiscal.Contracts.IFiscalDocuments fiscal) : ISalesReports
{
    public async Task<IReadOnlyList<DailySales>> ByDayAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (f, t) = BusinessDay.UtcRange(from, to);
        var sales = await db.Sales.Where(s => s.DateUtc >= f && s.DateUtc < t && s.Status == SaleStatus.Completed)
            .Select(s => new { s.DateUtc, s.Total, s.Itbis, s.Discount, Cost = s.Lines.Sum(l => l.UnitCost * l.Quantity) })
            .AsNoTracking().ToListAsync(ct);
        var returns = await ReturnedAsync(f, t, ct);

        var days = sales.Select(s => BusinessDay.Of(s.DateUtc)).Concat(returns.Select(x => BusinessDay.Of(x.DateUtc))).Distinct().Order();
        return days.Select(day =>
        {
            var ofDay = sales.Where(s => BusinessDay.Of(s.DateUtc) == day).ToList();
            var rets = returns.Where(x => BusinessDay.Of(x.DateUtc) == day).ToList();
            var itbis = ofDay.Sum(s => s.Itbis) - rets.Sum(x => x.Itbis);
            var net = ofDay.Sum(s => s.Total - s.Itbis) - rets.Sum(x => x.Total - x.Itbis);
            var cost = Money.Round(ofDay.Sum(s => s.Cost) - rets.Sum(x => x.RestockedCost));
            return new DailySales(day, ofDay.Count, ofDay.Sum(s => s.Total), ofDay.Sum(s => s.Discount), rets.Sum(x => x.Total), itbis, net, cost, net - cost);
        }).ToList();
    }

    public async Task<IReadOnlyList<ProductSales>> ByProductAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (f, t) = BusinessDay.UtcRange(from, to);
        var sold = await (from l in db.Lines
                          join s in db.Sales on l.SaleId equals s.Id
                          where s.DateUtc >= f && s.DateUtc < t && s.Status == SaleStatus.Completed
                          select new MovedLine(l.ProductId, l.CategoryId, l.Description, l.PresentationId == null, l.Quantity * l.Factor, l.Total - l.Itbis, l.UnitCost * l.Quantity))
            .AsNoTracking().ToListAsync(ct);
        var returned = (await ReturnedAsync(f, t, ct)).SelectMany(x => x.Lines);

        return sold.Concat(returned).GroupBy(m => m.ProductId).Select(g =>
        {
            var net = g.Sum(m => m.Net);
            var cost = Money.Round(g.Sum(m => m.Cost));
            var name = g.OrderByDescending(m => m.WithoutPresentation).First().Description;
            return new ProductSales(g.Key, g.First().CategoryId, name, g.Sum(m => m.Quantity), net, cost, net - cost);
        }).OrderByDescending(p => p.NetWithoutItbis).ToList();
    }

    public async Task<IReadOnlyList<PaymentMethodSales>> ByPaymentMethodAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (f, t) = BusinessDay.UtcRange(from, to);
        var paid = await (from p in db.Payments
                          join s in db.Sales on p.SaleId equals s.Id
                          where s.DateUtc >= f && s.DateUtc < t && s.Status == SaleStatus.Completed
                          select new { p.Method, p.SaleId, p.Amount }).AsNoTracking().ToListAsync(ct);
        var refunded = await db.Returns.Where(x => x.DateUtc >= f && x.DateUtc < t).Select(x => new { x.Method, x.Total }).AsNoTracking().ToListAsync(ct);
        return paid.Select(p => p.Method).Concat(refunded.Select(r => r.Method)).Distinct().Select(m =>
        {
            var mine = paid.Where(p => p.Method == m).ToList();
            var back = refunded.Where(r => r.Method == m).Sum(r => r.Total);
            return new PaymentMethodSales(m.ToString(), mine.Select(p => p.SaleId).Distinct().Count(), mine.Sum(p => p.Amount), back, mine.Sum(p => p.Amount) - back);
        }).OrderByDescending(x => x.Net).ToList();
    }

    public async Task<IReadOnlyList<HourlySales>> ByHourAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (f, t) = BusinessDay.UtcRange(from, to);
        var sales = await db.Sales.Where(s => s.DateUtc >= f && s.DateUtc < t && s.Status == SaleStatus.Completed).Select(s => new { s.DateUtc, s.Total })
            .AsNoTracking().ToListAsync(ct);
        return sales.GroupBy(s => (Day: BusinessDay.Of(s.DateUtc), (s.DateUtc + BusinessDay.Offset).Hour)).OrderBy(g => g.Key.Day).ThenBy(g => g.Key.Hour)
            .Select(g => new HourlySales(g.Key.Day, g.Key.Hour, g.Count(), g.Sum(s => s.Total))).ToList();
    }

    public async Task<IReadOnlyList<CashierSales>> ByCashierAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (f, t) = BusinessDay.UtcRange(from, to);
        var sales = await db.Sales.Where(s => s.DateUtc >= f && s.DateUtc < t).Select(s => new { s.CashierId, s.Cashier, s.Status, s.Total, s.Discount })
            .AsNoTracking().ToListAsync(ct);
        return sales.GroupBy(s => s.CashierId).Select(g => new CashierSales(g.Key, g.First().Cashier,
            g.Count(s => s.Status == SaleStatus.Completed), g.Count(s => s.Status == SaleStatus.Voided),
            g.Where(s => s.Status == SaleStatus.Completed).Sum(s => s.Total), g.Where(s => s.Status == SaleStatus.Completed).Sum(s => s.Discount)))
            .OrderByDescending(c => c.SoldWithItbis).ToList();
    }

    /// <remarks>shortcut: payment columns of credit notes are left at zero and e-CF already received by DGII are listed too; the accountant confirms both (R5 #1).</remarks>
    public async Task<IReadOnlyList<Report607Row>> Report607Async(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (f, t) = BusinessDay.UtcRange(from, to);
        // Every issued e-CF counts, even of a voided sale: the void issued its own credit note.
        var sales = await db.Sales.Where(s => s.DateUtc >= f && s.DateUtc < t).Include(s => s.Payments).AsNoTracking().ToListAsync(ct);
        var voided = await db.Sales.Where(s => s.VoidedUtc >= f && s.VoidedUtc < t)
            .Select(s => new { s.CustomerRnc, s.ENcf, s.VoidENcf, s.VoidedUtc, s.Total, s.Itbis }).AsNoTracking().ToListAsync(ct);
        var returns = await (from x in db.Returns
                             join s in db.Sales on x.SaleId equals s.Id
                             where x.DateUtc >= f && x.DateUtc < t
                             select new { x.ENcf, x.DateUtc, x.Total, x.Itbis, Modified = s.ENcf, s.CustomerRnc }).AsNoTracking().ToListAsync(ct);

        static int? IdType(string? rnc) => rnc switch { null => null, { Length: 11 } => 2, _ => 1 };
        static Report607Row Row(string? rnc, string ncf, string? modified, DateTime dateUtc, decimal total, decimal itbis,
            decimal cash = 0, decimal check = 0, decimal card = 0, decimal credit = 0, decimal other = 0) =>
            new(rnc, IdType(rnc), ncf, modified, 1, BusinessDay.Of(dateUtc), null, total - itbis, itbis, 0, 0, 0, 0, 0, 0, 0, cash, check, card, credit, 0, 0, other);
        decimal ByMethod(Sale s, PaymentMethod method) => s.Payments.Where(p => p.Method == method).Sum(p => p.Amount);

        // TK-149: e-CF of the DGII test environments are not fiscal (a day of margin: a void or return can be signed after the sale).
        var test = await fiscal.TestENcfsAsync(f.AddDays(-1), t.AddDays(1), ct);
        return sales.Select(s => Row(s.CustomerRnc, s.ENcf, null, s.DateUtc, s.Total, s.Itbis, ByMethod(s, PaymentMethod.Cash), ByMethod(s, PaymentMethod.Transfer),
                ByMethod(s, PaymentMethod.Card), ByMethod(s, PaymentMethod.OnAccount), ByMethod(s, PaymentMethod.CreditBalance)))
            .Concat(voided.Select(s => Row(s.CustomerRnc, s.VoidENcf!, s.ENcf, s.VoidedUtc!.Value, s.Total, s.Itbis)))
            .Concat(returns.Select(x => Row(x.CustomerRnc, x.ENcf, x.Modified, x.DateUtc, x.Total, x.Itbis)))
            .Where(x => !test.Contains(x.Ncf)).OrderBy(x => x.DocumentDate).ThenBy(x => x.Ncf, StringComparer.Ordinal).ToList();
    }

    /// <summary>A line sold (positive) or returned (negative) in the period.</summary>
    private sealed record MovedLine(Guid ProductId, Guid CategoryId, string Description, bool WithoutPresentation, decimal Quantity, decimal Net, decimal Cost);

    private sealed record Returned(DateTime DateUtc, decimal Total, decimal Itbis, decimal RestockedCost, List<MovedLine> Lines);

    /// <summary>Returns made in the range (by their own date), with their product, quantity and the cost of what went back on the shelf.</summary>
    private async Task<List<Returned>> ReturnedAsync(DateTime f, DateTime t, CancellationToken ct)
    {
        var lines = await (from rl in db.Set<ReturnLine>()
                           join x in db.Returns on rl.ReturnId equals x.Id
                           join sl in db.Lines on rl.SaleLineId equals sl.Id
                           where x.DateUtc >= f && x.DateUtc < t
                           select new { x.Id, x.DateUtc, ReturnTotal = x.Total, ReturnItbis = x.Itbis, sl.ProductId, sl.CategoryId, sl.Description, sl.PresentationId,
                               sl.Factor, sl.UnitCost, rl.Quantity, rl.Total, rl.Itbis, rl.Restocked })
            .AsNoTracking().ToListAsync(ct);
        return lines.GroupBy(l => l.Id).Select(g =>
        {
            var moved = g.Select(l => new MovedLine(l.ProductId, l.CategoryId, l.Description, l.PresentationId == null, -l.Quantity * l.Factor, -(l.Total - l.Itbis),
                l.Restocked ? -l.UnitCost * l.Quantity : 0)).ToList();
            return new Returned(g.First().DateUtc, g.First().ReturnTotal, g.First().ReturnItbis, -moved.Sum(m => m.Cost), moved);
        }).ToList();
    }
}
