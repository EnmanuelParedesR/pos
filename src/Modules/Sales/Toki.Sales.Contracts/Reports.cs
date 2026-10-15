namespace Toki.Sales.Contracts;

/// <summary>Sales of a business day. Voided sales are left out; returns are subtracted on the day they happen (in every report).</summary>
/// <param name="SalesWithItbis">What the day's sales charged, ITBIS included (before returns).</param>
/// <param name="Returns">Returned the same day (ITBIS included), whatever the day of the sale.</param>
/// <param name="NetWithoutItbis">Sales minus returns, without ITBIS: the revenue profit is measured on.</param>
/// <param name="Cost">Historical cost of what was sold, minus the cost of returned goods put back on the shelf.</param>
public sealed record DailySales(DateOnly Day, int SaleCount, decimal SalesWithItbis, decimal Discount, decimal Returns, decimal Itbis, decimal NetWithoutItbis,
    decimal Cost, decimal Profit);

/// <param name="Quantity">Base units (pounds or units) sold minus returned in the period.</param>
public sealed record ProductSales(Guid ProductId, Guid CategoryId, string Product, decimal Quantity, decimal NetWithoutItbis, decimal Cost, decimal Profit);

/// <param name="SoldWithItbis">What the cashier's completed sales charged, ITBIS included (returns are not per cashier).</param>
public sealed record CashierSales(Guid CashierId, string Cashier, int SaleCount, int VoidedCount, decimal SoldWithItbis, decimal Discount);

/// <summary>One line of the DGII 607 (sales), columns in the order of DGII's format. Amounts without ITBIS in <see cref="InvoicedAmount"/>.</summary>
/// <param name="IdType">1 = RNC, 2 = cédula; empty for a consumer receipt without one.</param>
/// <param name="IncomeType">1 = operating income.</param>
public sealed record Report607Row(
    string? TaxId, int? IdType, string Ncf, string? ModifiedNcf, int IncomeType, DateOnly DocumentDate, DateOnly? WithholdingDate,
    decimal InvoicedAmount, decimal ItbisInvoiced, decimal ItbisWithheldByThirdParties, decimal ItbisCollected, decimal IncomeTaxWithheldByThirdParties, decimal IsrCollected,
    decimal SelectiveTax, decimal OtherTaxes, decimal LegalTip,
    decimal Cash, decimal CheckTransfer, decimal Card, decimal CreditSale, decimal GiftVouchers, decimal Barter, decimal OtherForms);

public interface ISalesReports
{
    Task<IReadOnlyList<DailySales>> ByDayAsync(DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<ProductSales>> ByProductAsync(DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<CashierSales>> ByCashierAsync(DateOnly from, DateOnly to, CancellationToken ct);
    Task<IReadOnlyList<Report607Row>> Report607Async(DateOnly from, DateOnly to, CancellationToken ct);
}
