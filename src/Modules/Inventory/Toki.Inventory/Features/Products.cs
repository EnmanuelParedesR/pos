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

internal sealed class ProductDataValidator : AbstractValidator<IProductData>
{
    public ProductDataValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(150);
        RuleFor(c => c.Plu).Matches("^0*[1-9][0-9]{0,5}$").When(c => c.Plu is not null).WithMessage("El PLU son de 1 a 6 dígitos y no puede ser cero.");
        RuleFor(c => c.Price).GreaterThan(0).PrecisionScale(19, 2, true);
        RuleFor(c => c.WholesalePrice).GreaterThan(0).PrecisionScale(19, 2, true).When(c => c.WholesalePrice is not null);
        RuleFor(c => c.MinimumStock).GreaterThanOrEqualTo(0).PrecisionScale(19, 3, true);
        RuleFor(c => c.MaximumStock).GreaterThanOrEqualTo(0).PrecisionScale(19, 3, true)
            .Must((c, max) => max is null or 0m || max >= c.MinimumStock).WithMessage("El máximo no puede ser menor que el mínimo (0 si no lo usa).");
        RuleFor(c => c.Unit).IsInEnum();
        RuleFor(c => c.ItbisRate).IsInEnum();
    }
}

internal interface IProductData
{
    string Name { get; }
    Guid CategoryId { get; }
    UnitOfMeasure Unit { get; }
    string? Plu { get; }
    ItbisRate ItbisRate { get; }
    decimal Price { get; }
    decimal? WholesalePrice { get; }
    bool SellWithoutStock { get; }
    decimal MinimumStock { get; }
    /// <summary>Null on an update keeps the saved one (R8 #22: a client that does not send it no longer erases it).</summary>
    decimal? MaximumStock { get; }
}

internal static class CreateProduct
{
    /// <param name="Cost">Initial cost per base unit without ITBIS; purchases recompute it afterwards.</param>
    /// <param name="InitialStock">Optional opening stock in the main store, recorded as an adjustment.</param>
    public sealed record Command(
        string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate, decimal Price, decimal? WholesalePrice,
        decimal Cost, bool SellWithoutStock, decimal MinimumStock, IReadOnlyList<string>? Barcodes, decimal InitialStock = 0, decimal? MaximumStock = 0)
        : ICommand<Response>, IProductData;

    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            Include(new ProductDataValidator());
            RuleFor(c => c.Cost).GreaterThanOrEqualTo(0).PrecisionScale(19, 4, true);
            RuleFor(c => c.InitialStock).GreaterThanOrEqualTo(0).PrecisionScale(19, 3, true);
            RuleForEach(c => c.Barcodes).NotEmpty().MaximumLength(50);
        }
    }

    internal sealed class Handler(InventoryDbContext db, StockMovements movements, IUserContext user, IAuditLog audit)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Setting a price on a new product is setting a price: same permission as editing one.
            if (!user.Has(Permissions.PricesChange)) return InventoryErrors.NoPricePermission;
            if (!await db.Categories.AnyAsync(x => x.Id == c.CategoryId, ct)) return InventoryErrors.CategoryNotFound;
            var plu = Product.NormalizePlu(c.Plu);
            if (plu is not null && await db.Products.AnyAsync(p => p.Plu == plu, ct)) return InventoryErrors.DuplicatePlu;
            var codes = c.Barcodes?.Select(x => x.Trim()).Distinct().ToList() ?? [];
            if (await db.Barcodes.AnyAsync(x => codes.Contains(x.Code), ct)) return InventoryErrors.DuplicateBarcode;

            var product = new Product
            {
                Id = Guid.CreateVersion7(), Name = c.Name.Trim(), CategoryId = c.CategoryId, Unit = c.Unit, Plu = plu,
                ItbisRate = c.ItbisRate, Price = c.Price, WholesalePrice = c.WholesalePrice, Cost = c.Cost,
                SellWithoutStock = c.SellWithoutStock, MinimumStock = c.MinimumStock, MaximumStock = c.MaximumStock ?? 0,
            };
            db.Products.Add(product);
            db.Barcodes.AddRange(codes.Select(x => new Barcode { Code = x, ProductId = product.Id }));
            // One stock row per warehouse up front: stock changes are then always a single relative update.
            var warehouses = await db.Warehouses.Select(a => a.Id).ToListAsync(ct);
            db.Stock.AddRange(warehouses.Select(a => new Stock { ProductId = product.Id, WarehouseId = a }));
            await db.SaveChangesAsync(ct);

            if (c.InitialStock > 0)
            {
                var r = await movements.InboundAsync(StockMovementType.Adjustment, product.Id, [new StockMovementLine(product.Id, c.InitialStock)],
                    updateCost: false, "Existencia inicial", ct);
                if (r.IsFailure) return r.Error!;
            }

            await audit.RecordAsync("inventory.create-product", "Product", product.Id, c, ct);
            return new Response(product.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventory/products", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/inventory/products/{r.Id}", r)))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
    }
}

