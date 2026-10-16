using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Security;
using Toki.CashRegister.Contracts;
using Toki.Customers.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Purchasing.Contracts;
using Toki.Inventory.Contracts;
using Toki.SharedKernel;
using Toki.Sales.Contracts;

namespace Toki.Reports;

/// <summary>Reports module: no tables of its own (ADR-0004). It reads the other modules' report contracts and exports them.</summary>
public static class ReportsModule
{
}

/// <summary>Business days to report on (UTC−4, both included) and the output: json (default), csv or xlsx.</summary>
internal readonly record struct Period(DateOnly From, DateOnly To, string? Format)
{
    /// <summary>Reports aggregate in memory (see SalesReports); a year is the largest range.</summary>
    public const int MaxDays = 366;

    public IResult? Invalid() => To < From || To.DayNumber - From.DayNumber >= MaxDays
        ? EndpointExtensions.Problem(Error.Validation("Reports.InvalidPeriod", $"El período va de 'from' a 'to' y tiene como máximo {MaxDays} días."))
        : null;

    public string Name(string report) => $"{report}-{From:yyyyMMdd}-{To:yyyyMMdd}";
}

internal sealed record CategorySales(Guid CategoryId, string Category, decimal NetWithoutItbis, decimal? Cost, decimal? Profit);

