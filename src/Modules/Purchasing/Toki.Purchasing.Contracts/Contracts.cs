using Toki.SharedKernel;

namespace Toki.Purchasing.Contracts;

public sealed record PurchasePostedV1(Guid PurchaseId, Guid SupplierId, string Ncf, decimal Subtotal, decimal Itbis, decimal Total, bool OnCredit)
    : IntegrationEvent(PurchaseId);

public sealed record PurchaseVoidedV1(Guid PurchaseId, string Reason) : IntegrationEvent(PurchaseId);

public sealed record SupplierPaymentRecordedV1(Guid SupplierPaymentId, Guid SupplierId, decimal Amount) : IntegrationEvent(SupplierPaymentId);
