using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Purchasing.Contracts;
using Toki.Purchasing.Domain;
using Toki.Inventory.Contracts;
using Toki.SharedKernel;

namespace Toki.Purchasing.Features;

/// <summary>Creates or replaces a purchase draft. Nothing moves until it is posted.</summary>
internal static class SavePurchase
{
    /// <param name="UnitCost">Cost of one purchased unit (of the presentation when given), before ITBIS.</param>
    public sealed record Line(Guid ProductId, Guid? PresentationId, decimal Quantity, decimal UnitCost, ItbisRate? ItbisRate = null);

    /// <param name="IsNew">True for POST: <see cref="Id"/> comes from the client, and a retry returns the saved draft.</param>
    /// <param name="InvoiceItbis">ITBIS and total as printed on the invoice (optional): they win over Toki's line math when within RD$ 1.</param>
    public sealed record Command(Guid Id, bool IsNew, Guid SupplierId, string Ncf, DateOnly InvoiceDate, PaymentTerms Terms, IReadOnlyList<Line> Lines,
        Guid? WarehouseId = null, int? CreditDays = null, string? Notes = null, decimal? InvoiceItbis = null, decimal? InvoiceTotal = null,
        SupplierPaymentMethod? Method = null) : ICommand<Response>;

    public sealed record Response(Guid Id, decimal Subtotal, decimal Itbis, decimal Total);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator(TimeProvider clock)
        {
            RuleFor(c => c.Id).NotEmpty();
            RuleFor(c => c.InvoiceDate).Must(d => DocumentDate.IsValid(d, clock)).WithMessage(DocumentDate.Message);
            RuleFor(c => c.InvoiceItbis).GreaterThanOrEqualTo(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.InvoiceTotal).GreaterThan(0).PrecisionScale(19, 2, true);
            // A draft may wait for its invoice (TK-191: created from the suggested order); receiving it needs the NCF.
            RuleFor(c => c.Ncf).Must(Domain.Ncf.IsValid).When(c => !string.IsNullOrWhiteSpace(c.Ncf))
                .WithMessage("El NCF no es válido (B01… de 11 caracteres o E31… de 13).");
            RuleFor(c => c.Terms).IsInEnum();
            RuleFor(c => c.Method).IsInEnum();
            RuleFor(c => c.Lines).NotEmpty().Must(l => l.Count <= 500);
            RuleForEach(c => c.Lines).ChildRules(l =>
            {
                l.RuleFor(x => x.Quantity).GreaterThan(0).PrecisionScale(19, 3, true);
                l.RuleFor(x => x.UnitCost).GreaterThanOrEqualTo(0).PrecisionScale(19, 4, true);
                l.RuleFor(x => x.ItbisRate).IsInEnum();
            });
            RuleFor(c => c.CreditDays).InclusiveBetween(0, 365).When(c => c.CreditDays is not null);
            RuleFor(c => c.Notes).MaximumLength(500);
        }
    }

    internal sealed class Handler(PurchasingDbContext db, ICatalogQueries catalog, IUserContext user, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Lock first: an edit and a post of the same draft run one at a time (the post claims the same row).
            var purchase = await db.LockPurchaseAsync(c.Id, ct);
            if (c.IsNew && purchase is not null) return new Response(purchase.Id, purchase.Subtotal, purchase.Itbis, purchase.Total);
            if (!c.IsNew && purchase is null) return PurchasingErrors.PurchaseNotFound;
            if (purchase is { Status: not PurchaseStatus.Draft }) return PurchasingErrors.NotDraft;

            var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == c.SupplierId && s.IsActive, ct);
            if (supplier is null) return PurchasingErrors.SupplierNotFound;
            if (supplier.Rnc is null) return PurchasingErrors.SupplierWithoutRnc;
            var ncf = (c.Ncf ?? "").Trim().ToUpperInvariant();
            if (ncf.Length > 0 && (await db.Purchases.AnyAsync(x => x.SupplierId == c.SupplierId && x.Ncf == ncf && x.Status != PurchaseStatus.Voided && x.Id != c.Id, ct)
                || await db.Expenses.AnyAsync(e => e.SupplierId == c.SupplierId && e.Ncf == ncf && !e.IsVoided, ct)))
                return PurchasingErrors.DuplicateNcf;

            if (purchase is null)
            {
                purchase = new Purchase { Id = c.Id, Status = PurchaseStatus.Draft, CreatedUtc = clock.GetUtcNow().UtcDateTime, UserId = user.UserId };
                db.Purchases.Add(purchase);
            }

            var products = (await catalog.GetForSaleAsync(c.Lines.Select(l => (l.ProductId, l.PresentationId)).ToList(), ct))
                .ToDictionary(p => (p.ProductId, p.PresentationId));
            var lines = new List<PurchaseLine>();
            foreach (var (l, i) in c.Lines.Select((l, i) => (l, i)))
            {
                if (!products.TryGetValue((l.ProductId, l.PresentationId), out var p)) return PurchasingErrors.ProductNotFound;
                lines.Add(new PurchaseLine
                {
                    Id = Guid.CreateVersion7(), PurchaseId = purchase.Id, LineNumber = i + 1, ProductId = p.ProductId, PresentationId = p.PresentationId, Description = p.Name, Quantity = l.Quantity,
                    Factor = p.Factor, UnitCost = l.UnitCost, ItbisRate = l.ItbisRate ?? p.ItbisRate,
                });
            }

            db.RemoveRange(purchase.Lines);
            // Added explicitly: new lines (with their Guid already set) found only through the navigation of a saved draft were taken as existing
            // rows to update, so editing a draft failed with a concurrency error.
            db.AddRange(lines);
            purchase.Lines = lines;
            purchase.SupplierId = supplier.Id;
            purchase.SupplierName = supplier.Name;
            purchase.SupplierRnc = supplier.Rnc;
            purchase.Ncf = ncf;
            purchase.InvoiceDate = c.InvoiceDate;
            purchase.Terms = c.Terms;
            purchase.Method = c.Terms == PaymentTerms.Cash ? c.Method : null;
            purchase.DueDate = c.Terms == PaymentTerms.Credit ? c.InvoiceDate.AddDays(c.CreditDays ?? supplier.CreditDays) : null;
            purchase.WarehouseId = c.WarehouseId;
            purchase.Notes = c.Notes?.Trim();
            if (!purchase.Recalculate(c.InvoiceItbis, c.InvoiceTotal)) return PurchasingErrors.InvoiceTotalMismatch;
            return new Response(purchase.Id, purchase.Subtotal, purchase.Itbis, purchase.Total);
        }
    }

    /// <param name="Id">Generated by the client (GUID v7) on POST: a retry returns the same draft.</param>
    internal sealed record Body(Guid SupplierId, string Ncf, DateOnly InvoiceDate, PaymentTerms Terms, IReadOnlyList<Line> Lines,
        Guid? WarehouseId = null, int? CreditDays = null, string? Notes = null, decimal? InvoiceItbis = null, decimal? InvoiceTotal = null, Guid? Id = null,
        SupplierPaymentMethod? Method = null);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            static Command ToCommand(Guid id, bool isNew, Body b) =>
                new(id, isNew, b.SupplierId, b.Ncf, b.InvoiceDate, b.Terms, b.Lines, b.WarehouseId, b.CreditDays, b.Notes, b.InvoiceItbis, b.InvoiceTotal, b.Method);
            app.MapPost("/api/purchasing/purchases", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(ToCommand(b.Id ?? Guid.CreateVersion7(), true, b), ct)).ToHttp(r => Results.Created($"/api/purchasing/purchases/{r.Id}", r)))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
            app.MapPut("/api/purchasing/purchases/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(ToCommand(id, false, b), ct)).ToHttp())
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
        }
    }
}