internal sealed class ReportsEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/reports").RequirePermission(Permissions.ReportsView).WithTags("Reports");
        // TK-213: Turnos y cuadres and the DGII formats have their own permissions (the accountant sees the 606/607 without the cashiers' Z).
        var shifts = app.MapGroup("/api/reports").RequirePermission(Permissions.ShiftsView).WithTags("Reports");
        var dgii = app.MapGroup("/api/reports").RequirePermission(Permissions.FiscalReports).WithTags("Reports");

        // TK-201: cost, profit and stock value only with costs.view; without it those columns come empty (null in JSON).
        static bool Costs(IUserContext user) => user.Has(Permissions.CostsView);

        g.MapGet("/sales/by-day", async ([AsParameters] Period p, ISalesReports sales, IUserContext user, CancellationToken ct) =>
            p.Invalid() ?? Export.As(Hide(await sales.ByDayAsync(p.From, p.To, ct), Costs(user), d => d with { Cost = null, Profit = null }), p.Format, p.Name("ventas-por-dia")));

        g.MapGet("/sales/by-product", async ([AsParameters] Period p, ISalesReports sales, IUserContext user, CancellationToken ct) =>
            p.Invalid() ?? Export.As(Hide(await sales.ByProductAsync(p.From, p.To, ct), Costs(user), x => x with { Cost = null, Profit = null }), p.Format,
                p.Name("ventas-por-producto")));

        g.MapGet("/sales/by-category", async ([AsParameters] Period p, ISalesReports sales, IInventoryReports inventory, IUserContext user, CancellationToken ct) =>
        {
            if (p.Invalid() is { } invalid) return invalid;
            var names = await inventory.CategoriesAsync(ct);
            var rows = (await sales.ByProductAsync(p.From, p.To, ct)).GroupBy(x => x.CategoryId)
                .Select(c => new CategorySales(c.Key, names.GetValueOrDefault(c.Key, "?"), c.Sum(x => x.NetWithoutItbis), c.Sum(x => x.Cost), c.Sum(x => x.Profit)))
                .OrderByDescending(c => c.NetWithoutItbis).ToList();
            return Export.As(Hide(rows, Costs(user), c => c with { Cost = null, Profit = null }), p.Format, p.Name("ventas-por-categoria"));
        });

        // TK-179: the back office's payment mix and the same-hour comparison.
        g.MapGet("/sales/by-payment-method", async ([AsParameters] Period p, ISalesReports sales, CancellationToken ct) =>
            p.Invalid() ?? Export.As(await sales.ByPaymentMethodAsync(p.From, p.To, ct), p.Format, p.Name("ventas-por-medio-de-pago")));

        g.MapGet("/sales/by-hour", async ([AsParameters] Period p, ISalesReports sales, CancellationToken ct) =>
            p.Invalid() ?? Export.As(await sales.ByHourAsync(p.From, p.To, ct), p.Format, p.Name("ventas-por-hora")));

        g.MapGet("/sales/by-cashier", async ([AsParameters] Period p, ISalesReports sales, CancellationToken ct) =>
            p.Invalid() ?? Export.As(await sales.ByCashierAsync(p.From, p.To, ct), p.Format, p.Name("ventas-por-cajero")));

        shifts.MapGet("/shifts", async ([AsParameters] Period p, ICashRegisterReports cashRegister, CancellationToken ct) =>
            p.Invalid() ?? Export.As(await cashRegister.ShiftsAsync(p.From, p.To, ct), p.Format, p.Name("turnos")));

        g.MapGet("/inventory/value", async (string? format, IInventoryReports inventory, IUserContext user, TimeProvider clock, CancellationToken ct) =>
            Export.As(Hide(await inventory.ValueAsync(ct), Costs(user), v => v with { Cost = null, CostValue = null }), format,
                $"inventario-{BusinessDay.Of(clock.GetUtcNow().UtcDateTime):yyyyMMdd}"));

        // shortcut: rows of the 607, 606 and 608 as CSV/Excel to paste into the DGII tool, which writes the TXT; the TXT layout comes with TK-055.
        // R8 #3: blocked while an e-CF of the period is voided or rejected without its replacement (its sale would be missing from the 607).
        dgii.MapGet("/dgii/607", async ([AsParameters] Period p, ISalesReports sales, IFiscalDocuments fiscal, CancellationToken ct) =>
        {
            if (p.Invalid() is { } invalid) return invalid;
            var (f, t) = BusinessDay.UtcRange(p.From, p.To);
            var pending = (await fiscal.UnreplacedENcfsAsync(f, t, ct)).Count;
            return pending > 0
                ? EndpointExtensions.Problem(Error.Conflict("Reports.DocumentsToReplace",
                    $"Hay {pending} {(pending == 1 ? "comprobante anulado, rechazado o serie B" : "comprobantes anulados, rechazados o serie B")} por reemplazar o regularizar en el período. " +
                    "Resuélvalos en Comprobantes electrónicos antes de sacar el 607."))
                : Export.As(await sales.Report607Async(p.From, p.To, ct), p.Format, p.Name("607"));
        });
        dgii.MapGet("/dgii/606", async ([AsParameters] Period p, IPurchasingReports purchasing, CancellationToken ct) =>
            p.Invalid() ?? Export.As(await purchasing.Report606Async(p.From, p.To, ct), p.Format, p.Name("606")));
        dgii.MapGet("/dgii/608", async ([AsParameters] Period p, IFiscalReports fiscal, CancellationToken ct) =>
            p.Invalid() ?? Export.As(await fiscal.Report608Async(p.From, p.To, ct), p.Format, p.Name("608")));

        g.MapGet("/receivables", async (string? format, ICustomerReports customers, TimeProvider clock, CancellationToken ct) =>
            Export.As(await customers.ReceivablesAsync(ct), format, $"por-cobrar-{BusinessDay.Of(clock.GetUtcNow().UtcDateTime):yyyyMMdd}"));
        g.MapGet("/payables", async (string? format, IPurchasingReports purchasing, TimeProvider clock, CancellationToken ct) =>
            Export.As(await purchasing.PayablesAsync(ct), format, $"por-pagar-{BusinessDay.Of(clock.GetUtcNow().UtcDateTime):yyyyMMdd}"));
    }

    private static IReadOnlyList<T> Hide<T>(IReadOnlyList<T> rows, bool costs, Func<T, T> withoutCosts) => costs ? rows : [.. rows.Select(withoutCosts)];
}
