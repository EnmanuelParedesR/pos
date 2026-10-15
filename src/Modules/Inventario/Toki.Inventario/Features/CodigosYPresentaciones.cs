using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Inventario.Contracts;
using Toki.Inventario.Domain;
using Toki.Inventario.Persistence;
using Toki.SharedKernel;

namespace Toki.Inventario.Features;

internal static class AgregarCodigo
{
    public sealed record Command(Guid ProductoId, string Codigo, Guid? PresentacionId) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Codigo).NotEmpty().MaximumLength(50);
    }

    internal sealed class Handler(InventarioDbContext db, IAuditoria auditoria) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            if (!await db.Productos.AnyAsync(p => p.Id == c.ProductoId, ct)) return InventarioErrors.ProductoNoEncontrado;
            if (c.PresentacionId is { } pid && !await db.Presentaciones.AnyAsync(p => p.Id == pid && p.ProductoId == c.ProductoId, ct))
                return Error.NotFound("Inventario.PresentacionNoEncontrada", "La presentación no existe.");
            var codigo = c.Codigo.Trim();
            if (await db.Codigos.AnyAsync(x => x.Codigo == codigo, ct)) return InventarioErrors.CodigoDuplicado;
            db.Codigos.Add(new CodigoBarras { Codigo = codigo, ProductoId = c.ProductoId, PresentacionId = c.PresentacionId });
            await auditoria.RegistrarAsync("inventario.agregar-codigo", "Producto", c.ProductoId, c, ct);
            return Ok.Valor;
        }
    }

    internal sealed record Body(string Codigo, Guid? PresentacionId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/inventario/productos/{id:guid}/codigos", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Codigo, b.PresentacionId), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
            app.MapDelete("/api/inventario/productos/{id:guid}/codigos/{codigo}", async (Guid id, string codigo, InventarioDbContext db, CancellationToken ct) =>
                {
                    await db.Codigos.Where(x => x.ProductoId == id && x.Codigo == codigo).ExecuteDeleteAsync(ct);
                    return Results.NoContent();
                })
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
        }
    }
}

/// <summary>Creates (Id null) or updates a presentation (box, pack…) with its own price.</summary>
internal static class GuardarPresentacion
{
    public sealed record Command(Guid ProductoId, Guid? Id, string Nombre, decimal Factor, decimal Precio, decimal? PrecioMayorista, bool Activa = true) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Nombre).NotEmpty().MaximumLength(60);
            RuleFor(c => c.Factor).GreaterThan(0);
            RuleFor(c => c.Precio).GreaterThan(0);
            RuleFor(c => c.PrecioMayorista).GreaterThan(0).When(c => c.PrecioMayorista is not null);
        }
    }

    internal sealed class Handler(InventarioDbContext db, IUserContext usuario, IAuditoria auditoria) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (!await db.Productos.AnyAsync(p => p.Id == c.ProductoId, ct)) return InventarioErrors.ProductoNoEncontrado;
            if (!usuario.Tiene(Permisos.PreciosCambiar)) return InventarioErrors.SinPermisoPrecios;
            var pr = c.Id is { } id ? await db.Presentaciones.FirstOrDefaultAsync(x => x.Id == id && x.ProductoId == c.ProductoId, ct) : null;
            if (c.Id is not null && pr is null) return Error.NotFound("Inventario.PresentacionNoEncontrada", "La presentación no existe.");
            if (pr is null)
            {
                pr = new Presentacion { Id = Guid.CreateVersion7(), ProductoId = c.ProductoId };
                db.Presentaciones.Add(pr);
            }
            pr.Nombre = c.Nombre.Trim();
            pr.Factor = c.Factor;
            pr.Precio = c.Precio;
            pr.PrecioMayorista = c.PrecioMayorista;
            pr.Activa = c.Activa;
            await auditoria.RegistrarAsync("inventario.guardar-presentacion", "Producto", c.ProductoId, c, ct);
            return new Response(pr.Id);
        }
    }

    internal sealed record Body(string Nombre, decimal Factor, decimal Precio, decimal? PrecioMayorista, bool Activa = true);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/inventario/productos/{id:guid}/presentaciones", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, null, b.Nombre, b.Factor, b.Precio, b.PrecioMayorista, b.Activa), ct)).ToHttp(r => Results.Created($"/api/inventario/productos/{id}", r)))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
            app.MapPut("/api/inventario/productos/{id:guid}/presentaciones/{presentacionId:guid}", async (Guid id, Guid presentacionId, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, presentacionId, b.Nombre, b.Factor, b.Precio, b.PrecioMayorista, b.Activa), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
        }
    }
}

