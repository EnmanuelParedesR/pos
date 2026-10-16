using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Inventory.Domain;
using Abastalia.Inventory.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Inventory.Features;

/// <summary>Creates (Id null) or updates a category.</summary>
internal static class SaveCategory
{
    public sealed record Command(Guid? Id, string Name, string Color, IReadOnlyList<string>? Notes, bool IsActive = true) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(80);
            RuleFor(c => c.Color).Matches("^#[0-9A-Fa-f]{6}$").WithMessage("El color debe ser #RRGGBB.");
            RuleFor(c => c.Notes).Must(n => n is null || n.Count <= 20).WithMessage("Máximo 20 notas por categoría.");
            RuleForEach(c => c.Notes).NotEmpty().MaximumLength(60).Must(n => !n.Contains('\n', StringComparison.Ordinal)).WithMessage("Una nota no puede tener saltos de línea.");
        }
    }

    internal sealed class Handler(InventoryDbContext db, IAuditLog audit) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var category = c.Id is { } id ? await db.Categories.FirstOrDefaultAsync(x => x.Id == id, ct) : null;
            if (c.Id is not null && category is null) return InventoryErrors.CategoryNotFound;
            if (category is null)
            {
                category = new Category { Id = Guid.CreateVersion7() };
                db.Categories.Add(category);
            }
            category.Name = c.Name.Trim();
            category.Color = c.Color.ToUpperInvariant();
            category.Notes = c.Notes?.Select(n => n.Trim()).Distinct().ToList() ?? [];
            category.IsActive = c.IsActive;
            await audit.RecordAsync("inventory.save-category", "Category", category.Id, new { category.Name, category.IsActive }, ct);
            return new Response(category.Id);
        }
    }

    internal sealed record Body(string Name, string Color, IReadOnlyList<string>? Notes, bool IsActive = true);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/inventory/categories", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(null, b.Name, b.Color, b.Notes, b.IsActive), ct)).ToHttp(r => Results.Created($"/api/inventory/categories/{r.Id}", r)))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
            app.MapPut("/api/inventory/categories/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Name, b.Color, b.Notes, b.IsActive), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
        }
    }
}

internal static class ListCategories
{
    public sealed record Item(Guid Id, string Name, string Color, IReadOnlyList<string> Notes, bool IsActive);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventory/categories", async (InventoryDbContext db, CancellationToken ct) =>
                    Results.Ok((await db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
                        .Select(c => new Item(c.Id, c.Name, c.Color, c.Notes, c.IsActive))))
                .RequireAuthorization().WithTags("Inventory");
    }
}
