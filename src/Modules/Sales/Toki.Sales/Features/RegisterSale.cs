using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.CashRegister.Contracts;
using Toki.Customers.Contracts;
using Toki.Settings.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Inventory.Contracts;
using Toki.SharedKernel;
using Toki.Sales.Contracts;
using Toki.Sales.Domain;

namespace Toki.Sales.Features;

/// <summary>A sale in one transaction: totals and ITBIS, stock out, payments into the shift, e-CF, outbox and audit.
/// <see cref="Command.SaleId"/> comes from the register; repeating the request returns the same sale.</summary>
internal static class RegisterSale
{
    /// <param name="UnitPrice">Only when the cashier changes the price (needs sales.price-override).</param>
    /// <param name="ScaleLabel">Scale label as scanned. The server reads the weight or the amount from it; <see cref="Quantity"/> is then ignored.</param>
    public sealed record Line(Guid ProductId, Guid? PresentationId, decimal Quantity, decimal? UnitPrice = null, Discount? Discount = null,
        string? Note = null, string? ScaleLabel = null);

    public sealed record Payment(PaymentMethod Method, decimal Amount, string? Reference = null);

    /// <param name="ExpectedTotal">The total the cashier saw. If the server computes another one, nothing is saved and the new total is returned.</param>
    /// <param name="SupervisorApprovals">One-time supervisor approvals (for a big discount and/or a price change), bound to <see cref="SaleId"/>.</param>
    /// <param name="OrderId">The order or quote this sale charges (TK-193, TK-194): it is marked delivered in the same transaction.</param>
    public sealed record Command(
        Guid SaleId, Guid ShiftId, IReadOnlyList<Line> Lines, IReadOnlyList<Payment> Payments, decimal ExpectedTotal,
        EcfType DocumentType = EcfType.Consumer32, Guid? CustomerId = null, string? CustomerRnc = null, string? CustomerName = null,
        SalePriceLevel PriceLevel = SalePriceLevel.Retail, Discount? GlobalDiscount = null, IReadOnlyList<Guid>? SupervisorApprovals = null,
        Guid? OrderId = null) : ICommand<Response>;

