using Toki.CashRegister.Contracts;
using Toki.SharedKernel;

namespace Toki.Sales.Domain;

/// <summary>Order (encargo, TK-193): a customer picks it up on a date, maybe with a deposit. Quote (cotización, TK-194): prices for a customer,
/// valid for some days. Neither moves stock or issues an e-CF: that happens when the register charges it as a normal sale.</summary>
public enum OrderKind { Order, Quote }

/// <summary>Ready only applies to orders. Delivered = charged as a sale (a quote "converted"). Cancelled = an order called off.</summary>
public enum OrderStatus { Pending, Ready, Delivered, Cancelled }

/// <param name="Price">Unit price when the order or quote was made (retail or wholesale per the customer). The sale re-prices at today's price.</param>
public sealed record OrderLine(Guid ProductId, Guid? PresentationId, string Name, string Unit, decimal Quantity, decimal Price, decimal Total, string? Note);

internal sealed class Order
{
    public Guid Id { get; set; }
    public OrderKind Kind { get; set; }
    public OrderStatus Status { get; set; }
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public DateOnly? PickupDate { get; set; }
    public TimeOnly? PickupTime { get; set; }
    public DateOnly? ValidUntil { get; set; }
    /// <summary>"Picado fino", "sin grasa"…</summary>
    public string? Notes { get; set; }
    /// <summary>JSON of <see cref="OrderLine"/>: always read and written whole.</summary>
    public string Lines { get; set; } = "[]";
    public decimal Total { get; set; }
    /// <summary>Taken at the register when the order was made; it sits in the customer's credit balance until the order is charged.</summary>
    public decimal Deposit { get; set; }
    public PaymentMethod? DepositMethod { get; set; }
    public Guid? SaleId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid CreatedById { get; set; }
    public DateTime? ClosedUtc { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>Bumped by an atomic update before charging or cancelling: those run one at a time.</summary>
    public int Version { get; set; }

    public bool IsOpen => Status is OrderStatus.Pending or OrderStatus.Ready;
}

/// <summary>A product the owner shows on the public catalog (TK-195).</summary>
internal sealed class CatalogItem
{
    public Guid ProductId { get; set; }
}

/// <summary>Single row (Id 1).</summary>
internal sealed class CatalogSettings
{
    public int Id { get; set; } = 1;
    public string? WhatsApp { get; set; }
}

internal static class OrdersErrors
{
    public static readonly Error NotFound = Error.NotFound("Orders.NotFound", "El pedido o la cotización no existe.");
    public static readonly Error Closed = Error.Conflict("Orders.Closed", "Ese pedido o cotización ya se cobró o se canceló.");
    public static readonly Error NotAnOrder = Error.Conflict("Orders.NotAnOrder", "Solo un pedido se puede marcar listo o cancelar.");
    public static readonly Error DepositNeedsShift = Error.Validation("Orders.DepositNeedsShift", "Devolver el abono necesita una caja abierta.");
    public static readonly Error PastPickup = Error.Validation("Orders.PastPickup", "La fecha de entrega ya pasó.");
    public static readonly Error DepositTooBig = Error.Validation("Orders.DepositTooBig", "El abono es mayor que el total del pedido.");
}
