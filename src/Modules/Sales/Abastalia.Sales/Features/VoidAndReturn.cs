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
using Abastalia.Fiscal.Contracts;
using Abastalia.Inventory.Contracts;
using Abastalia.SharedKernel;
using Abastalia.Sales.Contracts;
using Abastalia.Sales.Domain;

namespace Abastalia.Sales.Features;

/// <summary>Voids a whole sale while its shift is still open: money out of the shift, stock back, and a credit note (an issued e-CF is never deleted).
/// Needs sales.void or a supervisor. After the shift closes, use a return.</summary>
internal static class VoidSale
{
    public sealed record Command(Guid SaleId, string Reason, Guid? SupervisorApprovalId) : ICommand<Response>, IRequiresApproval
    {
        public string Permission => Permissions.SalesVoid;
        public Guid ResourceId => SaleId;
    }

    public sealed record Response(Guid SaleId, string CreditNoteENcf, decimal Total);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
    }

    internal sealed class Handler(SalesDbContext db, ICashRegisterOperations cash, IStockMovements inventory, IFiscalDocuments fiscal,
        ICustomerAccounts customers, IUserContext user, IOutbox outbox, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Lock order: sale row → shift → stock → fiscal sequence → customer → audit.
            var sale = await db.LockSaleAsync(c.SaleId, ct);
            if (sale is null) return SalesErrors.SaleNotFound;
            if (sale.Status == SaleStatus.Voided) return SalesErrors.AlreadyVoided;
            if (sale.Lines.Any(l => l.ReturnedQuantity > 0)) return SalesErrors.HasReturns;

            var shift = await cash.GetOpenShiftAsync(sale.ShiftId, ct);
            if (shift.IsFailure) return shift.Error!.Code == "CashRegister.ShiftClosed" ? SalesErrors.OutsideShift : shift.Error;

            var inbound = await inventory.RecordInboundAsync(StockMovementType.SaleVoid, sale.Id,
                sale.Lines.Select(l => new StockMovementLine(l.ProductId, Math.Round(l.Quantity * l.Factor, 3), l.UnitCost / l.Factor)).ToList(),
                updateCost: false, ct);
            if (inbound.IsFailure) return inbound.Error!;
            var collected = await cash.VoidMovementsAsync(sale.Id, ct);
            if (collected.IsFailure) return collected.Error!;

            var note = await fiscal.IssueAsync(new DocumentToIssue(EcfType.CreditNote34, sale.Id, sale.CustomerRnc, sale.CustomerName,
                sale.Lines.Select(l => new FiscalLine(l.Description, l.Quantity, l.UnitPrice, l.Discount, l.ItbisRate, l.Itbis, l.Total)).ToList(),
                sale.Itbis, sale.Total, sale.ENcf, ModificationCode.Void), ct);
            if (note.IsFailure) return note.Error!;
            if (sale.CustomerId is not null) await customers.VoidSaleAsync(sale.Id, ct);

            sale.Status = SaleStatus.Voided;
            sale.VoidedUtc = clock.GetUtcNow().UtcDateTime;
            sale.VoidReason = c.Reason.Trim();
            sale.VoidedById = user.UserId;
            sale.VoidENcf = note.Value.ENcf;
            outbox.Add(new SaleVoidedV1(sale.Id, note.Value.ENcf, sale.VoidReason, user.SupervisorId));
            await audit.RecordAsync("sales.void", "Sale", sale.Id, new { sale.Number, sale.ENcf, sale.Total, c.Reason, CreditNote = note.Value.ENcf }, ct);
            return new Response(sale.Id, note.Value.ENcf, sale.Total);
        }
    }

    internal sealed record Body(string Reason, Guid? SupervisorApprovalId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/sales/{id:guid}/void", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Reason, b.SupervisorApprovalId), ct)).ToHttp())
                .RequirePermission(Permissions.SalesCreate).RequireUsableLicense().WithTags("Sales"); // TK-279: a credit note is a new e-CF
    }
}

/// <summary>Partial return against the original receipt: by line and quantity, stock back (unless it cannot be resold),
/// money back from the current shift, and a credit note. Needs sales.return or a supervisor.</summary>
internal static class ReturnSale
{
    /// <param name="Restock">False when the returned goods go to waste (meat that cannot be sold again).</param>
    public sealed record Line(Guid SaleLineId, decimal Quantity, bool Restock = true);

    /// <param name="ReturnId">Generated by the register; repeating the request returns the same return.</param>
    /// <param name="ShiftId">The shift that pays the money back (today's, not necessarily the sale's).</param>
    /// <param name="SupervisorApprovalId">Without sales.return: a supervisor's approval of exactly these lines, quantities, amount and method
    /// (R11 A1), checked by the handler once the lines are read.</param>
    public sealed record Command(Guid SaleId, Guid ReturnId, Guid ShiftId, IReadOnlyList<Line> Lines, PaymentMethod Method, string Reason,
        Guid? SupervisorApprovalId) : ICommand<Response>;

