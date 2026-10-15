using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Inventory.Contracts;
using Toki.Inventory.Domain;
using Toki.Inventory.Persistence;
using Toki.SharedKernel;

namespace Toki.Inventory.Features;

internal static class StockCountErrors
{
    public static readonly Error NotFound = Error.NotFound("Inventory.StockCountNotFound", "El conteo no existe.");
    public static readonly Error NotOpen = Error.Conflict("Inventory.StockCountClosed", "El conteo ya fue aplicado.");
}

/// <summary>Opens a blind count of a store (optionally one category): one line per active product.</summary>
internal static class StartStockCount
{
    public sealed record Command(Guid? WarehouseId, Guid? CategoryId) : ICommand<Response>;
    public sealed record Response(Guid StockCountId, int ProductCount);

    internal sealed class Handler(InventoryDbContext db, IUserContext user, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var warehouse = c.WarehouseId ?? Warehouse.MainId;
            if (!await db.Warehouses.AnyAsync(a => a.Id == warehouse, ct)) return InventoryErrors.WarehouseNotFound;
            var products = await db.Products.Where(p => p.IsActive && (c.CategoryId == null || p.CategoryId == c.CategoryId))
                .Select(p => p.Id).ToListAsync(ct);
            var count = new StockCount
            {
                Id = Guid.CreateVersion7(), WarehouseId = warehouse, CategoryId = c.CategoryId, Status = StockCountStatus.Open,
                UserId = user.UserId, CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            count.Lines = products.Select(p => new StockCountLine { StockCountId = count.Id, ProductId = p }).ToList();
            db.StockCounts.Add(count);
            return new Response(count.Id, products.Count);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventory/stock-counts", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/inventory/stock-counts/{r.StockCountId}", r)))
                .RequirePermission(Permissions.InventoryCount).WithTags("Inventory");
    }
}

/// <summary>Records counted quantities (can be sent many times while the count is open). Each line remembers the system stock
/// at that moment, so sales that happen before the count is applied are not erased.</summary>
internal static class RecordStockCount
{
    public sealed record Line(Guid ProductId, decimal Counted);
    public sealed record Command(Guid StockCountId, IReadOnlyList<Line> Lines) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleForEach(c => c.Lines).ChildRules(l => l.RuleFor(x => x.Counted).GreaterThanOrEqualTo(0).PrecisionScale(19, 3, true));
    }

    internal sealed class Handler(InventoryDbContext db) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var count = await db.StockCounts.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == c.StockCountId, ct);
            if (count is null) return StockCountErrors.NotFound;
            if (count.Status != StockCountStatus.Open) return StockCountErrors.NotOpen;

            var ids = c.Lines.Select(l => l.ProductId).Distinct().ToList();
            var existing = await db.Products.Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Unit, p.Name }).ToListAsync(ct);
            if (existing.Count != ids.Count) return InventoryErrors.ProductNotFound;
            var byUnit = existing.FirstOrDefault(p => p.Unit == UnitOfMeasure.Unit && c.Lines.Any(l => l.ProductId == p.Id && l.Counted != Math.Truncate(l.Counted)));
            if (byUnit is not null) return Error.Validation("Inventory.InvalidQuantity", $"{byUnit.Name} se cuenta por unidades enteras.");

            var system = await db.Stock.Where(e => e.WarehouseId == count.WarehouseId && ids.Contains(e.ProductId))
                .ToDictionaryAsync(e => e.ProductId, e => e.Quantity, ct);
            foreach (var l in c.Lines)
            {
                var line = count.Lines.FirstOrDefault(x => x.ProductId == l.ProductId);
                if (line is null) count.Lines.Add(line = new StockCountLine { StockCountId = count.Id, ProductId = l.ProductId });
                line.Counted = l.Counted;
                line.SystemAtCount = system.GetValueOrDefault(l.ProductId);
            }
            return Ok.Value;
        }
    }

    internal sealed record Body(IReadOnlyList<Line> Lines);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/inventory/stock-counts/{id:guid}/lines", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Lines), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.InventoryCount).WithTags("Inventory");
    }
}

/// <summary>Adds each counted line's difference (counted − stock when counted) to the stock and reports the differences.
/// Uncounted lines are skipped. Needs the adjust permission or a supervisor.</summary>
internal static class ApplyStockCount
{
    public sealed record Command(Guid StockCountId, Guid? SupervisorApprovalId) : ICommand<Response>, IRequiresApproval
    {
        public string Permission => Permissions.InventoryAdjust;
        public Guid ResourceId => StockCountId;
    }