    public sealed record Response(Guid SaleId, long Number, string ENcf, decimal Total, decimal Change, Receipt Receipt);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.SaleId).NotEmpty();
            RuleFor(c => c.Lines).NotEmpty().Must(l => l.Count <= 300).WithMessage("Una venta tiene como máximo 300 líneas.");
            RuleForEach(c => c.Lines).ChildRules(l =>
            {
                l.RuleFor(x => x.Quantity).GreaterThan(0).LessThanOrEqualTo(100_000).PrecisionScale(19, 3, true);
                l.RuleFor(x => x.UnitPrice).GreaterThan(0).PrecisionScale(19, 2, true).When(x => x.UnitPrice is not null);
                l.RuleFor(x => x.Note).MaximumLength(100);
                l.RuleFor(x => x.ScaleLabel).MaximumLength(20);
            });
            // TK-239: a RD$ 0 sale (100 % discount) has nothing to pay.
            RuleFor(c => c.Payments).NotEmpty().When(c => c.ExpectedTotal != 0);
            RuleForEach(c => c.Payments).ChildRules(p => { p.RuleFor(x => x.Method).IsInEnum(); p.RuleFor(x => x.Reference).MaximumLength(60); });
            RuleFor(c => c.DocumentType).Must(t => t is EcfType.Consumer32 or EcfType.FiscalCredit31).WithMessage("Tipo de comprobante no válido para una venta.");
            RuleFor(c => c.CustomerRnc).Must(r => Rnc.IsValid(Rnc.Normalize(r))).When(c => c.DocumentType == EcfType.FiscalCredit31 && c.CustomerId is null)
                .WithMessage("El crédito fiscal necesita el RNC o la cédula del cliente.");
            RuleFor(c => c.CustomerName).NotEmpty().When(c => c.DocumentType == EcfType.FiscalCredit31 && c.CustomerId is null);
            RuleFor(c => c.CustomerName).MaximumLength(150);
            RuleFor(c => c.CustomerId).NotNull().When(c => c.Payments.Any(p => p.Method is PaymentMethod.OnAccount or PaymentMethod.CreditBalance))
                .WithMessage("El fiado y el saldo a favor necesitan un cliente.");
            RuleFor(c => c.PriceLevel).IsInEnum();
        }
    }

    internal sealed class Handler(
        SalesDbContext db, ICashRegisterOperations cash, ICatalogQueries catalog, IStockMovements inventory, IFiscalDocuments fiscal,
        ISettingsQueries settings, ICustomerAccounts customers, ISupervisorApprovals approvals, IApprovalPolicy policy, IUserContext user, IOutbox outbox,
        IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var business = await settings.GetBusinessProfileAsync(ct);
            // A retry of a saved sale gets the same answer, even after the shift closed.
            if (await db.FullSaleAsync(c.SaleId, ct) is { } alreadySaved) return Repeated(alreadySaved, c, business);

            // Lock the shift: a concurrent retry of this same sale waits here and then finds it saved.
            var shift = await cash.GetOpenShiftAsync(c.ShiftId, ct);
            if (shift.IsFailure) return shift.Error!;
            if (await db.FullSaleAsync(c.SaleId, ct) is { } existing) return Repeated(existing, c, business);

            // After the shift lock: two registers charging the same order wait here, and the second finds it delivered.
            var order = c.OrderId is { } orderId ? await db.LockOrderAsync(orderId, ct) : null;
            if (c.OrderId is not null && order is null) return OrdersErrors.NotFound;
            if (order is { IsOpen: false }) return OrdersErrors.Closed;

            var labels = c.Lines.Where(l => l.ScaleLabel is not null).Select(l => l.ScaleLabel).ToList();
            if (labels.Count != labels.Distinct().Count()) return SalesErrors.DuplicateScaleLabel;

            CustomerForSale? customer = null;
            if (c.CustomerId is { } customerId)
            {
                customer = await customers.GetAsync(customerId, ct);
                if (customer is not { IsActive: true }) return Error.NotFound("Customers.CustomerNotFound", "El cliente no existe o está desactivado.");
            }
            // R8 #14: an order is charged to its own customer (its deposit and prices are theirs).
            if (order?.CustomerId is { } orderCustomer && orderCustomer != c.CustomerId) return OrdersErrors.OtherCustomer;
            var rnc = Rnc.Normalize(c.CustomerRnc) ?? customer?.TaxId;
            var customerName = c.CustomerName?.Trim() ?? customer?.Name;

            var products = (await catalog.GetForSaleAsync(c.Lines.Select(l => (l.ProductId, l.PresentationId)).ToList(), ct))
                .ToDictionary(p => (p.ProductId, p.PresentationId));
            var permissions = new HashSet<string>();
            var inputs = new List<(Line Line, ProductForSale Product, decimal Price, decimal Quantity, decimal? FixedAmount)>();
            foreach (var l in c.Lines)
            {
                if (!products.TryGetValue((l.ProductId, l.PresentationId), out var p) || !p.IsActive) return SalesErrors.ProductUnavailable;
                var list = c.PriceLevel == SalePriceLevel.Wholesale ? p.WholesalePrice ?? p.Price : p.Price;
                var price = l.UnitPrice ?? list;
                var quantity = l.Quantity;
                decimal? fixedAmount = null;
                if (l.ScaleLabel is { } code)
                {
                    var label = await catalog.ReadScaleLabelAsync(code, ct);
                    if (label.IsFailure) return label.Error!;
                    if (label.Value.ProductId != l.ProductId || l.PresentationId is not null) return SalesErrors.ScaleLabelMismatch;
                    if (label.Value.Weight is { } weight) quantity = weight;
                    // A price label fixes the amount; the quantity follows from the price (for stock).
                    if (label.Value.Amount is { } amount) (fixedAmount, quantity) = (amount, Math.Round(amount / price, 3, MidpointRounding.AwayFromZero));
                }
                if (p.Unit == UnitOfMeasure.Unit && p.PresentationId is null && quantity != Math.Truncate(quantity))
                    return Error.Validation("Sales.InvalidQuantity", $"{p.Name} se vende por unidad.");

                var wholesaleWithoutCustomer = c.PriceLevel == SalePriceLevel.Wholesale && customer?.PriceLevel != PriceLevel.Wholesale;
                if (price != list || wholesaleWithoutCustomer) permissions.Add(Permissions.SalesPriceOverride);
                inputs.Add((l, p, price, quantity, fixedAmount));
            }

            var totals = TotalsCalculator.Calculate(
                inputs.Select(e => new LineInput(e.Price, e.Quantity, e.Product.ItbisRate, e.Line.Discount, e.FixedAmount)).ToList(),
                c.GlobalDiscount);
            if (totals.IsFailure) return totals.Error!;
            var t = totals.Value;
            if (t.Total != c.ExpectedTotal) return SalesErrors.TotalChanged(t.Total);
            if ((c.DocumentType == EcfType.FiscalCredit31 || t.Total >= EcfLimits.IdentifiedConsumer) && !Rnc.IsValid(rnc)) return SalesErrors.RncRequired;

            // Per line, after every discount: a big discount on one item, or a line sold under its cost, needs a supervisor.
            for (var i = 0; i < inputs.Count; i++)
            {
                var line = t.Lines[i];
                var cost = Money.Round(inputs[i].Product.BaseCost * inputs[i].Product.Factor * inputs[i].Quantity);
                if (line.Discount * 100 > line.Gross * business.DiscountCapPercent) permissions.Add(Permissions.SalesLargeDiscount);
                if (line.Total - line.Itbis < cost) permissions.Add(Permissions.SalesPriceOverride);
            }

            var payments = Payments.Apply(t.Total, c.Payments.Select(p => new PaymentInput(p.Method, p.Amount, p.Reference?.Trim())).ToList());
            if (payments.IsFailure) return payments.Error!;
            var onAccount = payments.Value.Payments.Where(p => p.Method == PaymentMethod.OnAccount).Sum(p => p.Amount);
            var creditBalance = payments.Value.Payments.Where(p => p.Method == PaymentMethod.CreditBalance).Sum(p => p.Amount);
            if (onAccount > 0 && customer!.Debt + onAccount > customer.CreditLimit) permissions.Add(Permissions.CreditExceedLimit);

            // shortcut: one sales.price-override approval covers every line of the sale; bind it to the expected total if supervisors ask for it.
            var approved = await ApproveAsync(permissions, c, ct);
            if (approved.IsFailure) return approved.Error!;

            // Lock order, the same in every handler: shift → stock (by ProductId) → fiscal sequence → customer → audit.
            var outbound = await inventory.RecordOutboundAsync(StockMovementType.Sale, c.SaleId,
                inputs.Select(e => new StockMovementLine(e.Product.ProductId, Math.Round(e.Quantity * e.Product.Factor, 3))).ToList(), ct);
            if (outbound.IsFailure) return outbound.Error!;

            await cash.RecordSalePaymentAsync(c.ShiftId, c.SaleId, payments.Value.Payments.Select(p => new PaymentByMethod(p.Method, p.Amount, p.Reference)).ToList(),
                payments.Value.Rounding, ct);

            var fiscalLines = inputs.Select((e, i) => new FiscalLine(e.Product.Name, e.Quantity, e.Price, t.Lines[i].Discount,
                e.Product.ItbisRate, t.Lines[i].Itbis, t.Lines[i].Total)).ToList();
            var document = await fiscal.IssueAsync(new DocumentToIssue(c.DocumentType, c.SaleId, rnc, customerName, fiscalLines, t.Itbis, t.Total,
                PaymentType: onAccount > 0 ? EcfPaymentType.Credit : EcfPaymentType.Cash,
                Payments: payments.Value.Payments.Select(p => new FiscalPayment(p.Method switch
                {
                    PaymentMethod.Cash => EcfPaymentForm.Cash,
                    PaymentMethod.Card => EcfPaymentForm.Card,
                    PaymentMethod.Transfer => EcfPaymentForm.CheckOrTransfer,
                    PaymentMethod.OnAccount => EcfPaymentForm.OnCredit,
                    _ => EcfPaymentForm.Other, // shortcut: customer credit balance as "other"; ask the DGII whether it is 7 (credit note)
                }, p.Amount)).ToList()), ct);
            if (document.IsFailure) return document.Error!;

            // The customer's limit is re-checked under the customer's lock.
            if (creditBalance > 0 && await customers.UseCreditBalanceAsync(customer!.Id, c.SaleId, creditBalance,
                    await db.ReservedDepositsAsync(customer.Id, c.OrderId, ct), ct) is { IsFailure: true } noBalance)
                return noBalance.Error!;
            if (onAccount > 0 && await customers.ChargeOnAccountAsync(customer!.Id, c.SaleId, document.Value.ENcf, onAccount,
                    allowOverLimit: permissions.Contains(Permissions.CreditExceedLimit), ct) is { IsFailure: true } limit)
                return limit.Error!;

            var sale = new Sale
            {
                Id = c.SaleId, ShiftId = c.ShiftId, RegisterId = shift.Value.RegisterId, Register = shift.Value.Register, CashierId = user.UserId, Cashier = user.Name,
                CustomerId = c.CustomerId, CustomerName = customerName, CustomerRnc = rnc, DocumentType = c.DocumentType,
                ENcf = document.Value.ENcf, SecurityCode = document.Value.SecurityCode, QrUrl = document.Value.QrUrl, PriceLevel = c.PriceLevel,
                SignedUtc = document.Value.SignedUtc, SequenceExpiresOn = document.Value.SequenceExpiresOn, Contingency = document.Value.Contingency,
                Gross = t.Gross, Discount = t.Discount, Subtotal = t.Subtotal, Itbis = t.Itbis, Total = t.Total, Change = payments.Value.Change,
                Rounding = payments.Value.Rounding, Status = SaleStatus.Completed, DateUtc = clock.GetUtcNow().UtcDateTime, SupervisorId = user.SupervisorId,
                Lines = inputs.Select((e, i) => new SaleLine
                {
                    Id = Guid.CreateVersion7(), SaleId = c.SaleId, Position = i + 1, ProductId = e.Product.ProductId, PresentationId = e.Product.PresentationId,
                    CategoryId = e.Product.CategoryId, Description = e.Product.Name, Unit = e.Product.Unit, Quantity = e.Quantity, Factor = e.Product.Factor,
                    ListPrice = c.PriceLevel == SalePriceLevel.Wholesale ? e.Product.WholesalePrice ?? e.Product.Price : e.Product.Price, UnitPrice = e.Price,
                    Gross = t.Lines[i].Gross, Discount = t.Lines[i].Discount, ItbisRate = e.Product.ItbisRate, Itbis = t.Lines[i].Itbis,
                    Total = t.Lines[i].Total, UnitCost = Math.Round(e.Product.BaseCost * e.Product.Factor, 4), Note = e.Line.Note?.Trim(),
                    ScaleLabel = e.Line.ScaleLabel,
                }).ToList(),
                Payments = payments.Value.Payments.Select(p => new SalePayment { Id = Guid.CreateVersion7(), SaleId = c.SaleId, Method = p.Method, Amount = p.Amount, Received = p.Received, Reference = p.Reference }).ToList(),
            };
            db.Sales.Add(sale);
            if (order is not null) (order.Status, order.SaleId, order.ClosedUtc) = (OrderStatus.Delivered, sale.Id, sale.DateUtc);
            await db.SaveChangesAsync(ct); // assigns the receipt number

            outbox.Add(new SaleRecordedV2(sale.Id, sale.Number, sale.ShiftId, sale.CashierId, sale.CustomerId, sale.ENcf, sale.Total, sale.Itbis, sale.Discount,
                sale.Lines.Select(l => new SaleLineV2(l.ProductId, l.PresentationId, l.Description, l.Quantity, l.Unit.ToString(), l.Total, l.Itbis, l.UnitCost)).ToList(),
                sale.Payments.Select(p => new SalePaymentV1(p.Method.ToString(), p.Amount)).ToList()));
            await audit.RecordAsync("sales.create", "Sale", sale.Id,
                new { sale.Number, sale.ENcf, sale.Total, sale.Discount, DiscountPercent = t.DiscountPercent, Permissions = permissions, c.OrderId }, ct);
            return ToResponse(sale, business);
        }

        /// <summary>Each escalated permission the cashier lacks must be covered by one of the supervisor approvals sent.</summary>
        private async Task<Result> ApproveAsync(HashSet<string> permissions, Command c, CancellationToken ct)
        {
            var available = c.SupervisorApprovals?.Distinct().ToList() ?? [];
            foreach (var permission in permissions.Where(p => !user.Has(p)).Order())
            {
                if (!await policy.RequiresSupervisorAsync(permission, ct)) continue; // the store turned this supervisor off (TK-210)
                Guid? supervisor = null;
                foreach (var id in available)
                {
                    var r = await approvals.ConsumeAsync(id, permission, c.SaleId, ct);
                    if (r.IsFailure) continue;
                    supervisor = r.Value;
                    available.Remove(id);
                    break;
                }
                if (supervisor is null) return ApprovalErrors.Required(permission);
                user.SupervisorId = supervisor;
            }
            return Result.Success();
        }

        /// <summary>A retry gets the saved sale; a different cart under the same id (a register bug) must not be "paid" with the old sale.</summary>
        private static Result<Response> Repeated(Sale s, Command c, BusinessProfile business) =>
            s.Total == c.ExpectedTotal ? ToResponse(s, business) : SalesErrors.DuplicateId;

        private static Response ToResponse(Sale s, BusinessProfile business) =>
            new(s.Id, s.Number, s.ENcf, s.Total, s.Change, Receipt.From(s, business));
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/sales", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/sales/{r.SaleId}", r)))
                .RequirePermission(Permissions.SalesCreate).WithTags("Sales");
    }
}