/// <summary>Edits a product. Changing a price or the PLU (which decides what a scale label rings up) also needs the prices permission;
/// <see cref="Command.Version"/> detects concurrent edits.</summary>
internal static class UpdateProduct
{
    public sealed record Command(
        Guid Id, int Version, string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate,
        decimal Price, decimal? WholesalePrice, bool SellWithoutStock, decimal MinimumStock, bool IsActive, decimal? MaximumStock = null)
        : ICommand<Ok>, IProductData;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => Include(new ProductDataValidator());
    }

    internal sealed class Handler(InventoryDbContext db, IUserContext user, IOutbox outbox, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
            if (p is null) return InventoryErrors.ProductNotFound;
            if (p.Version != c.Version) return Error.Conflict("Concurrency", "Otro usuario cambió este producto. Recargue e intente de nuevo.");
            if (!await db.Categories.AnyAsync(x => x.Id == c.CategoryId, ct)) return InventoryErrors.CategoryNotFound;
            var plu = Product.NormalizePlu(c.Plu);
            if (plu is not null && await db.Products.AnyAsync(x => x.Plu == plu && x.Id != c.Id, ct)) return InventoryErrors.DuplicatePlu;

            var maximum = c.MaximumStock ?? p.MaximumStock;
            if (maximum != 0 && maximum < c.MinimumStock) return Error.Validation("Validation", "El máximo no puede ser menor que el mínimo (0 si no lo usa).");

            var priceChanges = p.Price != c.Price || p.WholesalePrice != c.WholesalePrice;
            if ((priceChanges || p.Plu != plu || p.ItbisRate != c.ItbisRate) && !user.Has(Permissions.PricesChange)) return InventoryErrors.NoPricePermission;
            if (priceChanges) outbox.Add(new PriceChangedV1(p.Id, p.Price, c.Price, p.WholesalePrice, c.WholesalePrice));
            if (p.Name != c.Name.Trim() || p.CategoryId != c.CategoryId || p.Unit != c.Unit || p.IsActive != c.IsActive)
                outbox.Add(new ProductChangedV1(p.Id, c.Name.Trim(), c.IsActive));

            var before = new { p.Name, p.Price, p.WholesalePrice, p.ItbisRate, p.Plu, p.IsActive };
            p.Name = c.Name.Trim();
            p.CategoryId = c.CategoryId;
            p.Unit = c.Unit;
            p.Plu = plu;
            p.ItbisRate = c.ItbisRate;
            p.Price = c.Price;
            p.WholesalePrice = c.WholesalePrice;
            p.SellWithoutStock = c.SellWithoutStock;
            p.MinimumStock = c.MinimumStock;
            p.MaximumStock = maximum;
            p.IsActive = c.IsActive;
            p.Version++;
            await audit.RecordAsync("inventory.update-product", "Product", p.Id, new { before, after = c }, ct);
            return Ok.Value;
        }
    }

    internal sealed record Body(
        int Version, string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate,
        decimal Price, decimal? WholesalePrice, bool SellWithoutStock, decimal MinimumStock, bool IsActive, decimal? MaximumStock = null);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/inventory/products/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Version, b.Name, b.CategoryId, b.Unit, b.Plu, b.ItbisRate, b.Price, b.WholesalePrice,
                        b.SellWithoutStock, b.MinimumStock, b.IsActive, b.MaximumStock), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
    }
}