/// <summary>What the register calls on every scan: barcode, PLU, or a scale label with weight or price inside.</summary>
internal static class BuscarPorCodigo
{
    /// <param name="CantidadEtiqueta">Pounds read from a weight label.</param>
    /// <param name="MontoEtiqueta">Amount read from a price label (the line total is fixed).</param>
    public sealed record Response(
        Guid ProductoId, Guid? PresentacionId, string Nombre, UnidadMedida Unidad, TasaItbis TasaItbis, decimal Precio, decimal? PrecioMayorista,
        decimal Factor, decimal? CantidadEtiqueta, decimal? MontoEtiqueta, IReadOnlyList<string> Notas, decimal Existencia);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventario/productos/por-codigo/{codigo}", async (string codigo, InventarioDbContext db, CancellationToken ct) =>
                {
                    codigo = codigo.Trim();
                    Guid? productoId = null, presentacionId = null;
                    decimal? cantidad = null, monto = null;

                    var barras = await db.Codigos.AsNoTracking().FirstOrDefaultAsync(x => x.Codigo == codigo, ct);
                    if (barras is not null) (productoId, presentacionId) = (barras.ProductoId, barras.PresentacionId);
                    else
                    {
                        string plu = codigo, pluSinCeros = codigo;
                        var lectura = EtiquetaBalanza.Leer(codigo, await db.FormatosEtiqueta.AsNoTracking().ToListAsync(ct));
                        if (lectura is { IsFailure: true }) return EndpointExtensions.Problema(lectura.Error!);
                        if (lectura is { IsSuccess: true })
                        {
                            (plu, pluSinCeros) = (lectura.Value.Plu, lectura.Value.Plu.TrimStart('0'));
                            (cantidad, monto) = (lectura.Value.Peso, lectura.Value.Precio);
                        }
                        productoId = await db.Productos.AsNoTracking().Where(p => p.Plu == plu || p.Plu == pluSinCeros)
                            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
                    }
                    if (productoId is null) return EndpointExtensions.Problema(InventarioErrors.CodigoNoEncontrado);

                    var p = await db.Productos.AsNoTracking().SingleAsync(x => x.Id == productoId, ct);
                    if (!p.Activo) return EndpointExtensions.Problema(Error.Conflict("Inventario.ProductoInactivo", $"{p.Nombre} está desactivado."));
                    var notas = await db.Categorias.Where(c => c.Id == p.CategoriaId).Select(c => c.Notas).SingleAsync(ct);
                    var existencia = await db.Existencias.Where(e => e.ProductoId == p.Id && e.AlmacenId == Almacen.PrincipalId).Select(e => e.Cantidad).FirstOrDefaultAsync(ct);

                    if (presentacionId is { } pid)
                    {
                        var pr = await db.Presentaciones.AsNoTracking().SingleAsync(x => x.Id == pid, ct);
                        return Results.Ok(new Response(p.Id, pr.Id, $"{p.Nombre} ({pr.Nombre})", p.Unidad, p.TasaItbis, pr.Precio, pr.PrecioMayorista,
                            pr.Factor, null, null, notas, existencia));
                    }
                    return Results.Ok(new Response(p.Id, null, p.Nombre, p.Unidad, p.TasaItbis, p.Precio, p.PrecioMayorista, 1, cantidad, monto, notas, existencia));
                })
                .RequireAuthorization().WithTags("Inventario");
    }
}
