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

internal enum AdjustmentReason { Shrinkage, Damage, InternalUse, Gift, Correction }

internal static class AdjustmentReasonNames
{
    /// <summary>The words the owner reads in the kardex, unchanged from before the code was in English.</summary>
    public static string Display(AdjustmentReason reason) => reason switch
    {
        AdjustmentReason.Shrinkage => "Merma",
        AdjustmentReason.Damage => "Danio",
        AdjustmentReason.InternalUse => "ConsumoInterno",
        AdjustmentReason.Gift => "Regalo",
        AdjustmentReason.Correction => "Correccion",
        _ => reason.ToString(),
    };
}

/// <summary>Adds or removes stock with a reason. Removing more than there is fails: count first.</summary>
internal static class AdjustStock
{
    public sealed record Command(Guid ProductId, Guid? WarehouseId, decimal Quantity, AdjustmentReason Reason, string? Note) : ICommand<Response>;
    public sealed record Response(Guid AdjustmentId);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Quantity).NotEqual(0).PrecisionScale(19, 3, true);
            RuleFor(c => c.Reason).IsInEnum();
            RuleFor(c => c.Note).MaximumLength(180);
        }
    }

    internal sealed class Handler(InventoryDbContext db, StockMovements movements, IAuditLog audit) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var warehouse = c.WarehouseId ?? Warehouse.MainId;
            if (!await db.Warehouses.AnyAsync(a => a.Id == warehouse, ct)) return InventoryErrors.WarehouseNotFound;
            var id = Guid.CreateVersion7();
            var line = new[] { new StockMovementLine(c.ProductId, Math.Abs(c.Quantity)) };
            var display = AdjustmentReasonNames.Display(c.Reason);
            var reason = string.IsNullOrWhiteSpace(c.Note) ? display : $"{display}: {c.Note.Trim()}";
            var r = c.Quantity > 0
                ? await movements.InboundAsync(StockMovementType.Adjustment, id, line, updateCost: false, reason, ct, warehouse)
                : await movements.OutboundAsync(StockMovementType.Adjustment, id, line, allowNegativeIfProductAllows: false, reason, ct, warehouse);
            if (r.IsFailure) return r.Error!;
            await audit.RecordAsync("inventory.adjustment", "Product", c.ProductId, c, ct);
            return new Response(id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventory/adjustments", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
                .RequirePermission(Permissions.InventoryAdjust).WithTags("Inventory");
    }
}

internal static class TransferStock
{
    public sealed record Line(Guid ProductId, decimal Quantity);
    public sealed record Command(Guid Source, Guid Destination, IReadOnlyList<Line> Lines) : ICommand<Response>;
    public sealed record Response(Guid TransferId);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Destination).NotEqual(c => c.Source).WithMessage("El destino debe ser otro almacén.");
            RuleFor(c => c.Lines).NotEmpty();
            RuleForEach(c => c.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).GreaterThan(0).PrecisionScale(19, 3, true));
        }
    }

    internal sealed class Handler(InventoryDbContext db, StockMovements movements, IAuditLog audit) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await db.Warehouses.CountAsync(a => (a.Id == c.Source || a.Id == c.Destination) && a.IsActive, ct) != 2) return InventoryErrors.WarehouseNotFound;
            var id = Guid.CreateVersion7();
            var lines = c.Lines.Select(l => new StockMovementLine(l.ProductId, l.Quantity)).ToList();
            var outbound = await movements.OutboundAsync(StockMovementType.TransferOut, id, lines, allowNegativeIfProductAllows: false, null, ct, c.Source);
            if (outbound.IsFailure) return outbound.Error!;
            var inbound = await movements.RecordInboundAsync(StockMovementType.TransferIn, id, lines, updateCost: false, ct, c.Destination);
            if (inbound.IsFailure) return inbound.Error!;
            await audit.RecordAsync("inventory.transfer", "Transfer", id, c, ct);
            return new Response(id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventory/transfers", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
                .RequirePermission(Permissions.InventoryTransfer).WithTags("Inventory");
    }
}
