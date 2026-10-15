namespace Toki.Client.UI.Api;

// Purchasing back office (TK-185): suppliers, purchases, expenses with NCF, supplier payments and payables.

public enum PaymentTerms { Cash, Credit }
public enum SupplierPaymentMethod { Cash, Transfer, Check, Card }

/// <summary>DGII 606 classification of an expense.</summary>
public enum ExpenseType
{
    PersonnelExpenses = 1, WorkSuppliesAndServices = 2, Leases = 3, FixedAssetExpenses = 4, RepresentationExpenses = 5,
    OtherAllowedDeductions = 6, FinancialExpenses = 7, ExtraordinaryExpenses = 8, CostOfSales = 9, AssetAcquisitions = 10, InsuranceExpenses = 11,
}

public sealed record Supplier(Guid Id, string Name, string? Rnc, string? Phone, string? Contact, int CreditDays, bool IsActive);
public sealed record SaveSupplier(string Name, string? Rnc, string? Phone, string? Contact, int CreditDays, bool IsActive);
/// <param name="Type">Already in Spanish: Compra, Gasto or Pago.</param>
public sealed record SupplierStatementLine(DateOnly Date, string Type, string Document, decimal Charge, decimal Payment, decimal Balance, DateOnly? DueDate,
    Guid? PaymentId);
public sealed record SupplierStatement(Guid SupplierId, string Supplier, decimal Balance, SupplierStatementLine[] Lines);

/// <param name="Status">Draft, Posted or Voided.</param>
public sealed record PurchaseSummary(Guid Id, long Number, string Supplier, string Ncf, DateOnly InvoiceDate, string Status, string Terms, decimal Total);
public sealed record PurchaseLineInput(Guid ProductId, Guid? PresentationId, decimal Quantity, decimal UnitCost, ItbisRate? ItbisRate);
/// <param name="Id">Generated here on the first save, so a retry returns the same draft.</param>
public sealed record SavePurchase(Guid SupplierId, string Ncf, DateOnly InvoiceDate, PaymentTerms Terms, IReadOnlyList<PurchaseLineInput> Lines,
    Guid? WarehouseId, int? CreditDays, string? Notes, decimal? InvoiceItbis, decimal? InvoiceTotal, Guid? Id, SupplierPaymentMethod? Method);
public sealed record PurchaseSaved(Guid Id, decimal Subtotal, decimal Itbis, decimal Total);
public sealed record PurchaseDetailLine(Guid ProductId, string Description, decimal Quantity, decimal Factor, decimal UnitCost, ItbisRate ItbisRate,
    decimal Subtotal, decimal Itbis);
public sealed record PurchaseDetail(Guid Id, long Number, Guid SupplierId, string SupplierName, string Ncf, DateOnly InvoiceDate, PaymentTerms Terms,
    SupplierPaymentMethod? Method, DateOnly? DueDate, Guid? WarehouseId, string Status, decimal Subtotal, decimal Itbis, decimal Total, string? Notes,
    string? VoidReason, PurchaseDetailLine[] Lines);

public sealed record Expense(Guid Id, Guid SupplierId, string SupplierName, string? SupplierRnc, string Ncf, DateOnly Date, string Description, ExpenseType Type,
    PaymentTerms Terms, SupplierPaymentMethod? Method, DateOnly? DueDate, decimal Subtotal, decimal Itbis, decimal Total, bool IsVoided, string? VoidReason);
public sealed record RecordExpense(Guid SupplierId, string Ncf, DateOnly Date, string Description, ExpenseType Type, decimal Subtotal, decimal Itbis,
    PaymentTerms Terms, int? CreditDays, Guid? Id, SupplierPaymentMethod? Method);
public sealed record ExpenseRecorded(Guid Id, decimal Total);

/// <param name="SupplierPaymentId">Generated here, so a retry returns the saved payment.</param>
public sealed record PaySupplier(Guid SupplierId, decimal Amount, SupplierPaymentMethod Method, DateOnly Date, string? Reference, Guid? SupplierPaymentId);
public sealed record PaymentAllocated(Guid PayableId, string Ncf, decimal Amount, decimal RemainingBalance);
public sealed record SupplierPaid(Guid SupplierPaymentId, PaymentAllocated[] Allocations);

public sealed record VoidReason(string Reason);

/// <summary>What is owed to a supplier by days overdue (from /api/reports/payables).</summary>
public sealed record SupplierPayable(Guid SupplierId, string Supplier, decimal NotYetDue, decimal Overdue1To30, decimal Overdue31To60, decimal Overdue61To90,
    decimal OverdueOver90, decimal Total);
