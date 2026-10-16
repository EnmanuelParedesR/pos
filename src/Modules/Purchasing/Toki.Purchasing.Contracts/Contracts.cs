using Toki.SharedKernel;

namespace Toki.Purchasing.Contracts;

public sealed record PurchasePostedV1(Guid PurchaseId, Guid SupplierId, string Ncf, decimal Subtotal, decimal Itbis, decimal Total, bool OnCredit)
    : IntegrationEvent(PurchaseId);

public sealed record PurchaseVoidedV1(Guid PurchaseId, string Reason) : IntegrationEvent(PurchaseId);

public sealed record SupplierPaymentRecordedV1(Guid SupplierPaymentId, Guid SupplierId, decimal Amount) : IntegrationEvent(SupplierPaymentId);

/// <summary>TK-224: the store sends the supplier's invoice to the cloud (<c>POST /ai/v1/supplier-invoice</c>, as the node) to be read by the AI.</summary>
/// <param name="File">The PDF or the photo, base64.</param>
/// <param name="ContentType">application/pdf, image/jpeg, image/png or image/webp.</param>
public sealed record SupplierInvoiceReadRequest(string? File, string? ContentType, string? FileName);

/// <param name="ItbisPercent">18, 16 or 0 when the invoice shows it per line.</param>
public sealed record SupplierInvoiceLineReading(string Description, decimal? Quantity, decimal? UnitCost, decimal? ItbisPercent);

/// <summary>What the AI read on the invoice: a proposal for a person to review, never saved as is. Any field may be missing.</summary>
/// <param name="Confidence">0 to 1: how sure the model says it is of the whole reading.</param>
/// <param name="Doubtful">Fields the model is not sure of: supplierRnc, ncf, date, lines, total.</param>
public sealed record SupplierInvoiceReading(string? SupplierRnc, string? SupplierName, string? Ncf, DateOnly? Date, IReadOnlyList<SupplierInvoiceLineReading> Lines,
    decimal? Subtotal, decimal? Itbis, decimal? Total, decimal Confidence, IReadOnlyList<string> Doubtful)
{
    public static readonly IReadOnlyList<string> ContentTypes = ["application/pdf", "image/jpeg", "image/png", "image/webp"];
    public const int MaxFileBytes = 4_000_000;
}
