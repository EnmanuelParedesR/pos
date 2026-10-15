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

internal static class Almacenes
{
    public sealed record Crear(string Nombre) : ICommand<Response>;
    public sealed record Response(Guid Id);
    public sealed record Item(Guid Id, string Nombre, bool Activo, bool Principal);

    internal sealed class Validator : AbstractValidator<Crear>
    {
        public Validator() => RuleFor(c => c.Nombre).NotEmpty().MaximumLength(60);
    }

    internal sealed class Handler(InventarioDbContext db, IAuditoria auditoria) : ICommandHandler<Crear, Response>
    {
        public async Task<Result<Response>> Handle(Crear c, CancellationToken ct)
        {
            var a = new Almacen { Id = Guid.CreateVersion7(), Nombre = c.Nombre.Trim() };
            db.Almacenes.Add(a);
            var productos = await db.Productos.Select(p => p.Id).ToListAsync(ct);
            db.Existencias.AddRange(productos.Select(p => new Existencia { ProductoId = p, AlmacenId = a.Id }));
            await auditoria.RegistrarAsync("inventario.crear-almacen", "Almacen", a.Id, c, ct);
            return new Response(a.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/inventario/almacenes", async (Crear c, ICommandHandler<Crear, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/inventario/almacenes/{r.Id}", r)))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
            app.MapGet("/api/inventario/almacenes", async (InventarioDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.Almacenes.OrderBy(a => a.Nombre)
                        .Select(a => new Item(a.Id, a.Nombre, a.Activo, a.Id == Almacen.PrincipalId)).AsNoTracking().ToListAsync(ct)))
                .RequireAuthorization().WithTags("Inventario");
        }
    }
}

/// <summary>Stock per product with value at average cost and flags for low stock and pending counts.</summary>
internal static class ListarExistencias
{
    public sealed record Item(Guid ProductoId, string Nombre, string Categoria, UnidadMedida Unidad, decimal Cantidad, decimal Costo,
        decimal Valor, bool BajoMinimo, bool PendienteContar);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventario/existencias", async (Guid? almacenId, bool? soloBajoMinimo, bool? soloPendientes, InventarioDbContext db, CancellationToken ct) =>
                {
                    var almacen = almacenId ?? Almacen.PrincipalId;
                    var q = from e in db.Existencias
                            join p in db.Productos on e.ProductoId equals p.Id
                            join c in db.Categorias on p.CategoriaId equals c.Id
                            where e.AlmacenId == almacen && p.Activo
                            select new Item(p.Id, p.Nombre, c.Nombre, p.Unidad, e.Cantidad, p.Costo, e.Cantidad * p.Costo,
                                e.Cantidad <= p.StockMinimo, e.PendienteContar);
                    if (soloBajoMinimo == true) q = q.Where(i => i.BajoMinimo);
                    if (soloPendientes == true) q = q.Where(i => i.PendienteContar);
                    var items = await q.AsNoTracking().ToListAsync(ct);
                    return Results.Ok(items.OrderBy(i => i.Nombre).Select(i => i with { Valor = Dinero.Redondear(i.Valor) }));
                })
                .RequirePermiso(Permisos.InventarioVer).WithTags("Inventario");
    }
}

internal static class Kardex
{
    public sealed record Item(long Secuencia, DateTime FechaUtc, TipoMovimiento Tipo, Guid AlmacenId, decimal Cantidad, decimal CostoUnitario,
        decimal ExistenciaResultante, Guid? ReferenciaId, string? Motivo);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventario/productos/{id:guid}/movimientos", async (Guid id, int? ultimos, InventarioDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.Movimientos.Where(m => m.ProductoId == id).OrderByDescending(m => m.Secuencia).Take(Math.Clamp(ultimos ?? 100, 1, 1000))
                        .Select(m => new Item(m.Secuencia, m.FechaUtc, m.Tipo, m.AlmacenId, m.Cantidad, m.CostoUnitario, m.ExistenciaResultante, m.ReferenciaId, m.Motivo))
                        .AsNoTracking().ToListAsync(ct)))
                .RequirePermiso(Permisos.InventarioVer).WithTags("Inventario");
    }
}

/// <summary>Scale label formats (EAN-13 prefixes 20–29). Replaces the whole list.</summary>
internal static class FormatosEtiquetaBalanza
{
    public sealed record Formato(string Prefijo, int LongitudPlu, bool EsPrecio, int Decimales);
    public sealed record Command(IReadOnlyList<Formato> Formatos) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Formatos).Must(f => f.Select(x => x.Prefijo).Distinct().Count() == f.Count).WithMessage("Hay prefijos repetidos.");
            RuleForEach(c => c.Formatos).ChildRules(f =>
            {
                f.RuleFor(x => x.Prefijo).Matches("^2[0-9]$").WithMessage("El prefijo va de 20 a 29.");
                f.RuleFor(x => x.LongitudPlu).InclusiveBetween(4, 6);
                f.RuleFor(x => x.Decimales).InclusiveBetween(0, 3);
            });
        }
    }

    internal sealed class Handler(InventarioDbContext db, IAuditoria auditoria) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            await db.FormatosEtiqueta.ExecuteDeleteAsync(ct);
            db.FormatosEtiqueta.AddRange(c.Formatos.Select(f => new FormatoEtiqueta { Prefijo = f.Prefijo, LongitudPlu = f.LongitudPlu, EsPrecio = f.EsPrecio, Decimales = f.Decimales }));
            await auditoria.RegistrarAsync("inventario.formatos-etiqueta", "FormatoEtiqueta", Guid.Empty, c, ct);
            return Ok.Valor;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/inventario/formatos-etiqueta", async (InventarioDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.FormatosEtiqueta.OrderBy(f => f.Prefijo).Select(f => new Formato(f.Prefijo, f.LongitudPlu, f.EsPrecio, f.Decimales)).ToListAsync(ct)))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
            app.MapPut("/api/inventario/formatos-etiqueta", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Inventario");
        }
    }
}
