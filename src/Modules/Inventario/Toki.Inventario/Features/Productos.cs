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

internal sealed class DatosProductoValidator : AbstractValidator<IDatosProducto>
{
    public DatosProductoValidator()
    {
        RuleFor(c => c.Nombre).NotEmpty().MaximumLength(150);
        RuleFor(c => c.Plu).Matches("^0*[1-9][0-9]{0,5}$").When(c => c.Plu is not null).WithMessage("El PLU son de 1 a 6 dígitos y no puede ser cero.");
        RuleFor(c => c.Precio).GreaterThan(0).PrecisionScale(19, 2, true);
        RuleFor(c => c.PrecioMayorista).GreaterThan(0).PrecisionScale(19, 2, true).When(c => c.PrecioMayorista is not null);
        RuleFor(c => c.StockMinimo).GreaterThanOrEqualTo(0).PrecisionScale(19, 3, true);
        RuleFor(c => c.Unidad).IsInEnum();
        RuleFor(c => c.TasaItbis).IsInEnum();
    }
}

internal interface IDatosProducto
{
    string Nombre { get; }
    Guid CategoriaId { get; }
    UnidadMedida Unidad { get; }
    string? Plu { get; }
    TasaItbis TasaItbis { get; }
    decimal Precio { get; }
    decimal? PrecioMayorista { get; }
    bool VenderSinExistencia { get; }
    decimal StockMinimo { get; }
}

internal static class CrearProducto
{
    /// <param name="Costo">Initial cost per base unit without ITBIS; purchases recompute it afterwards.</param>
    /// <param name="ExistenciaInicial">Optional opening stock in the main store, recorded as an adjustment.</param>
    public sealed record Command(
        string Nombre, Guid CategoriaId, UnidadMedida Unidad, string? Plu, TasaItbis TasaItbis, decimal Precio, decimal? PrecioMayorista,
        decimal Costo, bool VenderSinExistencia, decimal StockMinimo, IReadOnlyList<string>? Codigos, decimal ExistenciaInicial = 0)
        : ICommand<Response>, IDatosProducto;

    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            Include(new DatosProductoValidator());
            RuleFor(c => c.Costo).GreaterThanOrEqualTo(0).PrecisionScale(19, 4, true);
            RuleFor(c => c.ExistenciaInicial).GreaterThanOrEqualTo(0).PrecisionScale(19, 3, true);
            RuleForEach(c => c.Codigos).NotEmpty().MaximumLength(50);
        }
    }

    internal sealed class Handler(InventarioDbContext db, MovimientosInventario movimientos, IUserContext usuario, IAuditoria auditoria)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Setting a price on a new product is setting a price: same permission as editing one.
            if (!usuario.Tiene(Permisos.PreciosCambiar)) return InventarioErrors.SinPermisoPrecios;
            if (!await db.Categorias.AnyAsync(x => x.Id == c.CategoriaId, ct)) return InventarioErrors.CategoriaNoEncontrada;
            var plu = Producto.NormalizarPlu(c.Plu);
            if (plu is not null && await db.Productos.AnyAsync(p => p.Plu == plu, ct)) return InventarioErrors.PluDuplicado;
            var codigos = c.Codigos?.Select(x => x.Trim()).Distinct().ToList() ?? [];
            if (await db.Codigos.AnyAsync(x => codigos.Contains(x.Codigo), ct)) return InventarioErrors.CodigoDuplicado;

            var producto = new Producto
            {
                Id = Guid.CreateVersion7(), Nombre = c.Nombre.Trim(), CategoriaId = c.CategoriaId, Unidad = c.Unidad, Plu = plu,
                TasaItbis = c.TasaItbis, Precio = c.Precio, PrecioMayorista = c.PrecioMayorista, Costo = c.Costo,
                VenderSinExistencia = c.VenderSinExistencia, StockMinimo = c.StockMinimo,
            };
            db.Productos.Add(producto);
            db.Codigos.AddRange(codigos.Select(x => new CodigoBarras { Codigo = x, ProductoId = producto.Id }));
            // One stock row per warehouse up front: stock changes are then always a single relative update.
            var almacenes = await db.Almacenes.Select(a => a.Id).ToListAsync(ct);
            db.Existencias.AddRange(almacenes.Select(a => new Existencia { ProductoId = producto.Id, AlmacenId = a }));
            await db.SaveChangesAsync(ct);

            if (c.ExistenciaInicial > 0)
            {
                var r = await movimientos.EntradaAsync(TipoMovimiento.Ajuste, producto.Id, [new LineaMovimiento(producto.Id, c.ExistenciaInicial)],
                    actualizarCosto: false, "Existencia inicial", ct);
                if (r.IsFailure) return r.Error!;
            }

            await auditoria.RegistrarAsync("inventario.crear-producto", "Producto", producto.Id, c, ct);
            return new Response(producto.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventario/productos", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/inventario/productos/{r.Id}", r)))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
    }
}