/// <summary>Receives the goods: stock in at the purchase cost (weighted average updated) and, on credit, a payable.</summary>
internal static class PostPurchase
{
    public sealed record Command(Guid Id) : ICommand<Ok>;

    internal sealed class Handler(PurchasingDbContext db, IStockMovements stock, IOutbox outbox, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            // Claim the draft: two posts of the same purchase cannot both put stock in. A draft without its NCF yet cannot be received.
            var now = clock.GetUtcNow().UtcDateTime;
            if (await db.Purchases.AnyAsync(x => x.Id == c.Id && x.Status == PurchaseStatus.Draft && x.Ncf == "", ct)) return PurchasingErrors.NcfRequired;
            // A draft from the DGII Excel (TK-223) comes with the totals only: the products go in before receiving it.
            if (await db.Purchases.AnyAsync(x => x.Id == c.Id && x.Status == PurchaseStatus.Draft && !x.Lines.Any(), ct)) return PurchasingErrors.LinesRequired;
            if (await db.Purchases.Where(x => x.Id == c.Id && x.Status == PurchaseStatus.Draft)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PurchaseStatus.Posted).SetProperty(x => x.PostedUtc, now), ct) == 0)
                return await db.Purchases.AnyAsync(x => x.Id == c.Id, ct) ? PurchasingErrors.NotDraft : PurchasingErrors.PurchaseNotFound;
            var purchase = await db.Purchases.Include(x => x.Lines).AsNoTracking().SingleAsync(x => x.Id == c.Id, ct);

            var inbound = await stock.RecordInboundAsync(StockMovementType.Purchase, purchase.Id,
                purchase.Lines.Select(l => new StockMovementLine(l.ProductId, Math.Round(l.Quantity * l.Factor, 3), Math.Round(l.UnitCost / l.Factor, 4))).ToList(),
                updateCost: true, ct, purchase.WarehouseId);
            if (inbound.IsFailure) return inbound.Error!;

            if (purchase.Terms == PaymentTerms.Credit)
                db.Payables.Add(new Payable
                {
                    Id = Guid.CreateVersion7(), SupplierId = purchase.SupplierId, Type = PayableDocumentType.Purchase, DocumentId = purchase.Id, Ncf = purchase.Ncf,
                    Date = purchase.InvoiceDate, DueDate = purchase.DueDate ?? purchase.InvoiceDate, Total = purchase.Total,
                });
            outbox.Add(new PurchasePostedV1(purchase.Id, purchase.SupplierId, purchase.Ncf, purchase.Subtotal, purchase.Itbis, purchase.Total, purchase.Terms == PaymentTerms.Credit));
            await audit.RecordAsync("purchasing.post", "Purchase", purchase.Id, new { purchase.Number, purchase.Ncf, purchase.Total, purchase.Terms }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/purchasing/purchases/{id:guid}/post", async (Guid id, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}

/// <summary>Voids a purchase. A posted one takes its stock back out (never below zero), takes its cost back out of the average,
/// and needs no payments on it. Lock order: purchase row → supplier → stock → audit.</summary>
internal static class VoidPurchase
{
    public sealed record Command(Guid Id, string Reason) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(200);
    }

    internal sealed class Handler(PurchasingDbContext db, IStockMovements stock, IOutbox outbox, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var purchase = await db.LockPurchaseAsync(c.Id, ct);
            if (purchase is null) return PurchasingErrors.PurchaseNotFound;
            if (purchase.Status == PurchaseStatus.Voided) return PurchasingErrors.AlreadyVoided;

            if (purchase.Status == PurchaseStatus.Posted)
            {
                await db.LockSupplierAsync(purchase.SupplierId, ct);
                var payable = await db.Payables.FirstOrDefaultAsync(x => x.DocumentId == purchase.Id, ct);
                if (payable is not null && await db.HasPaymentsAsync(payable.Id, ct)) return PurchasingErrors.HasPayments;
                if (payable is not null) payable.IsVoided = true;

                // The same cost per base unit the post put in, so the average goes back to what it was.
                var outbound = await stock.RecordOutboundAsync(StockMovementType.PurchaseVoid, purchase.Id,
                    purchase.Lines.Select(l => new StockMovementLine(l.ProductId, Math.Round(l.Quantity * l.Factor, 3), Math.Round(l.UnitCost / l.Factor, 4))).ToList(),
                    ct, purchase.WarehouseId, onlyWithStock: true, reverseCost: true);
                if (outbound.IsFailure) return outbound.Error!;
                outbox.Add(new PurchaseVoidedV1(purchase.Id, c.Reason.Trim()));
            }
            purchase.Status = PurchaseStatus.Voided;
            purchase.VoidedUtc = clock.GetUtcNow().UtcDateTime;
            purchase.VoidReason = c.Reason.Trim();
            await audit.RecordAsync("purchasing.void", "Purchase", purchase.Id, new { purchase.Number, purchase.Ncf, purchase.Total, c.Reason }, ct);
            return Ok.Value;
        }
    }

    internal sealed record Body(string Reason);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/purchasing/purchases/{id:guid}/void", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Reason), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}

