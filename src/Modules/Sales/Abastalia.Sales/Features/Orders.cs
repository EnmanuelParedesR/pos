using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.CashRegister.Contracts;
using Abastalia.Customers.Contracts;
using Abastalia.Inventory.Contracts;
using Abastalia.SharedKernel;
using Abastalia.Sales.Domain;

namespace Abastalia.Sales.Features;

/// <summary>Creates an order (TK-193) or a quote (TK-194) priced at today's prices for its customer. Nothing moves in stock or fiscal.
/// An order's deposit goes into the register now (its own line of the Z) and into the customer's credit balance, reserved for this order
/// (R8 #7: no other sale spends it); the register later charges the order as a normal sale paying that part with "Saldo a favor". <see cref="Command.OrderId"/> is the idempotency key.</summary>
internal static class CreateOrder
{
    public sealed record Line(Guid ProductId, Guid? PresentationId, decimal Quantity, string? Note = null);

    /// <param name="ShiftId">The open shift that receives the money.</param>
    public sealed record DepositInput(Guid ShiftId, PaymentMethod Method, decimal Amount);

    /// <param name="ValidDays">Quotes only: how many days the prices hold (15 when not sent).</param>
    public sealed record Command(Guid OrderId, OrderKind Kind, Guid? CustomerId, IReadOnlyList<Line> Lines, DateOnly? PickupDate = null,
        TimeOnly? PickupTime = null, string? Notes = null, int? ValidDays = null, DepositInput? Deposit = null) : ICommand<Response>;

    public sealed record Response(Guid OrderId, decimal Total);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.OrderId).NotEmpty();
            RuleFor(c => c.Kind).IsInEnum();
            RuleFor(c => c.Lines).NotEmpty().Must(l => l.Count <= 100).WithMessage("Un pedido tiene como máximo 100 líneas.");
            RuleForEach(c => c.Lines).ChildRules(l =>
            {
                l.RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(100_000).PrecisionScale(19, 3, true);
                l.RuleFor(x => x.Note).MaximumLength(100);
            });
            RuleFor(c => c.Notes).MaximumLength(200);
            RuleFor(c => c.CustomerId).NotNull().When(c => c.Kind == OrderKind.Order).WithMessage("El pedido necesita un cliente.");
            RuleFor(c => c.PickupDate).NotNull().When(c => c.Kind == OrderKind.Order).WithMessage("Indique la fecha de entrega.");
            RuleFor(c => c.ValidDays).InclusiveBetween(1, 90).When(c => c.ValidDays is not null);
            RuleFor(c => c.Deposit).Null().When(c => c.Kind == OrderKind.Quote).WithMessage("Una cotización no lleva abono.");
            RuleFor(c => c.Deposit!.Amount).GreaterThan(0).PrecisionScale(19, 2, true).When(c => c.Deposit is not null);
            RuleFor(c => c.Deposit!.Method).Must(m => m is PaymentMethod.Cash or PaymentMethod.Card or PaymentMethod.Transfer).When(c => c.Deposit is not null)
                .WithMessage("El abono se cobra en efectivo, con tarjeta o por transferencia.");
            // R8 #6: as RecordCustomerPayment, so the drawer and the Z match to the peso.
            RuleFor(c => c.Deposit!.Amount).Must(a => a == Math.Truncate(a)).When(c => c.Deposit is { Method: PaymentMethod.Cash })
                .WithMessage("El efectivo se recibe en pesos enteros.");
        }
    }

    internal sealed class Handler(SalesDbContext db, ICatalogQueries catalog, ICustomerAccounts customers, ICashRegisterOperations cash,
        IUserContext user, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == c.OrderId, ct) is { } saved) return new Response(saved.Id, saved.Total);
            var now = clock.GetUtcNow().UtcDateTime;
            if (c.PickupDate < BusinessDay.Of(now)) return OrdersErrors.PastPickup;

            CustomerForSale? customer = null;
            if (c.CustomerId is { } customerId)
            {
                customer = await customers.GetAsync(customerId, ct);
                if (customer is not { IsActive: true }) return Error.NotFound("Customers.CustomerNotFound", "El cliente no existe o está desactivado.");
            }

            var products = (await catalog.GetForSaleAsync(c.Lines.Select(l => (l.ProductId, l.PresentationId)).ToList(), ct))
                .ToDictionary(p => (p.ProductId, p.PresentationId));
            var lines = new List<OrderLine>();
            foreach (var l in c.Lines)
            {
                if (!products.TryGetValue((l.ProductId, l.PresentationId), out var p) || !p.IsActive) return SalesErrors.ProductUnavailable;
                var price = customer?.PriceLevel == PriceLevel.Wholesale ? p.WholesalePrice ?? p.Price : p.Price;
                var unit = p.PresentationId is null ? p.Unit : UnitOfMeasure.Unit;
                lines.Add(new OrderLine(p.ProductId, p.PresentationId, p.Name, unit.ToString(), l.Quantity, price, Money.Round(price * l.Quantity), l.Note?.Trim()));
            }
            var total = lines.Sum(l => l.Total);

            var order = new Order
            {
                Id = c.OrderId, Kind = c.Kind, Status = OrderStatus.Pending, CustomerId = customer?.Id, CustomerName = customer?.Name,
                PickupDate = c.PickupDate, PickupTime = c.PickupTime, Notes = string.IsNullOrWhiteSpace(c.Notes) ? null : c.Notes.Trim(),
                ValidUntil = c.Kind == OrderKind.Quote ? BusinessDay.Of(now).AddDays(c.ValidDays ?? 15) : null,
                Lines = JsonSerializer.Serialize(lines, Json), Total = total, CreatedUtc = now, CreatedById = user.UserId,
            };

            if (c.Deposit is { } d)
            {
                if (d.Amount > total) return OrdersErrors.DepositTooBig;
                // Lock order as in a sale: shift, then customer.
                var inRegister = await cash.RecordOrderDepositAsync(d.ShiftId, order.Id, d.Method, d.Amount, ct);
                if (inRegister.IsFailure) return inRegister.Error!;
                var balance = await customers.AddDepositAsync(customer!.Id, order.Id, d.Amount, ct);
                if (balance.IsFailure) return balance.Error!;
                (order.Deposit, order.DepositMethod) = (d.Amount, d.Method);
            }

            db.Orders.Add(order);
            await audit.RecordAsync(c.Kind == OrderKind.Order ? "orders.create" : "quotes.create", nameof(Order), order.Id,
                new { order.CustomerId, order.Total, order.Deposit, order.PickupDate, order.ValidUntil }, ct);
            return new Response(order.Id, total);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/orders", async (Command c, ICommandHandler<Command, Response> h, SalesDbContext db, CancellationToken ct) =>
                {
                    var result = await h.Handle(c, ct);
                    // TK-304: two simultaneous retries of a new order: the second one hits the key the first one saved. Same order, same answer.
                    if (result.IsFailure && result.Error!.Code == "Duplicate"
                        && await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == c.OrderId, ct) is { } saved)
                        result = new Response(saved.Id, saved.Total);
                    return result.ToHttp(r => Results.Created($"/api/orders/{r.OrderId}", r));
                })
                .RequirePermission(Permissions.SalesCreate).RequireUsableLicense().WithTags("Orders"); // TK-279
    }
}