/// <summary>Edits a product. Changing a price or the PLU (which decides what a scale label rings up) also needs the prices permission;
/// <see cref="Command.Version"/> detects concurrent edits.</summary>
internal static class ActualizarProducto
{
    public sealed record Command(
        Guid Id, int Version, string Nombre, Guid CategoriaId, UnidadMedida Unidad, string? Plu, TasaItbis TasaItbis,
        decimal Precio, decimal? PrecioMayorista, bool VenderSinExistencia, decimal StockMinimo, bool Activo)
        : ICommand<Ok>, IDatosProducto;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => Include(new DatosProductoValidator());
    }

    internal sealed class Handler(InventarioDbContext db, IUserContext usuario, IOutbox outbox, IAuditoria auditoria) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var p = await db.Productos.FirstOrDefaultAsync(x => x.Id == c.Id, ct);
            if (p is null) return InventarioErrors.ProductoNoEncontrado;
            if (p.Version != c.Version) return Error.Conflict("Concurrencia", "Otro usuario cambió este producto. Recargue e intente de nuevo.");
            if (!await db.Categorias.AnyAsync(x => x.Id == c.CategoriaId, ct)) return InventarioErrors.CategoriaNoEncontrada;
            var plu = Producto.NormalizarPlu(c.Plu);
            if (plu is not null && await db.Productos.AnyAsync(x => x.Plu == plu && x.Id != c.Id, ct)) return InventarioErrors.PluDuplicado;

            var cambiaPrecio = p.Precio != c.Precio || p.PrecioMayorista != c.PrecioMayorista;
            if ((cambiaPrecio || p.Plu != plu || p.TasaItbis != c.TasaItbis) && !usuario.Tiene(Permisos.PreciosCambiar)) return InventarioErrors.SinPermisoPrecios;
            if (cambiaPrecio) outbox.Agregar(new PrecioCambiadoV1(p.Id, p.Precio, c.Precio, p.PrecioMayorista, c.PrecioMayorista));

            var antes = new { p.Nombre, p.Precio, p.PrecioMayorista, p.TasaItbis, p.Plu, p.Activo };
            p.Nombre = c.Nombre.Trim();
            p.CategoriaId = c.CategoriaId;
            p.Unidad = c.Unidad;
            p.Plu = plu;
            p.TasaItbis = c.TasaItbis;
            p.Precio = c.Precio;
            p.PrecioMayorista = c.PrecioMayorista;
            p.VenderSinExistencia = c.VenderSinExistencia;
            p.StockMinimo = c.StockMinimo;
            p.Activo = c.Activo;
            p.Version++;
            await auditoria.RegistrarAsync("inventario.actualizar-producto", "Producto", p.Id, new { antes, despues = c }, ct);
            return Ok.Valor;
        }
    }

    internal sealed record Body(
        int Version, string Nombre, Guid CategoriaId, UnidadMedida Unidad, string? Plu, TasaItbis TasaItbis,
        decimal Precio, decimal? PrecioMayorista, bool VenderSinExistencia, decimal StockMinimo, bool Activo);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/inventario/productos/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Version, b.Nombre, b.CategoriaId, b.Unidad, b.Plu, b.TasaItbis, b.Precio, b.PrecioMayorista,
                        b.VenderSinExistencia, b.StockMinimo, b.Activo), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ProductosGestionar).WithTags("Inventario");
    }
}

