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

/// <summary>Adds or removes a barcode. A barcode decides what a scan rings up, so it needs the prices permission too.</summary>
internal static class Barcodes
{
    public sealed record Add(Guid ProductId, string Code, Guid? PresentationId) : ICommand<Ok>;
    public sealed record Remove(Guid ProductId, string Code) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Add>
    {
        public Validator() => RuleFor(c => c.Code).NotEmpty().MaximumLength(50);
    }

    internal sealed class AddHandler(InventoryDbContext db, IUserContext user, IAuditLog audit) : ICommandHandler<Add, Ok>
    {
        public async Task<Result<Ok>> Handle(Add c, CancellationToken ct)
        {
            if (!user.Has(Permissions.PricesChange)) return InventoryErrors.NoPricePermission;
            if (!await db.Products.AnyAsync(p => p.Id == c.ProductId, ct)) return InventoryErrors.ProductNotFound;
            if (c.PresentationId is { } pid && !await db.Presentations.AnyAsync(p => p.Id == pid && p.ProductId == c.ProductId, ct))
                return Error.NotFound("Inventory.PresentationNotFound", "La presentación no existe.");
            var code = c.Code.Trim();
            if (await db.Barcodes.AnyAsync(x => x.Code == code, ct)) return InventoryErrors.DuplicateBarcode;
            db.Barcodes.Add(new Barcode { Code = code, ProductId = c.ProductId, PresentationId = c.PresentationId });
            await audit.RecordAsync("inventory.add-barcode", "Product", c.ProductId, c, ct);
            return Ok.Value;
        }
    }

    internal sealed class RemoveHandler(InventoryDbContext db, IUserContext user, IAuditLog audit) : ICommandHandler<Remove, Ok>
    {
        public async Task<Result<Ok>> Handle(Remove c, CancellationToken ct)
        {
            if (!user.Has(Permissions.PricesChange)) return InventoryErrors.NoPricePermission;
            if (await db.Barcodes.Where(x => x.ProductId == c.ProductId && x.Code == c.Code).ExecuteDeleteAsync(ct) == 0)
                return InventoryErrors.CodeNotFound;
            await audit.RecordAsync("inventory.remove-barcode", "Product", c.ProductId, c, ct);
            return Ok.Value;
        }
    }

    internal sealed record Body(string Code, Guid? PresentationId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/inventory/products/{id:guid}/barcodes", async (Guid id, Body b, ICommandHandler<Add, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Add(id, b.Code, b.PresentationId), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
            app.MapDelete("/api/inventory/products/{id:guid}/barcodes/{code}", async (Guid id, string code, ICommandHandler<Remove, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Remove(id, code), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
        }
    }
}

/// <summary>Creates (Id null) or updates a presentation (box, pack…) with its own price.</summary>
internal static class SavePresentation
{
    public sealed record Command(Guid ProductId, Guid? Id, string Name, decimal Factor, decimal Price, decimal? WholesalePrice, bool IsActive = true) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(60);
            RuleFor(c => c.Factor).GreaterThan(0).PrecisionScale(19, 3, true);
            RuleFor(c => c.Price).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.WholesalePrice).GreaterThan(0).PrecisionScale(19, 2, true).When(c => c.WholesalePrice is not null);
        }
    }

    internal sealed class Handler(InventoryDbContext db, IUserContext user, IOutbox outbox, IAuditLog audit) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == c.ProductId, ct);
            if (product is null) return InventoryErrors.ProductNotFound;
            if (!user.Has(Permissions.PricesChange)) return InventoryErrors.NoPricePermission;
            if (product.Unit == UnitOfMeasure.Unit && c.Factor != Math.Truncate(c.Factor))
                return Error.Validation("Inventory.InvalidQuantity", "Un producto por unidad necesita un factor entero.");
            var pr = c.Id is { } id ? await db.Presentations.FirstOrDefaultAsync(x => x.Id == id && x.ProductId == c.ProductId, ct) : null;
            if (c.Id is not null && pr is null) return Error.NotFound("Inventory.PresentationNotFound", "La presentación no existe.");
            if (pr is null)
            {
                pr = new Presentation { Id = Guid.CreateVersion7(), ProductId = c.ProductId };
                db.Presentations.Add(pr);
            }
            else if (pr.Price != c.Price || pr.WholesalePrice != c.WholesalePrice)
            {
                outbox.Add(new PriceChangedV1(c.ProductId, pr.Price, c.Price, pr.WholesalePrice, c.WholesalePrice));
            }
            var before = new { pr.Name, pr.Factor, pr.Price, pr.WholesalePrice, pr.IsActive };
            pr.Name = c.Name.Trim();
            pr.Factor = c.Factor;
            pr.Price = c.Price;
            pr.WholesalePrice = c.WholesalePrice;
            pr.IsActive = c.IsActive;
            await audit.RecordAsync("inventory.save-presentation", "Product", c.ProductId, new { Presentation = pr.Id, before, after = c }, ct);
            return new Response(pr.Id);
        }
    }

    internal sealed record Body(string Name, decimal Factor, decimal Price, decimal? WholesalePrice, bool IsActive = true);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/inventory/products/{id:guid}/presentations", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, null, b.Name, b.Factor, b.Price, b.WholesalePrice, b.IsActive), ct)).ToHttp(r => Results.Created($"/api/inventory/products/{id}", r)))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
            app.MapPut("/api/inventory/products/{id:guid}/presentations/{presentationId:guid}", async (Guid id, Guid presentationId, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, presentationId, b.Name, b.Factor, b.Price, b.WholesalePrice, b.IsActive), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
        }
    }
}

