namespace Toki.Hardware;

// The receipt as the store API returns it (Toki.Sales.Domain.Receipt) and as the printer prints it. Its texts are already Spanish.
public sealed record ReceiptLine(string Description, decimal Quantity, string Unit, decimal Price, decimal Discount, decimal Total, string? Note);
public sealed record ReceiptItbis(string Rate, decimal Base, decimal Itbis);
/// <param name="Method">Already in Spanish, as printed.</param>
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Received);
public sealed record Receipt(string BusinessName, string? Rnc, string? Address, string? Phone, string DocumentType, string ENcf, long Number,
    DateTime LocalDateTime, string Register, string Cashier, string? Customer, string? CustomerRnc, ReceiptLine[] Lines, decimal Subtotal, decimal Discount,
    ReceiptItbis[] Itbis, decimal Total, ReceiptPayment[] Payments, decimal Change, string SecurityCode, string QrUrl, string? Footer, bool IsVoided,
    DateTime? SignedAt = null, DateOnly? SequenceExpiresOn = null, string? ContingencyLegend = null, string? DgiiStatus = null);

/// <summary>TK-239: one line of a shift report: an amount, or a text such as "12 (1 anulada)".</summary>
public sealed record ShiftReportLine(string Label, decimal? Amount, string? Text = null, bool Bold = false);
public sealed record ShiftReportSection(string Title, ShiftReportLine[] Lines);
/// <summary>TK-239: the shift close (Z) or an open shift (X) on the thermal printer. Texts already Spanish.</summary>
/// <param name="Title">"Cierre Z #12" or "Reporte X (turno abierto)".</param>
public sealed record ShiftReport(string BusinessName, string? Rnc, string Title, string Register, string Cashier, DateTime OpenedLocal, DateTime? ClosedLocal,
    ShiftReportSection[] Sections, DateTime PrintedLocal, string? AuditChainHead = null);
