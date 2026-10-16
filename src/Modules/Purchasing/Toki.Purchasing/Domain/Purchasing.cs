using Toki.SharedKernel;

namespace Toki.Purchasing.Domain;

internal sealed class Supplier
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Rnc { get; set; }
    public string? Phone { get; set; }
    public string? Contact { get; set; }
    /// <summary>Default credit days for its invoices.</summary>
    public int CreditDays { get; set; }
    public bool IsActive { get; set; } = true;
}

internal enum PurchaseStatus { Draft, Posted, Voided }

public enum PaymentTerms { Cash, Credit }

/// <summary>A supplier invoice for merchandise. Draft → posted (stock in, average cost, payable if on credit) → voided.</summary>
internal sealed class Purchase
{
    public long Number { get; set; }
    public Guid Id { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public string? SupplierRnc { get; set; }
    /// <summary>Empty only in a draft still waiting for the supplier's invoice (TK-191); posting needs it.</summary>
    public string Ncf { get; set; } = "";
    public DateOnly InvoiceDate { get; set; }
    public PaymentTerms Terms { get; set; }
    /// <summary>How a cash invoice was paid, for the 606 payment form. Unknown stays empty there.</summary>
    public SupplierPaymentMethod? Method { get; set; }
    public DateOnly? DueDate { get; set; }
    public Guid? WarehouseId { get; set; }
    public PurchaseStatus Status { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Itbis { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PostedUtc { get; set; }
    public DateTime? VoidedUtc { get; set; }
    public string? VoidReason { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Bumped by an atomic update before an edit or a void reads the purchase: it locks the row, so they run one at a time.</summary>
    public int Version { get; set; }
    public List<PurchaseLine> Lines { get; set; } = [];

    /// <summary>The invoice's own totals may differ from Toki's line math by centavos (suppliers round ITBIS on the subtotal).</summary>
    public const decimal InvoiceTolerance = 1m;

    /// <param name="invoiceItbis">ITBIS printed on the invoice; when given (within RD$ 1 of the computed one) it is the one paid and reported in the 606.</param>
    /// <param name="invoiceTotal">Total printed on the invoice, same rule.</param>
    public bool Recalculate(decimal? invoiceItbis = null, decimal? invoiceTotal = null)
    {
        foreach (var l in Lines)
        {
            l.Subtotal = Money.Round(l.Quantity * l.UnitCost);
            // Supplier costs come before ITBIS, so it is added on top (unlike shelf prices, which include it).
            l.Itbis = Money.Round(l.Subtotal * SharedKernel.Itbis.Percentage(l.ItbisRate));
        }
        Subtotal = Lines.Sum(l => l.Subtotal);
        Itbis = Lines.Sum(l => l.Itbis);
        Total = Subtotal + Itbis;
        if (Math.Abs((invoiceItbis ?? Itbis) - Itbis) > InvoiceTolerance || Math.Abs((invoiceTotal ?? Total) - Total) > InvoiceTolerance) return false;
        Itbis = invoiceItbis ?? Itbis;
        Total = invoiceTotal ?? Total;
        Subtotal = Total - Itbis;
        return true;
    }
}

/// <param name="UnitCost">Cost of one purchased unit (a box when <see cref="Factor"/> is 24), before ITBIS.</param>
internal sealed class PurchaseLine
{
    public Guid Id { get; set; }
    public Guid PurchaseId { get; set; }
    public int LineNumber { get; set; }
    public Guid ProductId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    /// <summary>Base units per purchased unit.</summary>
    public decimal Factor { get; set; } = 1;
    public decimal UnitCost { get; set; }
    public ItbisRate ItbisRate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Itbis { get; set; }
}

/// <summary>DGII 606 classification of an expense.</summary>
public enum ExpenseType
{
    PersonnelExpenses = 1, WorkSuppliesAndServices = 2, Leases = 3, FixedAssetExpenses = 4, RepresentationExpenses = 5,
    OtherAllowedDeductions = 6, FinancialExpenses = 7, ExtraordinaryExpenses = 8, CostOfSales = 9, AssetAcquisitions = 10, InsuranceExpenses = 11,
}

/// <summary>A supplier invoice without merchandise (electricity, rent, transport). Feeds the 606.</summary>
internal sealed class Expense
{
    public Guid Id { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public string? SupplierRnc { get; set; }
    public string Ncf { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Description { get; set; } = "";
    public ExpenseType Type { get; set; }
    public PaymentTerms Terms { get; set; }
    /// <summary>How a cash expense was paid, for the 606 payment form.</summary>
    public SupplierPaymentMethod? Method { get; set; }
    public DateOnly? DueDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Itbis { get; set; }
    public decimal Total { get; set; }
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid UserId { get; set; }
}

internal enum PayableDocumentType { Purchase, Expense }

/// <summary>An open payable: created when a credit purchase is posted or a credit expense is recorded. Its balance is
/// always Total minus the sum of allocated payments (never a stored, hand-edited number).</summary>
internal sealed class Payable
{
    public Guid Id { get; set; }
    public Guid SupplierId { get; set; }
    public PayableDocumentType Type { get; set; }
    public Guid DocumentId { get; set; }
    public string Ncf { get; set; } = "";
    public DateOnly Date { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal Total { get; set; }
    public bool IsVoided { get; set; }
}

public enum SupplierPaymentMethod { Cash, Transfer, Check, Card }

internal sealed class SupplierPayment
{
    public Guid Id { get; set; }
    public Guid SupplierId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Amount { get; set; }
    public SupplierPaymentMethod Method { get; set; }
    public string? Reference { get; set; }
    /// <summary>Set when the cash came out of a register's drawer during this shift.</summary>
    public Guid? ShiftId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid UserId { get; set; }
    /// <summary>A voided payment stays for history; its allocations no longer count (the invoices are open again).</summary>
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public List<PaymentAllocation> Allocations { get; set; } = [];
}

internal sealed class PaymentAllocation
{
    public Guid SupplierPaymentId { get; set; }
    public Guid PayableId { get; set; }
    public decimal Amount { get; set; }
}

internal static class Ncf
{
    /// <summary>Old NCF (B01 + 8 digits, 11 chars) or e-NCF (E31 + 10 digits, 13 chars).</summary>
    public static bool IsValid(string? ncf) =>
        ncf is { Length: 11 or 13 } && (ncf[0] is 'B' or 'E') && ncf.Skip(1).All(char.IsAsciiDigit);
}

internal static class PurchasingErrors
{
    public static readonly Error SupplierNotFound = Error.NotFound("Purchasing.SupplierNotFound", "El suplidor no existe o está desactivado.");
    public static readonly Error PurchaseNotFound = Error.NotFound("Purchasing.PurchaseNotFound", "La compra no existe.");
    public static readonly Error NcfRequired = Error.Validation("Purchasing.NcfRequired", "Escriba el NCF de la factura del suplidor antes de recibir la mercancía.");
    public static readonly Error LinesRequired = Error.Validation("Purchasing.LinesRequired", "Agregue los productos de la factura antes de recibir la mercancía.");
    public static readonly Error NotDraft = Error.Conflict("Purchasing.NotDraft", "Solo se puede cambiar o aplicar una compra en borrador.");
    public static readonly Error DuplicateNcf = Error.Conflict("Purchasing.DuplicateNcf", "Ya se registró ese NCF de ese suplidor.");
    public static readonly Error HasPayments = Error.Conflict("Purchasing.HasPayments", "La compra tiene pagos aplicados; no se puede anular.");
    public static readonly Error PaymentExceedsDebt = Error.Validation("Purchasing.PaymentExceedsDebt", "El pago es mayor que lo que se le debe al suplidor.");
    public static readonly Error ProductNotFound = Error.NotFound("Purchasing.ProductNotFound", "Un producto de la compra no existe.");
    public static readonly Error AlreadyVoided = Error.Conflict("Purchasing.AlreadyVoided", "Ya está anulado.");
    public static readonly Error SupplierWithoutRnc = Error.Validation("Purchasing.SupplierWithoutRnc", "El suplidor necesita RNC o cédula para registrar facturas con NCF (606).");
    public static readonly Error InvoiceTotalMismatch = Error.Validation("Purchasing.InvoiceTotalMismatch",
        $"El ITBIS o el total de la factura difiere en más de RD$ {Purchase.InvoiceTolerance:N0} de lo calculado. Revise cantidades y costos.");
    public static readonly Error PaymentNotFound = Error.NotFound("Purchasing.PaymentNotFound", "El pago no existe.");
    public static readonly Error ExpenseNotFound = Error.NotFound("Purchasing.ExpenseNotFound", "El gasto no existe.");
}

/// <summary>Invoice and payment dates: required, and never later than tomorrow (a typo in the year would break statements and aging).</summary>
internal static class DocumentDate
{
    public static bool IsValid(DateOnly date, TimeProvider clock) => date != default && date <= BusinessDay.Of(clock.GetUtcNow().UtcDateTime).AddDays(1);

    public const string Message = "La fecha es obligatoria y no puede ser futura.";
}