internal static class ObtenerProducto
{
    public sealed record PresentacionDto(Guid Id, string Nombre, decimal Factor, decimal Precio, decimal? PrecioMayorista, bool Activa);
    public sealed record CodigoDto(string Codigo, Guid? PresentacionId);
    public sealed record ExistenciaDto(Guid AlmacenId, string Almacen, decimal Cantidad, bool PendienteContar);
    public sealed record Response(
        Guid Id, int Version, string Nombre, Guid CategoriaId, UnidadMedida Unidad, string? Plu, TasaItbis TasaItbis, decimal Precio,
        decimal? PrecioMayorista, decimal Costo, bool VenderSinExistencia, decimal StockMinimo, bool Activo,
        IReadOnlyList<PresentacionDto> Presentaciones, IReadOnlyList<CodigoDto> Codigos, IReadOnlyList<ExistenciaDto> Existencias);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventario/productos/{id:guid}", async (Guid id, InventarioDbContext db, CancellationToken ct) =>
                {
                    var p = await db.Productos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (p is null) return EndpointExtensions.Problema(InventarioErrors.ProductoNoEncontrado);
                    var presentaciones = await db.Presentaciones.Where(x => x.ProductoId == id).OrderBy(x => x.Factor)
                        .Select(x => new PresentacionDto(x.Id, x.Nombre, x.Factor, x.Precio, x.PrecioMayorista, x.Activa)).ToListAsync(ct);
                    var codigos = await db.Codigos.Where(x => x.ProductoId == id).Select(x => new CodigoDto(x.Codigo, x.PresentacionId)).ToListAsync(ct);
                    var existencias = await (from e in db.Existencias
                                             join a in db.Almacenes on e.AlmacenId equals a.Id
                                             where e.ProductoId == id
                                             select new ExistenciaDto(a.Id, a.Nombre, e.Cantidad, e.PendienteContar)).ToListAsync(ct);
                    return Results.Ok(new Response(p.Id, p.Version, p.Nombre, p.CategoriaId, p.Unidad, p.Plu, p.TasaItbis, p.Precio, p.PrecioMayorista,
                        p.Costo, p.VenderSinExistencia, p.StockMinimo, p.Activo, presentaciones, codigos, existencias));
                })
                .RequirePermiso(Permisos.InventarioVer).WithTags("Inventario");
    }
}

/// <summary>Product search for the register and back office. Stock is only shown to users who may see inventory (counts stay blind).</summary>
internal static class BuscarProductos
{
    public const int TamanoPagina = 50;

    public sealed record Item(Guid Id, string Nombre, string Categoria, UnidadMedida Unidad, TasaItbis TasaItbis, decimal Precio,
        decimal? PrecioMayorista, decimal? Existencia, bool Activo);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventario/productos", async (string? buscar, Guid? categoriaId, bool? incluirInactivos, int? pagina,
                    IUserContext usuario, InventarioDbContext db, CancellationToken ct) =>
                {
                    var q = from p in db.Productos
                            join c in db.Categorias on p.CategoriaId equals c.Id
                            join e in db.Existencias.Where(e => e.AlmacenId == Almacen.PrincipalId) on p.Id equals e.ProductoId into ex
                            from e in ex.DefaultIfEmpty()
                            select new { p, Categoria = c.Nombre, Existencia = e == null ? 0 : e.Cantidad };
                    if (incluirInactivos != true) q = q.Where(x => x.p.Activo);
                    if (categoriaId is { } cid) q = q.Where(x => x.p.CategoriaId == cid);
                    if (!string.IsNullOrWhiteSpace(buscar))
                    {
                        var patron = $"%{Escapar(buscar.Trim())}%";
                        var plu = Producto.NormalizarPlu(buscar);
                        q = q.Where(x => EF.Functions.Like(x.p.Nombre, patron, "\\") || x.p.Plu == plu);
                    }
                    var verExistencia = usuario.Tiene(Permisos.InventarioVer);
                    var items = await q.OrderBy(x => x.p.Nombre).Skip((Math.Max(pagina ?? 1, 1) - 1) * TamanoPagina).Take(TamanoPagina)
                        .Select(x => new Item(x.p.Id, x.p.Nombre, x.Categoria, x.p.Unidad, x.p.TasaItbis, x.p.Precio, x.p.PrecioMayorista,
                            verExistencia ? x.Existencia : null, x.p.Activo))
                        .AsNoTracking().ToListAsync(ct);
                    return Results.Ok(items);
                })
                .RequireAuthorization().WithTags("Inventario");
    }

    private static string Escapar(string texto) => texto.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
