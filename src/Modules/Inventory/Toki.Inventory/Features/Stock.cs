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

internal static class Warehouses
{
    public sealed record Create(string Name) : ICommand<Response>;
    public sealed record Response(Guid Id);
    public sealed record Item(Guid Id, string Name, bool IsActive, bool IsMain);

    internal sealed class Validator : AbstractValidator<Create>
    {
        public Validator() => RuleFor(c => c.Name).NotEmpty().MaximumLength(60);
    }

    internal sealed class Handler(InventoryDbContext db, IAuditLog audit) : ICommandHandler<Create, Response>
    {
        public async Task<Result<Response>> Handle(Create c, CancellationToken ct)
        {
            var a = new Warehouse { Id = Guid.CreateVersion7(), Name = c.Name.Trim() };
            db.Warehouses.Add(a);
            var products = await db.Products.Select(p => p.Id).ToListAsync(ct);
            db.Stock.AddRange(products.Select(p => new Stock { ProductId = p, WarehouseId = a.Id }));
            await audit.RecordAsync("inventory.create-warehouse", "Warehouse", a.Id, c, ct);
            return new Response(a.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/inventory/warehouses", async (Create c, ICommandHandler<Create, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/inventory/warehouses/{r.Id}", r)))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
            app.MapGet("/api/inventory/warehouses", async (InventoryDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.Warehouses.OrderBy(a => a.Name)
                        .Select(a => new Item(a.Id, a.Name, a.IsActive, a.Id == Warehouse.MainId)).AsNoTracking().ToListAsync(ct)))
                .RequireAuthorization().WithTags("Inventory");
        }
    }
}

/// <summary>Stock per product with value at average cost (only with costs.view, TK-201) and flags for low stock and pending counts.</summary>
internal static class ListStock
{
    public sealed record Item(Guid ProductId, string Name, string Category, UnitOfMeasure Unit, decimal Quantity, decimal? Cost,
        decimal? Value, bool BelowMinimum, bool NeedsCount);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventory/stock", async (Guid? warehouseId, bool? belowMinimumOnly, bool? needsCountOnly, IUserContext user, InventoryDbContext db,
                CancellationToken ct) =>
                {
                    var warehouse = warehouseId ?? Warehouse.MainId;
                    // Filter before projecting: EF cannot translate a Where on the record's members (TK-178).
                    var q = from e in db.Stock
                            join p in db.Products on e.ProductId equals p.Id
                            join c in db.Categories on p.CategoryId equals c.Id
                            where e.WarehouseId == warehouse && p.IsActive
                                && (belowMinimumOnly != true || e.Quantity <= p.MinimumStock)
                                && (needsCountOnly != true || e.NeedsCount)
                            select new Item(p.Id, p.Name, c.Name, p.Unit, e.Quantity, p.Cost, e.Quantity * p.Cost,
                                e.Quantity <= p.MinimumStock, e.NeedsCount);
                    var items = await q.AsNoTracking().ToListAsync(ct);
                    var costs = user.Has(Permissions.CostsView);
                    return Results.Ok(items.OrderBy(i => i.Name).Select(i => costs ? i with { Value = Money.Round(i.Value!.Value) } : i with { Cost = null, Value = null }));
                })
                .RequirePermission(Permissions.InventoryView).WithTags("Inventory");
    }
}

internal static class ListStockMovements
{
    /// <param name="UnitCost">Null without costs.view (TK-201).</param>
    public sealed record Item(long Sequence, DateTime DateUtc, StockMovementType Type, Guid WarehouseId, decimal Quantity, decimal? UnitCost,
        decimal ResultingStock, Guid? ReferenceId, string? Reason);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventory/products/{id:guid}/movements", async (Guid id, int? last, IUserContext user, InventoryDbContext db, CancellationToken ct) =>
                {
                    var costs = user.Has(Permissions.CostsView);
                    return Results.Ok(await db.StockMovements.Where(m => m.ProductId == id).OrderByDescending(m => m.Sequence).Take(Math.Clamp(last ?? 100, 1, 1000))
                        .Select(m => new Item(m.Sequence, m.DateUtc, m.Type, m.WarehouseId, m.Quantity, costs ? m.UnitCost : null, m.ResultingStock, m.ReferenceId, m.Reason))
                        .AsNoTracking().ToListAsync(ct));
                })
                .RequirePermission(Permissions.InventoryView).WithTags("Inventory");
    }
}

/// <summary>Scale label formats (EAN-13 prefixes 20–29). Replaces the whole list.</summary>
internal static class ScaleLabelFormats
{
    public sealed record Format(string Prefix, int PluLength, bool IsPrice, int Decimals);
    public sealed record Command(IReadOnlyList<Format> Formats) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Formats).Must(f => f.Select(x => x.Prefix).Distinct().Count() == f.Count).WithMessage("Hay prefijos repetidos.");
            RuleForEach(c => c.Formats).ChildRules(f =>
            {
                f.RuleFor(x => x.Prefix).Matches("^2[0-9]$").WithMessage("El prefijo va de 20 a 29.");
                f.RuleFor(x => x.PluLength).InclusiveBetween(4, 6);
                f.RuleFor(x => x.Decimals).InclusiveBetween(0, 3);
            });
        }
    }

    internal sealed class Handler(InventoryDbContext db, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            await db.LabelFormats.ExecuteDeleteAsync(ct);
            db.LabelFormats.AddRange(c.Formats.Select(f => new LabelFormat { Prefix = f.Prefix, PluLength = f.PluLength, IsPrice = f.IsPrice, Decimals = f.Decimals }));
            await audit.RecordAsync("inventory.label-formats", "LabelFormat", Guid.Empty, c, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/inventory/label-formats", async (InventoryDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.LabelFormats.OrderBy(f => f.Prefix).Select(f => new Format(f.Prefix, f.PluLength, f.IsPrice, f.Decimals)).ToListAsync(ct)))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
            app.MapPut("/api/inventory/label-formats", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Inventory");
        }
    }
}
