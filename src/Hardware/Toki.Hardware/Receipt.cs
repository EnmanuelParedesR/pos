namespace Toki.Hardware;

// The receipt as the store API returns it (Toki.Sales.Domain.Receipt) and as the printer prints it. Its texts are already Spanish.
public sealed record ReceiptLine(string Description, decimal Quantity, string Unit, decimal Price, decimal Discount, decimal Total, string? Note);
public sealed record ReceiptItbis(string Rate, decimal Base, decimal Itbis);
/// <param name="Method">Already in Spanish, as printed.</param>
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Received);
public sealed record Receipt(string BusinessName, string? Rnc, string? Address, string? Phone, string DocumentType, string ENcf, long Number,
    DateTime LocalDateTime, string Register, string Cashier, string? Customer, string? CustomerRnc, ReceiptLine[] Lines, decimal Subtotal, decimal Discount,
    ReceiptItbis[] Itbis, decimal Total, ReceiptPayment[] Payments, decimal Change, string SecurityCode, string QrUrl, string? Footer, bool IsVoided);
