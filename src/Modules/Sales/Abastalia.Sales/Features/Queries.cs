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
using Abastalia.Settings.Contracts;
using Abastalia.Fiscal.Contracts;
using Abastalia.Inventory.Contracts;
using Abastalia.SharedKernel;
using Abastalia.Sales.Domain;

namespace Abastalia.Sales.Features;

/// <summary>Reprint: the same print model as the original sale (also for a voided one, marked as voided).</summary>
internal static class GetReceipt
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/sales/{id:guid}/receipt", async (Guid id, SalesDbContext db, ISettingsQueries settings, IUserContext user,
                    ICashRegisterReports cash, TimeProvider clock, CancellationToken ct) =>
                {
                    var s = await db.FullSaleAsync(id, ct);
                    if (s is null || !SaleAccess.MayRead(user, s)) return EndpointExtensions.Problem(SalesErrors.SaleNotFound);
                    var receipt = Receipt.From(s, await settings.GetBusinessProfileAsync(ct));
                    // R11 A2 (TK-268): like the sale detail, the reprint shows no payments nor change to its cashier while the shift is open.
                    // R12: except the sale they just charged (an uncertain checkout recovers its change and receipt): they saw that money already.
                    var justCharged = s.CashierId == user.UserId && clock.GetUtcNow().UtcDateTime - s.DateUtc < JustCharged;
                    return Results.Ok(!SaleAccess.SeesAll(user) && !justCharged && await cash.IsOpenAsync(s.ShiftId, ct) ? receipt with { Payments = [], Change = 0 } : receipt);
                })
                .RequirePermission(Permissions.SalesCreate).WithTags("Sales");
    }

    public static readonly TimeSpan JustCharged = TimeSpan.FromMinutes(10);
}

/// <summary>R11 A13: a cashier reads their own sales; other people's only with reports, Turnos y cuadres, or the right to void or return them
/// (a return of another cashier's sale). Someone else's sale answers "not found", like one that does not exist.</summary>
internal static class SaleAccess
{
    public static bool SeesAll(IUserContext user) => user.Has(Permissions.ReportsView) || user.Has(Permissions.ShiftsView);

    public static bool MayRead(IUserContext user, Sale s) =>
        s.CashierId == user.UserId || SeesAll(user) || user.Has(Permissions.SalesReturn) || user.Has(Permissions.SalesVoid);
}

internal static class GetSale
{
    public sealed record LineDto(Guid Id, string Description, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total,
        decimal Itbis, decimal ReturnedQuantity, string? Note, UnitOfMeasure Unit);
    public sealed record PaymentDto(PaymentMethod Method, decimal Amount, decimal? Received, string? Reference);
    public sealed record ReturnDto(Guid Id, DateTime DateUtc, decimal Total, PaymentMethod Method, string ENcf, string Reason);
    public sealed record Response(Guid Id, long Number, string ENcf, EcfType DocumentType, DateTime DateUtc, string Status, string Register, string Cashier,
        Guid? CustomerId, string? Customer, decimal Discount, decimal Itbis, decimal Total, decimal Change, string? VoidReason, string? VoidENcf,
        IReadOnlyList<LineDto> Lines, IReadOnlyList<PaymentDto> Payments, IReadOnlyList<ReturnDto> Returns);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/sales/{id:guid}", async (Guid id, SalesDbContext db, IUserContext user, ICashRegisterReports cash, CancellationToken ct) =>
                {
                    var s = await db.FullSaleAsync(id, ct);
                    if (s is null || !SaleAccess.MayRead(user, s)) return EndpointExtensions.Problem(SalesErrors.SaleNotFound);
                    var returns = await db.Returns.Where(r => r.SaleId == id).OrderBy(r => r.DateUtc)
                        .Select(r => new ReturnDto(r.Id, r.DateUtc, r.Total, r.Method, r.ENcf, r.Reason)).AsNoTracking().ToListAsync(ct);
                    // R11 A2: while the shift is open its cashier does not see how each sale was paid (nor the change): adding them up would give
                    // the expected cash of the blind count. They show once the shift is closed.
                    var blind = !SaleAccess.SeesAll(user) && await cash.IsOpenAsync(s.ShiftId, ct);
                    return Results.Ok(new Response(s.Id, s.Number, s.ENcf, s.DocumentType, s.DateUtc, s.Status.ToString(), s.Register, s.Cashier, s.CustomerId,
                        s.CustomerName, s.Discount, s.Itbis, s.Total, blind ? 0 : s.Change, s.VoidReason, s.VoidENcf,
                        s.Lines.Select(l => new LineDto(l.Id, l.Description, l.Quantity, l.UnitPrice, l.Discount, l.Total, l.Itbis, l.ReturnedQuantity, l.Note, l.Unit)).ToList(),
                        blind ? [] : s.Payments.Select(p => new PaymentDto(p.Method, p.Amount, p.Received, p.Reference)).ToList(), returns));
                })
                .RequirePermission(Permissions.SalesCreate).WithTags("Sales");
    }
}

/// <summary>Sales of a shift (the register's list for reprints and voids), or of a date range for whoever may see reports.
/// A cashier without reports only sees their own sales.</summary>
internal static class ListSales
{
    public const int PageSize = 100;

