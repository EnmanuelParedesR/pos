using Toki.SharedKernel;

namespace Toki.Sales.Contracts;

public sealed record SaleLineV1(Guid ProductId, Guid? PresentationId, string Description, decimal Quantity, decimal Total, decimal Itbis, decimal Cost);

public sealed record SalePaymentV1(string Method, decimal Amount);

public sealed record SaleRecordedV1(
    Guid SaleId, long Number, Guid ShiftId, Guid CashierId, Guid? CustomerId, string ENcf, decimal Total, decimal Itbis, decimal Discount,
    IReadOnlyList<SaleLineV1> Lines, IReadOnlyList<SalePaymentV1> Payments) : IntegrationEvent(SaleId);

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

/// <summary>A product shown on (or taken off) the store's public catalog (TK-195). The page is public: retail price with ITBIS only, never cost or stock.</summary>
/// <param name="Unit">"Unit" or "Pound".</param>
public sealed record CatalogItemChangedV1(Guid ProductId, string Name, string Category, string Unit, decimal Price, bool Visible) : IntegrationEvent(ProductId);

/// <summary>The public catalog's WhatsApp number (digits with the country code), or null when the store has none.</summary>
public sealed record CatalogSettingsChangedV1(string? WhatsApp) : IntegrationEvent(Guid.Empty);