internal static class GetProduct
{
    public sealed record PresentationDto(Guid Id, string Name, decimal Factor, decimal Price, decimal? WholesalePrice, bool IsActive);
    public sealed record BarcodeDto(string Code, Guid? PresentationId);
    public sealed record StockDto(Guid WarehouseId, string Warehouse, decimal Quantity, bool NeedsCount);
    public sealed record Response(
        Guid Id, int Version, string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate, decimal Price,
        decimal? WholesalePrice, decimal? Cost, bool SellWithoutStock, decimal MinimumStock, decimal MaximumStock, bool IsActive,
        IReadOnlyList<PresentationDto> Presentations, IReadOnlyList<BarcodeDto> Barcodes, IReadOnlyList<StockDto> Stock);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventory/products/{id:guid}", async (Guid id, IUserContext user, InventoryDbContext db, CancellationToken ct) =>
                {
                    var p = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (p is null) return EndpointExtensions.Problem(InventoryErrors.ProductNotFound);
                    var presentations = await db.Presentations.Where(x => x.ProductId == id).OrderBy(x => x.Factor)
                        .Select(x => new PresentationDto(x.Id, x.Name, x.Factor, x.Price, x.WholesalePrice, x.IsActive)).ToListAsync(ct);
                    var barcodes = await db.Barcodes.Where(x => x.ProductId == id).Select(x => new BarcodeDto(x.Code, x.PresentationId)).ToListAsync(ct);
                    var stock = await (from e in db.Stock
                                       join a in db.Warehouses on e.WarehouseId equals a.Id
                                       where e.ProductId == id
                                       select new StockDto(a.Id, a.Name, e.Quantity, e.NeedsCount)).ToListAsync(ct);
                    return Results.Ok(new Response(p.Id, p.Version, p.Name, p.CategoryId, p.Unit, p.Plu, p.ItbisRate, p.Price, p.WholesalePrice,
                        user.Has(Permissions.CostsView) ? p.Cost : null, p.SellWithoutStock, p.MinimumStock, p.MaximumStock, p.IsActive, presentations, barcodes, stock));
                })
                .RequirePermission(Permissions.InventoryView).WithTags("Inventory");
    }
}

/// <summary>Product search for the register and back office. Stock is only shown to users who may see inventory (counts stay blind).</summary>
internal static class SearchProducts
{
    public const int PageSize = 50;

    public sealed record Item(Guid Id, string Name, string Category, UnitOfMeasure Unit, ItbisRate ItbisRate, decimal Price,
        decimal? WholesalePrice, decimal? Stock, bool IsActive);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventory/products", async (string? search, Guid? categoryId, bool? includeInactive, int? page,
                    IUserContext user, InventoryDbContext db, CancellationToken ct) =>
                {
                    var q = from p in db.Products
                            join c in db.Categories on p.CategoryId equals c.Id
                            join e in db.Stock.Where(e => e.WarehouseId == Warehouse.MainId) on p.Id equals e.ProductId into ex
                            from e in ex.DefaultIfEmpty()
                            select new { p, Category = c.Name, Stock = e == null ? 0 : e.Quantity };
                    if (includeInactive != true) q = q.Where(x => x.p.IsActive);
                    if (categoryId is { } cid) q = q.Where(x => x.p.CategoryId == cid);
                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        var pattern = $"%{Escape(search.Trim())}%";
                        var plu = Product.NormalizePlu(search);
                        q = q.Where(x => EF.Functions.Like(x.p.Name, pattern, "\\") || x.p.Plu == plu);
                    }
                    var canSeeStock = user.Has(Permissions.InventoryView);
                    var items = await q.OrderBy(x => x.p.Name).Skip((Math.Max(page ?? 1, 1) - 1) * PageSize).Take(PageSize)
                        .Select(x => new Item(x.p.Id, x.p.Name, x.Category, x.p.Unit, x.p.ItbisRate, x.p.Price, x.p.WholesalePrice,
                            canSeeStock ? x.Stock : null, x.p.IsActive))
                        .AsNoTracking().ToListAsync(ct);
                    return Results.Ok(items);
                })
                .RequireAuthorization().WithTags("Inventory");
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