/// <summary>What the register calls on every scan: barcode, PLU, or a scale label with weight or price inside.</summary>
internal static class FindByCode
{
    /// <param name="LabelQuantity">Pounds read from a weight label.</param>
    /// <param name="LabelAmount">Amount read from a price label (the line total is fixed).</param>
    /// <param name="Stock">Only for users who may see inventory (counts stay blind).</param>
    public sealed record Response(
        Guid ProductId, Guid? PresentationId, string Name, UnitOfMeasure Unit, ItbisRate ItbisRate, decimal Price, decimal? WholesalePrice,
        decimal Factor, decimal? LabelQuantity, decimal? LabelAmount, IReadOnlyList<string> Notes, decimal? Stock);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventory/products/by-code/{code}", async (string code, IUserContext user, InventoryDbContext db, CancellationToken ct) =>
                {
                    code = code.Trim();
                    Guid? productId = null, presentationId = null;
                    decimal? quantity = null, amount = null;

                    var barcode = await db.Barcodes.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code, ct);
                    if (barcode is not null) (productId, presentationId) = (barcode.ProductId, barcode.PresentationId);
                    else
                    {
                        var plu = Product.NormalizePlu(code);
                        var parsed = ScaleLabel.Read(code, await db.LabelFormats.AsNoTracking().ToListAsync(ct));
                        if (parsed is { IsFailure: true }) return EndpointExtensions.Problem(parsed.Error!);
                        if (parsed is { IsSuccess: true })
                        {
                            plu = Product.NormalizePlu(parsed.Value.Plu);
                            (quantity, amount) = (parsed.Value.Weight, parsed.Value.Price);
                        }
                        productId = await db.Products.AsNoTracking().Where(p => p.Plu == plu).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
                    }
                    if (productId is null) return EndpointExtensions.Problem(InventoryErrors.CodeNotFound);

                    var p = await db.Products.AsNoTracking().SingleAsync(x => x.Id == productId, ct);
                    if (!p.IsActive) return EndpointExtensions.Problem(Error.Conflict("Inventory.ProductInactive", $"{p.Name} está desactivado."));
                    if (quantity is not null && p.Unit != UnitOfMeasure.Pound)
                        return EndpointExtensions.Problem(Error.Validation("Inventory.LabelNotApplicable", $"{p.Name} se vende por unidad; esa etiqueta trae un peso."));
                    var notes = await db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Notes).SingleAsync(ct);
                    decimal? stock = user.Has(Permissions.InventoryView)
                        ? await db.Stock.Where(e => e.ProductId == p.Id && e.WarehouseId == Warehouse.MainId).Select(e => e.Quantity).FirstOrDefaultAsync(ct)
                        : null;

                    if (presentationId is { } pid)
                    {
                        var pr = await db.Presentations.AsNoTracking().SingleAsync(x => x.Id == pid, ct);
                        if (!pr.IsActive) return EndpointExtensions.Problem(Error.Conflict("Inventory.ProductInactive", $"{p.Name} ({pr.Name}) está desactivado."));
                        return Results.Ok(new Response(p.Id, pr.Id, $"{p.Name} ({pr.Name})", p.Unit, p.ItbisRate, pr.Price, pr.WholesalePrice,
                            pr.Factor, null, null, notes, stock));
                    }
                    return Results.Ok(new Response(p.Id, null, p.Name, p.Unit, p.ItbisRate, p.Price, p.WholesalePrice, 1, quantity, amount, notes, stock));
                })
                .RequireAuthorization().WithTags("Inventory");
    }
}

/// <summary>Current prices of the products in a cart, so the register can re-price its lines after the server answers Sales.TotalChanged.
/// Only sales.create: the product detail needs inventory.view (a role may not have it) and shows the cost.</summary>
internal static class PricesForSale
{
    /// <param name="PresentationId">Null for the product itself; each active presentation comes as its own row.</param>
    public sealed record Item(Guid ProductId, Guid? PresentationId, decimal Price, decimal? WholesalePrice);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventory/products/prices", async (Guid[] ids, InventoryDbContext db, CancellationToken ct) =>
                {
                    if (ids.Length is 0 or > 300) return EndpointExtensions.Problem(Error.Validation("Inventory.InvalidIds", "Entre 1 y 300 productos."));
                    var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id))
                        .Select(p => new Item(p.Id, null, p.Price, p.WholesalePrice)).ToListAsync(ct);
                    var presentations = await db.Presentations.AsNoTracking().Where(p => ids.Contains(p.ProductId) && p.IsActive)
                        .Select(p => new Item(p.ProductId, p.Id, p.Price, p.WholesalePrice)).ToListAsync(ct);
                    return Results.Ok(products.Concat(presentations));
                })
                .RequirePermission(Permissions.SalesCreate).WithTags("Inventory");
    }
}