/// <summary>Orders by pickup day, quotes by the day they were made; and one of them (to print or charge).</summary>
internal static class ListOrders
{
    public sealed record Item(Guid Id, OrderKind Kind, OrderStatus Status, Guid? CustomerId, string? CustomerName, DateOnly? PickupDate, TimeOnly? PickupTime,
        DateOnly? ValidUntil, string? Notes, decimal Total, decimal Deposit, PaymentMethod? DepositMethod, Guid? SaleId, DateTime CreatedUtc,
        IReadOnlyList<OrderLine> Lines);

    private static Item ToItem(Order o) => new(o.Id, o.Kind, o.Status, o.CustomerId, o.CustomerName, o.PickupDate, o.PickupTime, o.ValidUntil, o.Notes,
        o.Total, o.Deposit, o.DepositMethod, o.SaleId, o.CreatedUtc, JsonSerializer.Deserialize<List<OrderLine>>(o.Lines, CreateOrder.Json) ?? []);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            // shortcut: no paging; a range is at most 62 days, plenty for one store's orders and quotes.
            app.MapGet("/api/orders", async (OrderKind? kind, DateOnly from, DateOnly to, bool? openOnly, SalesDbContext db, CancellationToken ct) =>
                {
                    if (to < from || to.DayNumber - from.DayNumber > 62)
                        return EndpointExtensions.Problem(Error.Validation("Orders.InvalidRange", "El rango de fechas no es válido (máximo 62 días)."));
                    var q = db.Orders.AsNoTracking().Where(o => o.Kind == (kind ?? OrderKind.Order));
                    if (kind == OrderKind.Quote)
                    {
                        var (f, t) = BusinessDay.UtcRange(from, to);
                        q = q.Where(o => o.CreatedUtc >= f && o.CreatedUtc < t);
                    }
                    else q = q.Where(o => o.PickupDate >= from && o.PickupDate <= to);
                    if (openOnly == true) q = q.Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Ready);
                    var items = await q.ToListAsync(ct);
                    return Results.Ok(items.OrderBy(o => o.PickupDate).ThenBy(o => o.PickupTime).ThenBy(o => o.CreatedUtc).Select(ToItem));
                })
                .RequirePermission(Permissions.SalesCreate).WithTags("Orders");
            app.MapGet("/api/orders/{id:guid}", async (Guid id, SalesDbContext db, CancellationToken ct) =>
                    await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct) is { } o ? Results.Ok(ToItem(o)) : EndpointExtensions.Problem(OrdersErrors.NotFound))
                .RequirePermission(Permissions.SalesCreate).WithTags("Orders");
        }
    }
}