internal static class QueryPurchases
{
    public sealed record Item(Guid Id, long Number, string Supplier, string Ncf, DateOnly InvoiceDate, string Status, string Terms, decimal Total);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/purchasing/purchases/{id:guid}", async (Guid id, PurchasingDbContext db, CancellationToken ct) =>
                    await db.Purchases.Include(p => p.Lines.OrderBy(l => l.LineNumber)).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is { } purchase
                        ? Results.Ok(purchase)
                        : EndpointExtensions.Problem(PurchasingErrors.PurchaseNotFound))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
            app.MapGet("/api/purchasing/purchases", async (DateOnly? from, DateOnly? to, Guid? supplierId, PurchasingDbContext db, CancellationToken ct) =>
                {
                    var q = db.Purchases.AsNoTracking();
                    if (from is { } f) q = q.Where(p => p.InvoiceDate >= f);
                    if (to is { } t) q = q.Where(p => p.InvoiceDate <= t);
                    if (supplierId is { } s) q = q.Where(p => p.SupplierId == s);
                    return Results.Ok(await q.OrderByDescending(p => p.Number).Take(500)
                        .Select(p => new Item(p.Id, p.Number, p.SupplierName, p.Ncf, p.InvoiceDate, p.Status.ToString(), p.Terms.ToString(), p.Total)).ToListAsync(ct));
                })
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
        }
    }
}
