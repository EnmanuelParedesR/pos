using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Inventario.Domain;
using Toki.Inventario.Persistence;
using Toki.SharedKernel;

namespace Toki.Inventario.Features;

/// <summary>Creates (Id null) or updates a category.</summary>
internal static class GuardarCategoria
{
    public sealed record Command(Guid? Id, string Nombre, string Color, IReadOnlyList<string>? Notas, bool Activa = true) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Nombre).NotEmpty().MaximumLength(80);
            RuleFor(c => c.Color).Matches("^#[0-9A-Fa-f]{6}$").WithMessage("El color debe ser #RRGGBB.");
            RuleForEach(c => c.Notas).NotEmpty().MaximumLength(60);
        }
    }

    internal sealed class Handler(InventarioDbContext db, IAuditoria auditoria) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var categoria = c.Id is { } id ? await db.Categorias.FirstOrDefaultAsync(x => x.Id == id, ct) : null;
            if (c.Id is not null && categoria is null) return InventarioErrors.CategoriaNoEncontrada;
            if (categoria is null)
            {
                categoria = new Categoria { Id = Guid.CreateVersion7() };
                db.Categorias.Add(categoria);
            }
            categoria.Nombre = c.Nombre.Trim();
            categoria.Color = c.Color.ToUpperInvariant();
            categoria.Notas = c.Notas?.Select(n => n.Trim()).Distinct().ToList() ?? [];
            categoria.Activa = c.Activa;
            await auditoria.RegistrarAsync("inventario.guardar-categoria", "Categoria", categoria.Id, new { categoria.Nombre, categoria.Activa }, ct);
            return new Response(categoria.Id);
        }
    }

    internal sealed record Body(string Nombre, string Color, IReadOnlyList<string>? Notas, bool Activa = true);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/inventario/categorias", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(null, b.Nombre, b.Color, b.Notas, b.Activa), ct)).ToHttp(r => Results.Created($"/api/inventario/categorias/{r.Id}", r)))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
            app.MapPut("/api/inventario/categorias/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Nombre, b.Color, b.Notas, b.Activa), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
        }
    }
}

internal static class ListarCategorias
{
    public sealed record Item(Guid Id, string Nombre, string Color, IReadOnlyList<string> Notas, bool Activa);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventario/categorias", async (InventarioDbContext db, CancellationToken ct) =>
                    Results.Ok((await db.Categorias.AsNoTracking().OrderBy(c => c.Nombre).ToListAsync(ct))
                        .Select(c => new Item(c.Id, c.Nombre, c.Color, c.Notas, c.Activa))))
                .RequireAuthorization().WithTags("Inventario");
    }
}
