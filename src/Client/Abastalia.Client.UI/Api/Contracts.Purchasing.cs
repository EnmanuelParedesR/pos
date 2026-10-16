namespace Abastalia.Client.UI.Api;

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
    Guid? WarehouseId, int? CreditDays, string? Notes, decimal? InvoiceItbis, decimal? InvoiceTotal, Guid? Id, SupplierPaymentMethod? Method,
    string? NcfOverrideReason = null);
public sealed record PurchaseSaved(Guid Id, decimal Subtotal, decimal Itbis, decimal Total);
public sealed record PurchaseDetailLine(Guid ProductId, Guid? PresentationId, string Description, decimal Quantity, decimal Factor, decimal UnitCost, ItbisRate ItbisRate,
    decimal Subtotal, decimal Itbis);
public sealed record PurchaseDetail(Guid Id, long Number, Guid SupplierId, string SupplierName, string Ncf, DateOnly InvoiceDate, PaymentTerms Terms,
    SupplierPaymentMethod? Method, DateOnly? DueDate, Guid? WarehouseId, string Status, decimal Subtotal, decimal Itbis, decimal Total, string? Notes,
    string? VoidReason, PurchaseDetailLine[] Lines, string? AttachmentName = null);

public sealed record Expense(Guid Id, Guid SupplierId, string SupplierName, string? SupplierRnc, string Ncf, DateOnly Date, string Description, ExpenseType Type,
    PaymentTerms Terms, SupplierPaymentMethod? Method, DateOnly? DueDate, decimal Subtotal, decimal Itbis, decimal Total, bool IsVoided, string? VoidReason);
public sealed record RecordExpense(Guid SupplierId, string Ncf, DateOnly Date, string Description, ExpenseType Type, decimal Subtotal, decimal Itbis,
    PaymentTerms Terms, int? CreditDays, Guid? Id, SupplierPaymentMethod? Method, string? NcfOverrideReason = null);
public sealed record ExpenseRecorded(Guid Id, decimal Total);

/// <param name="SupplierPaymentId">Generated here, so a retry returns the saved payment.</param>
public sealed record PaySupplier(Guid SupplierId, decimal Amount, SupplierPaymentMethod Method, DateOnly Date, string? Reference, Guid? SupplierPaymentId);
public sealed record PaymentAllocated(Guid PayableId, string Ncf, decimal Amount, decimal RemainingBalance);
public sealed record SupplierPaid(Guid SupplierPaymentId, PaymentAllocated[] Allocations);

public sealed record VoidReason(string Reason);

/// <summary>TK-191: GET /api/purchasing/suggested-order. Quantity in the presentation last bought (base units without one), Suggested in base
/// units (lb or units), LastCost per ordered unit without ITBIS.</summary>
public sealed record SuggestedLine(Guid ProductId, string Product, UnitOfMeasure Unit, ItbisRate ItbisRate, decimal Stock, decimal MinimumStock, decimal MaximumStock,
    decimal Suggested, decimal? LastCost, Guid? PresentationId, string? Presentation, decimal Factor, decimal Quantity);
/// <param name="SupplierId">Null for products never bought from an active supplier.</param>
/// <param name="Subtotal">Null (and every LastCost) without costs.view.</param>
public sealed record SuggestedGroup(Guid? SupplierId, string? Supplier, SuggestedLine[] Lines, decimal? Subtotal);

/// <summary>TK-223: one row of the cross with the DGII Excel. Status: Matched, Missing, Difference, Duplicate or OnlyInAbastalia.</summary>
public sealed record DgiiMatch(string Status, int? Row, string Rnc, string? Name, string Ncf, DateOnly? Date, decimal? DgiiTotal, decimal? DgiiItbis,
    string? DocumentType, Guid? DocumentId, string? DocumentStatus, decimal? AbastaliaTotal, decimal? AbastaliaItbis, Guid? SupplierId);
public sealed record DgiiCross(int Rows, int Matched, int Missing, int Differences, int Duplicates, int OnlyInAbastalia, DgiiMatch[] Items);
public sealed record DraftFromDgii(string Rnc, string? Name, string Ncf, DateOnly Date, decimal Total, decimal Itbis);
public sealed record DraftFromDgiiCreated(Guid PurchaseId, Guid SupplierId, string Supplier);
public sealed record SupplierFound(Guid Id, string Name);

/// <summary>TK-224: the supplier invoice as the AI read it, checked by the store. Nothing is saved until the person saves the purchase.</summary>
public sealed record InvoiceLineRead(string Description, decimal? Quantity, decimal? UnitCost, ItbisRate? ItbisRate, Guid? ProductId, string? ProductName);
public sealed record InvoiceRead(Guid? SupplierId, string? SupplierRnc, string? SupplierName, string? Ncf, DateOnly? Date, InvoiceLineRead[] Lines,
    decimal? Subtotal, decimal? Itbis, decimal? Total, decimal Confidence, string[] Warnings, string? Source = null);
/// <summary>TK-224, batch: one invoice saved as a draft (PurchaseId null when no supplier RNC was read; its review fills the form instead).</summary>
public sealed record InvoiceDraft(Guid? PurchaseId, InvoiceRead Review);

/// <summary>TK-248: the business's invoice readings this month; AddOn = it has the paid add-on.</summary>
public sealed record InvoiceReadingQuota(int Used, int Limit, DateOnly ResetsOn, bool AddOn)
{
    public int Remaining => Math.Max(Limit - Used, 0);
}

/// <summary>What is owed to a supplier by days overdue (from /api/reports/payables).</summary>
public sealed record SupplierPayable(Guid SupplierId, string Supplier, decimal NotYetDue, decimal Overdue1To30, decimal Overdue31To60, decimal Overdue61To90,
    decimal OverdueOver90, decimal Total);
