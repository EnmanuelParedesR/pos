namespace Abastalia.Hardware;

// The receipt as the store API returns it (Abastalia.Sales.Domain.Receipt) and as the printer prints it. Its texts are already Spanish.
public sealed record ReceiptLine(string Description, decimal Quantity, string Unit, decimal Price, decimal Discount, decimal Total, string? Note);
public sealed record ReceiptItbis(string Rate, decimal Base, decimal Itbis);
/// <summary>TK-280: the business logo as the store server dithers it for one paper width: one bit a dot (1 = ink), <c>Width / 8</c> bytes a row, top row first.</summary>
/// <param name="Width">Dots a row, a multiple of 8.</param>
public sealed record ReceiptLogo(int Width, int Height, byte[] Bits);
/// <param name="Method">Already in Spanish, as printed.</param>
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Received);
/// <param name="LogoVersion">TK-280: the business has a logo (this version of it); the register fetches its <see cref="Logo"/> before printing.</param>
/// <param name="Logo">TK-280: the logo for this printer's width, filled by the register (never by the API); null prints without it.</param>
public sealed record Receipt(string BusinessName, string? Rnc, string? Address, string? Phone, string DocumentType, string ENcf, long Number,
    DateTime LocalDateTime, string Register, string Cashier, string? Customer, string? CustomerRnc, ReceiptLine[] Lines, decimal Subtotal, decimal Discount,
    ReceiptItbis[] Itbis, decimal Total, ReceiptPayment[] Payments, decimal Change, string SecurityCode, string QrUrl, string? Footer, bool IsVoided,
    DateTime? SignedAt = null, DateOnly? SequenceExpiresOn = null, string? ContingencyLegend = null, string? DgiiStatus = null, string? LogoVersion = null,
    ReceiptLogo? Logo = null);

/// <summary>TK-239: one line of a shift report: an amount, or a text such as "12 (1 anulada)".</summary>
public sealed record ShiftReportLine(string Label, decimal? Amount, string? Text = null, bool Bold = false);
public sealed record ShiftReportSection(string Title, ShiftReportLine[] Lines);
/// <summary>TK-239: the shift close (Z) or an open shift (X) on the thermal printer. Texts already Spanish.</summary>
/// <param name="Title">"Cierre Z #12" or "Reporte X (turno abierto)".</param>
public sealed record ShiftReport(string BusinessName, string? Rnc, string Title, string Register, string Cashier, DateTime OpenedLocal, DateTime? ClosedLocal,
    ShiftReportSection[] Sections, DateTime PrintedLocal, string? AuditChainHead = null);