    public sealed record Item(Guid Id, long Number, string ENcf, DateTime DateUtc, string Status, string Cashier, string? Customer, decimal Total);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/sales", async (Guid? shiftId, DateOnly? from, DateOnly? to, string? search, int? page, IUserContext user,
                    SalesDbContext db, CancellationToken ct) =>
                {
                    var q = db.Sales.AsNoTracking();
                    if (!SaleAccess.SeesAll(user)) q = q.Where(s => s.CashierId == user.UserId); // as the detail (R11 A13)
                    if (shiftId is { } sh) q = q.Where(s => s.ShiftId == sh);
                    if (from is not null || to is not null)
                    {
                        var (f, t) = BusinessDay.UtcRange(from ?? to!.Value, to ?? from!.Value);
                        q = q.Where(s => s.DateUtc >= f && s.DateUtc < t);
                    }
                    // TK-304: a number or an e-NCF, each by its index (Number.ToString() would read the whole table).
                    if (long.TryParse(search, System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite, null, out var number)) q = q.Where(s => s.Number == number);
                    else if (!string.IsNullOrWhiteSpace(search)) { var encf = search.Trim().ToUpperInvariant(); q = q.Where(s => s.ENcf == encf); }
                    var items = await q.OrderByDescending(s => s.Number).Skip((Math.Max(page ?? 1, 1) - 1) * PageSize).Take(PageSize)
                        .Select(s => new Item(s.Id, s.Number, s.ENcf, s.DateUtc, s.Status == SaleStatus.Voided ? "Voided" : "Completed", s.Cashier, s.CustomerName, s.Total))
                        .ToListAsync(ct);
                    return Results.Ok(items);
                })
                .RequirePermission(Permissions.SalesCreate).WithTags("Sales");
    }
}

/// <summary>Parks a cart to serve the next customer. The register sends its draft as JSON and gets it back later.</summary>
internal static class HeldSales
{
    public sealed record Save(Guid Id, Guid ShiftId, string Label, JsonElement Content, decimal Total) : ICommand<Ok>;
    public sealed record Remove(Guid Id, Guid ShiftId) : ICommand<Ok>;
    public sealed record Item(Guid Id, string Label, decimal Total, DateTime CreatedUtc, JsonElement Content);

    internal sealed class Validator : AbstractValidator<Save>
    {
        public Validator()
        {
            RuleFor(c => c.Id).NotEmpty();
            RuleFor(c => c.Label).NotEmpty().MaximumLength(60);
            RuleFor(c => c.Content.GetRawText()).MaximumLength(ModuleDbContext.LongText).WithMessage("La venta en espera es demasiado grande.");
        }
    }

    internal sealed class SaveHandler(SalesDbContext db, ICashRegisterOperations cash, IUserContext user, TimeProvider clock) : ICommandHandler<Save, Ok>
    {
        public async Task<Result<Ok>> Handle(Save c, CancellationToken ct)
        {
            var shift = await cash.GetOpenShiftAsync(c.ShiftId, ct);
            if (shift.IsFailure) return shift.Error!;
            // Only this shift's cart: an id from another register never overwrites theirs (it fails as a duplicate instead).
            var held = await db.HeldSales.FirstOrDefaultAsync(h => h.Id == c.Id && h.ShiftId == c.ShiftId, ct);
            if (held is null) db.HeldSales.Add(held = new HeldSale { Id = c.Id, CreatedUtc = clock.GetUtcNow().UtcDateTime });
            held.ShiftId = c.ShiftId;
            held.UserId = user.UserId;
            held.Label = c.Label.Trim();
            held.Content = c.Content.GetRawText();
            held.Total = c.Total;
            return Ok.Value;
        }
    }

    /// <summary>A closed shift's parked carts are no longer usable: they go when CashRegister publishes the closing.</summary>
    internal sealed class PurgeOnShiftClosed(SalesDbContext db) : IEventHandler<ShiftClosedV1>
    {
        public Task HandleAsync(ShiftClosedV1 @event, CancellationToken ct) => db.HeldSales.Where(h => h.ShiftId == @event.ShiftId).ExecuteDeleteAsync(ct);
    }

    internal sealed class RemoveHandler(SalesDbContext db, ICashRegisterOperations cash) : ICommandHandler<Remove, Ok>
    {
        public async Task<Result<Ok>> Handle(Remove c, CancellationToken ct)
        {
            var shift = await cash.GetOpenShiftAsync(c.ShiftId, ct);
            if (shift.IsFailure) return shift.Error!;
            await db.HeldSales.Where(h => h.Id == c.Id && h.ShiftId == c.ShiftId).ExecuteDeleteAsync(ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/sales/held", async (Save c, ICommandHandler<Save, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SalesCreate).WithTags("Sales");
            app.MapDelete("/api/sales/held/{id:guid}", async (Guid id, Guid shiftId, ICommandHandler<Remove, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Remove(id, shiftId), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SalesCreate).WithTags("Sales");
            app.MapGet("/api/sales/held", async (Guid shiftId, SalesDbContext db, IUserContext user, CancellationToken ct) =>
                {
                    // R11 A13: only one's own parked carts, unless one may operate other registers' shifts.
                    var q = db.HeldSales.Where(h => h.ShiftId == shiftId);
                    if (!user.Has(Permissions.RegisterApproveClose)) q = q.Where(h => h.UserId == user.UserId);
                    var items = await q.OrderBy(h => h.CreatedUtc).AsNoTracking().ToListAsync(ct);
                    return Results.Ok(items.Select(h => new Item(h.Id, h.Label, h.Total, h.CreatedUtc, JsonDocument.Parse(h.Content).RootElement)));
                })
                .RequirePermission(Permissions.SalesCreate).WithTags("Sales");
        }
    }
}
