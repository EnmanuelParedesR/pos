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
internal static class Codigos
{
    public sealed record Agregar(Guid ProductoId, string Codigo, Guid? PresentacionId) : ICommand<Ok>;
    public sealed record Quitar(Guid ProductoId, string Codigo) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Agregar>
    {
        public Validator() => RuleFor(c => c.Codigo).NotEmpty().MaximumLength(50);
    }

    internal sealed class AgregarHandler(InventarioDbContext db, IUserContext usuario, IAuditoria auditoria) : ICommandHandler<Agregar, Ok>
    {
        public async Task<Result<Ok>> Handle(Agregar c, CancellationToken ct)
        {
            if (!usuario.Tiene(Permisos.PreciosCambiar)) return InventarioErrors.SinPermisoPrecios;
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

    internal sealed class QuitarHandler(InventarioDbContext db, IUserContext usuario, IAuditoria auditoria) : ICommandHandler<Quitar, Ok>
    {
        public async Task<Result<Ok>> Handle(Quitar c, CancellationToken ct)
        {
            if (!usuario.Tiene(Permisos.PreciosCambiar)) return InventarioErrors.SinPermisoPrecios;
            if (await db.Codigos.Where(x => x.ProductoId == c.ProductoId && x.Codigo == c.Codigo).ExecuteDeleteAsync(ct) == 0)
                return InventarioErrors.CodigoNoEncontrado;
            await auditoria.RegistrarAsync("inventario.quitar-codigo", "Producto", c.ProductoId, c, ct);
            return Ok.Valor;
        }
    }

    internal sealed record Body(string Codigo, Guid? PresentacionId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/inventario/productos/{id:guid}/codigos", async (Guid id, Body b, ICommandHandler<Agregar, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Agregar(id, b.Codigo, b.PresentacionId), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
            app.MapDelete("/api/inventario/productos/{id:guid}/codigos/{codigo}", async (Guid id, string codigo, ICommandHandler<Quitar, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Quitar(id, codigo), ct)).ToHttp(_ => Results.NoContent()))
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
            RuleFor(c => c.Factor).GreaterThan(0).PrecisionScale(19, 3, true);
            RuleFor(c => c.Precio).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.PrecioMayorista).GreaterThan(0).PrecisionScale(19, 2, true).When(c => c.PrecioMayorista is not null);
        }
    }

    internal sealed class Handler(InventarioDbContext db, IUserContext usuario, IOutbox outbox, IAuditoria auditoria) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var producto = await db.Productos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == c.ProductoId, ct);
            if (producto is null) return InventarioErrors.ProductoNoEncontrado;
            if (!usuario.Tiene(Permisos.PreciosCambiar)) return InventarioErrors.SinPermisoPrecios;
            if (producto.Unidad == UnidadMedida.Unidad && c.Factor != Math.Truncate(c.Factor))
                return Error.Validation("Inventario.CantidadInvalida", "Un producto por unidad necesita un factor entero.");
            var pr = c.Id is { } id ? await db.Presentaciones.FirstOrDefaultAsync(x => x.Id == id && x.ProductoId == c.ProductoId, ct) : null;
            if (c.Id is not null && pr is null) return Error.NotFound("Inventario.PresentacionNoEncontrada", "La presentación no existe.");
            if (pr is null)
            {
                pr = new Presentacion { Id = Guid.CreateVersion7(), ProductoId = c.ProductoId };
                db.Presentaciones.Add(pr);
            }
            else if (pr.Precio != c.Precio || pr.PrecioMayorista != c.PrecioMayorista)
            {
                outbox.Agregar(new PrecioCambiadoV1(c.ProductoId, pr.Precio, c.Precio, pr.PrecioMayorista, c.PrecioMayorista));
            }
            var antes = new { pr.Nombre, pr.Factor, pr.Precio, pr.PrecioMayorista, pr.Activa };
            pr.Nombre = c.Nombre.Trim();
            pr.Factor = c.Factor;
            pr.Precio = c.Precio;
            pr.PrecioMayorista = c.PrecioMayorista;
            pr.Activa = c.Activa;
            await auditoria.RegistrarAsync("inventario.guardar-presentacion", "Producto", c.ProductoId, new { Presentacion = pr.Id, antes, despues = c }, ct);
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
    /// <param name="Existencia">Only for users who may see inventory (counts stay blind).</param>
    public sealed record Response(
        Guid ProductoId, Guid? PresentacionId, string Nombre, UnidadMedida Unidad, TasaItbis TasaItbis, decimal Precio, decimal? PrecioMayorista,
        decimal Factor, decimal? CantidadEtiqueta, decimal? MontoEtiqueta, IReadOnlyList<string> Notas, decimal? Existencia);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventario/productos/por-codigo/{codigo}", async (string codigo, IUserContext usuario, InventarioDbContext db, CancellationToken ct) =>
                {
                    codigo = codigo.Trim();
                    Guid? productoId = null, presentacionId = null;
                    decimal? cantidad = null, monto = null;

                    var barras = await db.Codigos.AsNoTracking().FirstOrDefaultAsync(x => x.Codigo == codigo, ct);
                    if (barras is not null) (productoId, presentacionId) = (barras.ProductoId, barras.PresentacionId);
                    else
                    {
                        var plu = Producto.NormalizarPlu(codigo);
                        var lectura = EtiquetaBalanza.Leer(codigo, await db.FormatosEtiqueta.AsNoTracking().ToListAsync(ct));
                        if (lectura is { IsFailure: true }) return EndpointExtensions.Problema(lectura.Error!);
                        if (lectura is { IsSuccess: true })
                        {
                            plu = Producto.NormalizarPlu(lectura.Value.Plu);
                            (cantidad, monto) = (lectura.Value.Peso, lectura.Value.Precio);
                        }
                        productoId = await db.Productos.AsNoTracking().Where(p => p.Plu == plu).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
                    }
                    if (productoId is null) return EndpointExtensions.Problema(InventarioErrors.CodigoNoEncontrado);

                    var p = await db.Productos.AsNoTracking().SingleAsync(x => x.Id == productoId, ct);
                    if (!p.Activo) return EndpointExtensions.Problema(Error.Conflict("Inventario.ProductoInactivo", $"{p.Nombre} está desactivado."));
                    if (cantidad is not null && p.Unidad != UnidadMedida.Libra)
                        return EndpointExtensions.Problema(Error.Validation("Inventario.EtiquetaNoAplica", $"{p.Nombre} se vende por unidad; esa etiqueta trae un peso."));
                    var notas = await db.Categorias.Where(c => c.Id == p.CategoriaId).Select(c => c.Notas).SingleAsync(ct);
                    decimal? existencia = usuario.Tiene(Permisos.InventarioVer)
                        ? await db.Existencias.Where(e => e.ProductoId == p.Id && e.AlmacenId == Almacen.PrincipalId).Select(e => e.Cantidad).FirstOrDefaultAsync(ct)
                        : null;

                    if (presentacionId is { } pid)
                    {
                        var pr = await db.Presentaciones.AsNoTracking().SingleAsync(x => x.Id == pid, ct);
                        if (!pr.Activa) return EndpointExtensions.Problema(Error.Conflict("Inventario.ProductoInactivo", $"{p.Nombre} ({pr.Nombre}) está desactivado."));
                        return Results.Ok(new Response(p.Id, pr.Id, $"{p.Nombre} ({pr.Nombre})", p.Unidad, p.TasaItbis, pr.Precio, pr.PrecioMayorista,
                            pr.Factor, null, null, notas, existencia));
                    }
                    return Results.Ok(new Response(p.Id, null, p.Nombre, p.Unidad, p.TasaItbis, p.Precio, p.PrecioMayorista, 1, cantidad, monto, notas, existencia));
                })
                .RequireAuthorization().WithTags("Inventario");
    }
}