    public sealed record CountDifference(Guid ProductId, string Product, decimal SystemStock, decimal Counted, decimal Difference, decimal Value);
    public sealed record Response(IReadOnlyList<CountDifference> Differences, decimal DifferenceValue);

    internal sealed class Handler(InventoryDbContext db, StockMovements movements, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            // Claim it first: two applies of the same count cannot both run.
            var claimed = await db.StockCounts.Where(x => x.Id == c.StockCountId && x.Status == StockCountStatus.Open)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StockCountStatus.Applied).SetProperty(x => x.AppliedUtc, now), ct);
            if (claimed == 0)
                return await db.StockCounts.AnyAsync(x => x.Id == c.StockCountId, ct) ? StockCountErrors.NotOpen : StockCountErrors.NotFound;

            var count = await db.StockCounts.AsNoTracking().SingleAsync(x => x.Id == c.StockCountId, ct);
            var counted = await db.Set<StockCountLine>().Where(l => l.StockCountId == c.StockCountId && l.Counted != null).AsNoTracking().ToListAsync(ct);
            var ids = counted.Select(l => l.ProductId).ToList();
            var products = await db.Products.Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Cost }).ToDictionaryAsync(p => p.Id, ct);

            var differences = new List<CountDifference>();
            foreach (var l in counted.OrderBy(l => l.ProductId))
            {
                var p = products[l.ProductId];
                var diff = l.Counted!.Value - (l.SystemAtCount ?? 0);
                await movements.ApplyDifferenceAsync(StockMovementType.Count, count.Id, l.ProductId, count.WarehouseId, diff, p.Cost, "Conteo físico", ct);
                if (diff != 0) differences.Add(new CountDifference(l.ProductId, p.Name, l.SystemAtCount ?? 0, l.Counted.Value, diff, Money.Round(diff * p.Cost)));
            }
            var total = differences.Sum(d => d.Value);
            await audit.RecordAsync("inventory.apply-stock-count", "StockCount", count.Id, new { Lines = counted.Count, Differences = differences.Count, Value = total }, ct);
            return new Response(differences, total);
        }
    }

    internal sealed record Body(Guid? SupervisorApprovalId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventory/stock-counts/{id:guid}/apply", async (Guid id, Body? b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b?.SupervisorApprovalId), ct)).ToHttp())
                .RequirePermission(Permissions.InventoryCount).WithTags("Inventory");
    }
}

/// <summary>Lines of a count without system quantities (blind).</summary>
internal static class GetStockCount
{
    public sealed record Line(Guid ProductId, string Product, UnitOfMeasure Unit, decimal? Counted);
    public sealed record Response(Guid Id, StockCountStatus Status, Guid WarehouseId, IReadOnlyList<Line> Lines);
    /// <summary>A count in the list, so an open one can be picked up again (TK-184).</summary>
    public sealed record Item(Guid Id, StockCountStatus Status, Guid WarehouseId, Guid? CategoryId, DateTime CreatedUtc, DateTime? AppliedUtc,
        int LineCount, int CountedCount);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/inventory/stock-counts", async (InventoryDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.StockCounts.OrderByDescending(c => c.CreatedUtc).Take(20)
                        .Select(c => new Item(c.Id, c.Status, c.WarehouseId, c.CategoryId, c.CreatedUtc, c.AppliedUtc, c.Lines.Count, c.Lines.Count(l => l.Counted != null)))
                        .AsNoTracking().ToListAsync(ct)))
                .RequirePermission(Permissions.InventoryCount).WithTags("Inventory");
            app.MapGet("/api/inventory/stock-counts/{id:guid}", async (Guid id, InventoryDbContext db, CancellationToken ct) =>
                {
                    var count = await db.StockCounts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
                    if (count is null) return EndpointExtensions.Problem(StockCountErrors.NotFound);
                    var lines = await (from l in db.Set<StockCountLine>()
                                       join p in db.Products on l.ProductId equals p.Id
                                       where l.StockCountId == id
                                       orderby p.Name
                                       select new Line(p.Id, p.Name, p.Unit, l.Counted)).AsNoTracking().ToListAsync(ct);
                    return Results.Ok(new Response(count.Id, count.Status, count.WarehouseId, lines));
                })
                .RequirePermission(Permissions.InventoryCount).WithTags("Inventory");
        }
    }
}