    public sealed record Response(Guid ReturnId, string CreditNoteENcf, decimal Total, decimal Itbis, PaymentMethod Method);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.ReturnId).NotEmpty();
            RuleFor(c => c.Lines).NotEmpty();
            RuleForEach(c => c.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).GreaterThan(0).PrecisionScale(19, 3, true));
            RuleFor(c => c.Lines).Must(l => l.Select(x => x.SaleLineId).Distinct().Count() == l.Count).WithMessage("Hay líneas repetidas.");
            RuleFor(c => c.Method).Must(m => m is PaymentMethod.Cash or PaymentMethod.Card or PaymentMethod.Transfer or PaymentMethod.CreditBalance)
                .WithMessage("La devolución se paga en efectivo, a la tarjeta, por transferencia o a la cuenta del cliente.");
            RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
        }
    }

    internal sealed class Handler(SalesDbContext db, ICashRegisterOperations cash, IStockMovements inventory, IFiscalDocuments fiscal,
        ICustomerAccounts customers, IUserContext user, IOutbox outbox, IAuditLog audit, ISupervisorApprovals approvals, IApprovalPolicy policy,
        TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await AlreadyRecordedAsync(c.ReturnId, ct) is { } already) return already;

            // Lock order: sale row → shift → stock → fiscal sequence → customer → audit. The sale lock makes returns and voids of one sale run one at a time.
            var sale = await db.LockSaleAsync(c.SaleId, ct);
            if (sale is null) return SalesErrors.SaleNotFound;
            if (await AlreadyRecordedAsync(c.ReturnId, ct) is { } repeated) return repeated;
            if (sale.Status == SaleStatus.Voided) return SalesErrors.AlreadyVoided;

            var shift = await cash.GetOpenShiftAsync(c.ShiftId, ct);
            if (shift.IsFailure) return shift.Error!;

            var ids = c.Lines.Select(l => l.SaleLineId).ToList();
            var alreadyReturned = await db.Set<ReturnLine>().Where(l => ids.Contains(l.SaleLineId))
                .GroupBy(l => l.SaleLineId).Select(g => new { g.Key, Total = g.Sum(x => x.Total), Itbis = g.Sum(x => x.Itbis) })
                .ToDictionaryAsync(x => x.Key, x => (x.Total, x.Itbis), ct);

            var saleReturn = new SaleReturn
            {
                Id = c.ReturnId, SaleId = sale.Id, ShiftId = c.ShiftId, Method = c.Method, Reason = c.Reason.Trim(),
                DateUtc = clock.GetUtcNow().UtcDateTime, UserId = user.UserId, SupervisorId = user.SupervisorId,
            };
            var fiscalLines = new List<FiscalLine>();
            var restock = new List<StockMovementLine>();
            foreach (var l in c.Lines.OrderBy(l => l.SaleLineId))
            {
                var sl = sale.Lines.FirstOrDefault(x => x.Id == l.SaleLineId);
                if (sl is null) return SalesErrors.SaleNotFound;
                var pending = sl.Quantity - sl.ReturnedQuantity;
                if (l.Quantity > pending) return SalesErrors.ReturnQuantityExceeded;
                if (sl.Unit == UnitOfMeasure.Unit && sl.PresentationId is null && l.Quantity != Math.Truncate(l.Quantity))
                    return Error.Validation("Sales.InvalidQuantity", $"{sl.Description} se devuelve por unidad.");

                // The last piece returned takes whatever is left, so the line never refunds more (or other ITBIS) than it charged.
                var (previousAmount, previousItbis) = alreadyReturned.GetValueOrDefault(sl.Id);
                var last = l.Quantity == pending;
                var amount = last ? sl.Total - previousAmount : Money.Round(sl.Total * l.Quantity / sl.Quantity);
                var itbis = last ? sl.Itbis - previousItbis : Itbis.Included(amount, sl.ItbisRate);
                sl.ReturnedQuantity += l.Quantity;
                saleReturn.Lines.Add(new ReturnLine { ReturnId = saleReturn.Id, SaleLineId = sl.Id, Quantity = l.Quantity, Total = amount, Itbis = itbis, Restocked = l.Restock });
                // DGII checks Price × Quantity − Discount = ItemAmount; the returned amount is already net of the sale's discounts.
                var discount = Math.Max(0, Money.Round(sl.UnitPrice * l.Quantity) - amount);
                fiscalLines.Add(new FiscalLine(sl.Description, l.Quantity, sl.UnitPrice, discount, sl.ItbisRate, itbis, amount));
                if (l.Restock) restock.Add(new StockMovementLine(sl.ProductId, Math.Round(l.Quantity * sl.Factor, 3), sl.UnitCost / sl.Factor));
            }
            saleReturn.Total = saleReturn.Lines.Sum(l => l.Total);
            saleReturn.Itbis = saleReturn.Lines.Sum(l => l.Itbis);

            if (c.Method == PaymentMethod.CreditBalance && sale.CustomerId is null) return SalesErrors.MethodUnavailable;
            if (c.Method != PaymentMethod.CreditBalance)
            {
                // Money goes back the way it came in: a card sale is never refunded in cash.
                var charged = sale.Payments.Where(p => p.Method == c.Method).Sum(p => p.Amount);
                var returnedThisWay = await db.Returns.Where(r => r.SaleId == sale.Id && r.Method == c.Method).Select(r => r.Total).ToListAsync(ct);
                if (saleReturn.Total > charged - returnedThisWay.Sum()) return SalesErrors.MethodExceeded(c.Method, Math.Max(0, charged - returnedThisWay.Sum()));
            }

            // R11 A1: the supervisor approves these lines and quantities, this amount and this way of paying back; another return is refused.
            string? summary = null;
            if (!user.Has(Permissions.SalesReturn) && await policy.RequiresSupervisorAsync(Permissions.SalesReturn, ct))
            {
                summary = $"Devolver {ApprovalErrors.Money(saleReturn.Total)} por {PaymentMethodNames.Display(c.Method)} de la venta {sale.Number}: " + string.Join(", ",
                    from l in saleReturn.Lines join s in sale.Lines on l.SaleLineId equals s.Id orderby s.Position
                    select $"{ApprovalErrors.Quantity(l.Quantity)} × {s.Description}");
                if (c.SupervisorApprovalId is not { } id) return ApprovalErrors.Required(Permissions.SalesReturn, summary);
                var supervisor = await approvals.ConsumeAsync(id, Permissions.SalesReturn, c.SaleId, summary, ct);
                if (supervisor.IsFailure) return supervisor.Error!;
                saleReturn.SupervisorId = user.SupervisorId = supervisor.Value;
            }

            if (restock.Count > 0)
            {
                var inbound = await inventory.RecordInboundAsync(StockMovementType.Return, saleReturn.Id, restock, updateCost: false, ct);
                if (inbound.IsFailure) return inbound.Error!;
            }
            if (c.Method != PaymentMethod.CreditBalance)
            {
                // No coins under RD$ 1: like a sale's change, cash goes back in whole pesos rounded down and the drawer keeps the
                // centavos as rounding, so expected cash stays countable. The credit note and the Z report's refunds keep the exact amount.
                var rounding = c.Method == PaymentMethod.Cash ? saleReturn.Total - Math.Floor(saleReturn.Total) : 0m;
                var refund = await cash.RecordRefundAsync(c.ShiftId, saleReturn.Id, c.Method, saleReturn.Total, rounding, ct);
                if (refund.IsFailure) return refund.Error!;
            }

            var note = await fiscal.IssueAsync(new DocumentToIssue(EcfType.CreditNote34, saleReturn.Id, sale.CustomerRnc, sale.CustomerName,
                fiscalLines, saleReturn.Itbis, saleReturn.Total, sale.ENcf, ModificationCode.AmountCorrection), ct);
            if (note.IsFailure) return note.Error!;
            saleReturn.ENcf = note.Value.ENcf;

            // To the customer account: pays down this sale's unpaid credit first, the rest is credit balance.
            if (c.Method == PaymentMethod.CreditBalance) await customers.CreditReturnAsync(sale.CustomerId!.Value, sale.Id, saleReturn.Id, saleReturn.Total, ct);

            db.Returns.Add(saleReturn);
            outbox.Add(new ReturnRecordedV2(saleReturn.Id, sale.Id, saleReturn.ENcf, c.Method.ToString(), saleReturn.Total, saleReturn.Itbis,
                [.. from l in saleReturn.Lines
                    join s in sale.Lines on l.SaleLineId equals s.Id
                    select new ReturnLineV2(s.ProductId, s.PresentationId, s.Description, l.Quantity, l.Total, l.Itbis, s.UnitCost)]));
            await audit.RecordAsync("sales.return", "Sale", sale.Id,
                new { Return = saleReturn.Id, saleReturn.Total, c.Method, c.Reason, CreditNote = saleReturn.ENcf }, ct);
            if (summary is not null)
                await audit.RecordAsync("approval.used", nameof(ReturnSale), c.SaleId, new { Permission = Permissions.SalesReturn, Approval = c.SupervisorApprovalId, Summary = summary }, ct);
            return new Response(saleReturn.Id, saleReturn.ENcf, saleReturn.Total, saleReturn.Itbis, c.Method);
        }

        private async Task<Response?> AlreadyRecordedAsync(Guid returnId, CancellationToken ct) =>
            await db.Returns.AsNoTracking().Where(r => r.Id == returnId)
                .Select(r => new Response(r.Id, r.ENcf, r.Total, r.Itbis, r.Method)).FirstOrDefaultAsync(ct);
    }

    internal sealed record Body(Guid ReturnId, Guid ShiftId, IReadOnlyList<Line> Lines, PaymentMethod Method, string Reason, Guid? SupervisorApprovalId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/sales/{id:guid}/returns", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.ReturnId, b.ShiftId, b.Lines, b.Method, b.Reason, b.SupervisorApprovalId), ct)).ToHttp())
                .RequirePermission(Permissions.SalesCreate).RequireUsableLicense().WithTags("Sales"); // TK-279: a credit note is a new e-CF
    }
}