/// <summary>The order is prepared and waits for the customer.</summary>
internal static class MarkOrderReady
{
    public sealed record Command(Guid OrderId) : ICommand<Ok>;

    internal sealed class Handler(SalesDbContext db, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var order = await db.LockOrderAsync(c.OrderId, ct);
            if (order is null) return OrdersErrors.NotFound;
            if (order.Kind != OrderKind.Order) return OrdersErrors.NotAnOrder;
            if (!order.IsOpen) return OrdersErrors.Closed;
            order.Status = OrderStatus.Ready;
            await audit.RecordAsync("orders.ready", nameof(Order), order.Id, new { order.CustomerName }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/orders/{id:guid}/ready", async (Guid id, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SalesCreate).WithTags("Orders");
    }
}

/// <summary>Calls an order off. Needs sales.void or a supervisor. A deposit goes back from the given shift by the method it came in,
/// and leaves the customer's credit balance. R8 #7: a deposit the customer already spent (before deposits were reserved for their order, or
/// through the order's sale, voided later) does not block the cancellation: what is left goes back and the reason notes the rest.</summary>
internal static class CancelOrder
{
    public sealed record Command(Guid OrderId, Guid? ShiftId, string Reason, Guid? SupervisorApprovalId) : ICommand<Response>, IRequiresApproval
    {
        public string Permission => Permissions.SalesVoid;
        public Guid ResourceId => OrderId;
    }

    /// <param name="Refunded">What the cashier hands back: the deposit, or less when part of it was already spent.</param>
    public sealed record Response(decimal Refunded);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
    }

    internal sealed class Handler(SalesDbContext db, ICashRegisterOperations cash, ICustomerAccounts customers, IAuditLog audit, TimeProvider clock)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // TK-251: shift before order, as RegisterSale takes them, so a cancel and a sale charging the order cannot deadlock.
            // A closed shift is only an error when there is cash to give back (the refund below says so).
            if (c.ShiftId is { } lockShift) await cash.GetOpenShiftAsync(lockShift, ct);
            var order = await db.LockOrderAsync(c.OrderId, ct);
            if (order is null) return OrdersErrors.NotFound;
            if (order.Kind != OrderKind.Order) return OrdersErrors.NotAnOrder;
            if (!order.IsOpen) return OrdersErrors.Closed;
            var reason = c.Reason.Trim();
            var refunded = 0m;
            if (order.Deposit > 0)
            {
                var customerId = order.CustomerId!.Value;
                var left = ((await customers.GetAsync(customerId, ct))?.CreditBalance ?? 0) - await db.ReservedDepositsAsync(customerId, order.Id, ct);
                refunded = Math.Clamp(left, 0, order.Deposit);
                if (order.DepositMethod == PaymentMethod.Cash) refunded = Math.Truncate(refunded); // cash goes back in whole pesos
                if (refunded > 0)
                {
                    if (c.ShiftId is not { } shiftId) return OrdersErrors.DepositNeedsShift;
                    var refund = await cash.RecordRefundAsync(shiftId, order.Id, order.DepositMethod!.Value, refunded, 0, ct);
                    if (refund.IsFailure) return refund.Error!;
                    var balance = await customers.RefundDepositAsync(customerId, order.Id, refunded, ct);
                    if (balance.IsFailure) return balance.Error!;
                }
                if (refunded < order.Deposit)
                {
                    var note = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Abono ya usado: se devolvieron RD$ {refunded:N2} de RD$ {order.Deposit:N2}. {reason}");
                    reason = note.Length > 200 ? note[..200] : note;
                }
            }
            (order.Status, order.CancelReason, order.ClosedUtc) = (OrderStatus.Cancelled, reason, clock.GetUtcNow().UtcDateTime);
            await audit.RecordAsync("orders.cancel", nameof(Order), order.Id, new { order.CustomerName, order.Deposit, Refunded = refunded, c.Reason }, ct);
            return new Response(refunded);
        }
    }

    internal sealed record Body(Guid? ShiftId, string Reason, Guid? SupervisorApprovalId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/orders/{id:guid}/cancel", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.ShiftId, b.Reason, b.SupervisorApprovalId), ct)).ToHttp(Results.Ok))
                .RequirePermission(Permissions.SalesCreate).WithTags("Orders");
    }
}
