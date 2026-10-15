using Toki.SharedKernel;

namespace Toki.Sales.Contracts;

public sealed record SaleLineV1(Guid ProductId, Guid? PresentationId, string Description, decimal Quantity, decimal Total, decimal Itbis, decimal Cost);

public sealed record SalePaymentV1(string Method, decimal Amount);

public sealed record SaleRecordedV1(
    Guid SaleId, long Number, Guid ShiftId, Guid CashierId, Guid? CustomerId, string ENcf, decimal Total, decimal Itbis, decimal Discount,
    IReadOnlyList<SaleLineV1> Lines, IReadOnlyList<SalePaymentV1> Payments) : IntegrationEvent(SaleId);

public sealed record SaleVoidedV1(Guid SaleId, string CreditNoteENcf, string Reason, Guid? SupervisorId) : IntegrationEvent(SaleId);

public sealed record ReturnRecordedV1(Guid ReturnId, Guid SaleId, string CreditNoteENcf, string Method, decimal Total, decimal Itbis)
    : IntegrationEvent(SaleId);
