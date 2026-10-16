namespace Toki.Client.UI.Api;

// Orders, quotes and the public catalog (TK-193, TK-194, TK-195). Same rules as Contracts.cs: camelCase JSON, enums by name.

public enum OrderKind { Order, Quote }
public enum OrderStatus { Pending, Ready, Delivered, Cancelled }

public sealed record OrderLineInput(Guid ProductId, Guid? PresentationId, decimal Quantity, string? Note);
public sealed record DepositInput(Guid ShiftId, PaymentMethod Method, decimal Amount);
public sealed record CreateOrder(Guid OrderId, OrderKind Kind, Guid? CustomerId, List<OrderLineInput> Lines, DateOnly? PickupDate, TimeOnly? PickupTime,
    string? Notes, int? ValidDays, DepositInput? Deposit);
public sealed record OrderCreated(Guid OrderId, decimal Total);
/// <param name="Refunded">What goes back to the customer: less than the deposit when part of it was already spent.</param>
public sealed record OrderCancelled(decimal Refunded);

/// <param name="Price">The unit price when it was made; the register charges today's.</param>
public sealed record OrderLine(Guid ProductId, Guid? PresentationId, string Name, string Unit, decimal Quantity, decimal Price, decimal Total, string? Note);
public sealed record OrderItem(Guid Id, OrderKind Kind, OrderStatus Status, Guid? CustomerId, string? CustomerName, DateOnly? PickupDate, TimeOnly? PickupTime,
    DateOnly? ValidUntil, string? Notes, decimal Total, decimal Deposit, PaymentMethod? DepositMethod, Guid? SaleId, DateTime CreatedUtc, OrderLine[] Lines);

/// <param name="PanelUrl">The owner's panel; the public page is {PanelUrl}/c/{storeId}. Null when this store has not set it.</param>
public sealed record CatalogSettings(string? WhatsApp, Guid[] ProductIds, string? PanelUrl);
public sealed record SaveCatalog(string? WhatsApp, List<Guid> ProductIds);
public sealed record CatalogQr(string Svg);
public sealed record CloudLink(bool Connected, Guid? StoreId);
