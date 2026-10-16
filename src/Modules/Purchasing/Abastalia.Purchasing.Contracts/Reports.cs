namespace Abastalia.Purchasing.Contracts;

/// <summary>One line of the DGII 606 (purchases), columns in the order of DGII's format: a posted purchase or an expense with its NCF.
/// Voided ones are left out. Amounts without ITBIS, split into services and goods.</summary>
/// <param name="IdType">1 = RNC, 2 = cédula.</param>
/// <param name="GoodsServicesType">DGII code: 9 (cost of sales) for merchandise, the expense type otherwise.</param>
/// <param name="PaymentForm">DGII code: 1 cash, 2 check/transfer/deposit, 3 card, 4 on credit; empty when a cash invoice's method was not recorded.</param>
/// <param name="PaymentDate">The invoice date for cash invoices; empty for credit ones.</param>
public sealed record Report606Row(
    string? TaxId, int IdType, int GoodsServicesType, string Ncf, string? ModifiedNcf, DateOnly DocumentDate, DateOnly? PaymentDate,
    decimal ServicesAmount, decimal GoodsAmount, decimal TotalAmount, decimal ItbisInvoiced, decimal ItbisWithheld, decimal ItbisProportionality,
    decimal ItbisToCost, decimal ItbisToAdvance, decimal ItbisCollected, int? IsrWithholdingType, decimal IncomeTaxWithheld, decimal IsrCollected,
    decimal SelectiveTax, decimal OtherTaxes, decimal LegalTip, int? PaymentForm);

/// <summary>Accounts payable of a supplier by days overdue.</summary>
public sealed record SupplierPayable(Guid SupplierId, string Supplier, decimal NotYetDue, decimal Overdue1To30, decimal Overdue31To60, decimal Overdue61To90,
    decimal OverdueOver90, decimal Total);

public interface IPurchasingReports
{
    Task<IReadOnlyList<Report606Row>> Report606Async(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Open payables by supplier, largest first.</summary>
    Task<IReadOnlyList<SupplierPayable>> PayablesAsync(CancellationToken ct);
}
