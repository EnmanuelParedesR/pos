using Toki.SharedKernel;

namespace Toki.Sales.Contracts;

public sealed record SaleLineV1(Guid ProductId, Guid? PresentationId, string Description, decimal Quantity, decimal Total, decimal Itbis, decimal Cost);

public sealed record SalePaymentV1(string Method, decimal Amount);

/// <summary>Superseded by <see cref="SaleRecordedV2"/>; kept so the cloud can still apply the ones already uploaded.</summary>
public sealed record SaleRecordedV1(
    Guid SaleId, long Number, Guid ShiftId, Guid CashierId, Guid? CustomerId, string ENcf, decimal Total, decimal Itbis, decimal Discount,
    IReadOnlyList<SaleLineV1> Lines, IReadOnlyList<SalePaymentV1> Payments) : IntegrationEvent(SaleId);

/// <summary>V1 plus each line's unit (TK-180), so the cloud can say "26.5 lb" or "12 unidades".</summary>
public sealed record SaleRecordedV2(
    Guid SaleId, long Number, Guid ShiftId, Guid CashierId, Guid? CustomerId, string ENcf, decimal Total, decimal Itbis, decimal Discount,
    IReadOnlyList<SaleLineV2> Lines, IReadOnlyList<SalePaymentV1> Payments) : IntegrationEvent(SaleId);

/// <param name="Unit">Unit of <paramref name="Quantity"/> as on the receipt: "Pound" or "Unit" (the product's unit of measure).</param>
public sealed record SaleLineV2(Guid ProductId, Guid? PresentationId, string Description, decimal Quantity, string Unit, decimal Total, decimal Itbis, decimal Cost);

public sealed record SaleVoidedV1(Guid SaleId, string CreditNoteENcf, string Reason, Guid? SupervisorId) : IntegrationEvent(SaleId);

/// <summary>Superseded by <see cref="ReturnRecordedV2"/>; kept so the cloud can still apply the ones already uploaded.</summary>
public sealed record ReturnRecordedV1(Guid ReturnId, Guid SaleId, string CreditNoteENcf, string Method, decimal Total, decimal Itbis)
    : IntegrationEvent(SaleId);

/// <param name="Quantity">In the sale line's unit (as in <see cref="SaleLineV1"/>).</param>
/// <param name="Total">Money given back for the line, ITBIS included.</param>
/// <param name="Cost">The sale line's unit cost (as in <see cref="SaleLineV1"/>).</param>
public sealed record ReturnLineV2(Guid ProductId, Guid? PresentationId, string Description, decimal Quantity, decimal Total, decimal Itbis, decimal Cost);

/// <summary>V1 plus the returned lines, so the cloud can subtract returns per product.</summary>
public sealed record ReturnRecordedV2(Guid ReturnId, Guid SaleId, string CreditNoteENcf, string Method, decimal Total, decimal Itbis,
    IReadOnlyList<ReturnLineV2> Lines) : IntegrationEvent(SaleId);
