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

internal static class ConteoErrors
{
    public static readonly Error NoEncontrado = Error.NotFound("Inventario.ConteoNoEncontrado", "El conteo no existe.");
    public static readonly Error NoAbierto = Error.Conflict("Inventario.ConteoCerrado", "El conteo ya fue aplicado.");
}

/// <summary>Opens a blind count of a store (optionally one category): one line per active product.</summary>
internal static class IniciarConteo
{
    public sealed record Command(Guid? AlmacenId, Guid? CategoriaId) : ICommand<Response>;
    public sealed record Response(Guid ConteoId, int Productos);

    internal sealed class Handler(InventarioDbContext db, IUserContext usuario, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var almacen = c.AlmacenId ?? Almacen.PrincipalId;
            if (!await db.Almacenes.AnyAsync(a => a.Id == almacen, ct)) return InventarioErrors.AlmacenNoEncontrado;
            var productos = await db.Productos.Where(p => p.Activo && (c.CategoriaId == null || p.CategoriaId == c.CategoriaId))
                .Select(p => p.Id).ToListAsync(ct);
            var conteo = new ConteoFisico
            {
                Id = Guid.CreateVersion7(), AlmacenId = almacen, CategoriaId = c.CategoriaId, Estado = EstadoConteo.Abierto,
                UsuarioId = usuario.UsuarioId, CreadoUtc = reloj.GetUtcNow().UtcDateTime,
            };
            conteo.Lineas = productos.Select(p => new ConteoLinea { ConteoId = conteo.Id, ProductoId = p }).ToList();
            db.Conteos.Add(conteo);
            return new Response(conteo.Id, productos.Count);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventario/conteos", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/inventario/conteos/{r.ConteoId}", r)))
                .RequirePermiso(Permisos.InventarioContar).WithTags("Inventario");
    }
}

/// <summary>Records counted quantities (can be sent many times while the count is open). Each line remembers the system stock
/// at that moment, so sales that happen before the count is applied are not erased.</summary>
internal static class RegistrarConteo
{
    public sealed record Linea(Guid ProductoId, decimal Contado);
    public sealed record Command(Guid ConteoId, IReadOnlyList<Linea> Lineas) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleForEach(c => c.Lineas).ChildRules(l => l.RuleFor(x => x.Contado).GreaterThanOrEqualTo(0).PrecisionScale(19, 3, true));
    }

    internal sealed class Handler(InventarioDbContext db) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var conteo = await db.Conteos.Include(x => x.Lineas).FirstOrDefaultAsync(x => x.Id == c.ConteoId, ct);
            if (conteo is null) return ConteoErrors.NoEncontrado;
            if (conteo.Estado != EstadoConteo.Abierto) return ConteoErrors.NoAbierto;

            var ids = c.Lineas.Select(l => l.ProductoId).Distinct().ToList();
            var existentes = await db.Productos.Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Unidad, p.Nombre }).ToListAsync(ct);
            if (existentes.Count != ids.Count) return InventarioErrors.ProductoNoEncontrado;
            var porUnidad = existentes.FirstOrDefault(p => p.Unidad == UnidadMedida.Unidad && c.Lineas.Any(l => l.ProductoId == p.Id && l.Contado != Math.Truncate(l.Contado)));
            if (porUnidad is not null) return Error.Validation("Inventario.CantidadInvalida", $"{porUnidad.Nombre} se cuenta por unidades enteras.");

            var sistema = await db.Existencias.Where(e => e.AlmacenId == conteo.AlmacenId && ids.Contains(e.ProductoId))
                .ToDictionaryAsync(e => e.ProductoId, e => e.Cantidad, ct);
            foreach (var l in c.Lineas)
            {
                var linea = conteo.Lineas.FirstOrDefault(x => x.ProductoId == l.ProductoId);
                if (linea is null) conteo.Lineas.Add(linea = new ConteoLinea { ConteoId = conteo.Id, ProductoId = l.ProductoId });
                linea.Contado = l.Contado;
                linea.SistemaAlContar = sistema.GetValueOrDefault(l.ProductoId);
            }
            return Ok.Valor;
        }
    }

    internal sealed record Body(IReadOnlyList<Linea> Lineas);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/inventario/conteos/{id:guid}/lineas", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Lineas), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.InventarioContar).WithTags("Inventario");
    }
}

/// <summary>Adds each counted line's difference (counted − stock when counted) to the stock and reports the differences.
/// Uncounted lines are skipped. Needs the adjust permission or a supervisor.</summary>
internal static class AplicarConteo
{
    public sealed record Command(Guid ConteoId, Guid? AutorizacionSupervisorId) : ICommand<Response>, IRequiereAutorizacion
    {
        public string Permiso => Permisos.InventarioAjustar;
        public Guid RecursoId => ConteoId;
    }

    public sealed record DiferenciaConteo(Guid ProductoId, string Producto, decimal Sistema, decimal Contado, decimal Diferencia, decimal Valor);
    public sealed record Response(IReadOnlyList<DiferenciaConteo> Diferencias, decimal ValorDiferencia);

    internal sealed class Handler(InventarioDbContext db, MovimientosInventario movimientos, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var ahora = reloj.GetUtcNow().UtcDateTime;
            // Claim it first: two applies of the same count cannot both run.
            var reclamado = await db.Conteos.Where(x => x.Id == c.ConteoId && x.Estado == EstadoConteo.Abierto)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Estado, EstadoConteo.Aplicado).SetProperty(x => x.AplicadoUtc, ahora), ct);
            if (reclamado == 0)
                return await db.Conteos.AnyAsync(x => x.Id == c.ConteoId, ct) ? ConteoErrors.NoAbierto : ConteoErrors.NoEncontrado;

            var conteo = await db.Conteos.AsNoTracking().SingleAsync(x => x.Id == c.ConteoId, ct);
            var contadas = await db.Set<ConteoLinea>().Where(l => l.ConteoId == c.ConteoId && l.Contado != null).AsNoTracking().ToListAsync(ct);
            var ids = contadas.Select(l => l.ProductoId).ToList();
            var productos = await db.Productos.Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Nombre, p.Costo }).ToDictionaryAsync(p => p.Id, ct);

            var diferencias = new List<DiferenciaConteo>();
            foreach (var l in contadas.OrderBy(l => l.ProductoId))
            {
                var p = productos[l.ProductoId];
                var dif = l.Contado!.Value - (l.SistemaAlContar ?? 0);
                await movimientos.AplicarDiferenciaAsync(TipoMovimiento.Conteo, conteo.Id, l.ProductoId, conteo.AlmacenId, dif, p.Costo, "Conteo físico", ct);
                if (dif != 0) diferencias.Add(new DiferenciaConteo(l.ProductoId, p.Nombre, l.SistemaAlContar ?? 0, l.Contado.Value, dif, Dinero.Redondear(dif * p.Costo)));
            }
            var total = diferencias.Sum(d => d.Valor);
            await auditoria.RegistrarAsync("inventario.aplicar-conteo", "Conteo", conteo.Id, new { Lineas = contadas.Count, Diferencias = diferencias.Count, Valor = total }, ct);
            return new Response(diferencias, total);
        }
    }

    internal sealed record Body(Guid? AutorizacionSupervisorId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventario/conteos/{id:guid}/aplicar", async (Guid id, Body? b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b?.AutorizacionSupervisorId), ct)).ToHttp())
                .RequirePermiso(Permisos.InventarioContar).WithTags("Inventario");
    }
}

/// <summary>Lines of a count without system quantities (blind).</summary>
internal static class ObtenerConteo
{
    public sealed record Linea(Guid ProductoId, string Producto, UnidadMedida Unidad, decimal? Contado);
    public sealed record Response(Guid Id, EstadoConteo Estado, Guid AlmacenId, IReadOnlyList<Linea> Lineas);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/inventario/conteos/{id:guid}", async (Guid id, InventarioDbContext db, CancellationToken ct) =>
                {
                    var conteo = await db.Conteos.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
                    if (conteo is null) return EndpointExtensions.Problema(ConteoErrors.NoEncontrado);
                    var lineas = await (from l in db.Set<ConteoLinea>()
                                        join p in db.Productos on l.ProductoId equals p.Id
                                        where l.ConteoId == id
                                        orderby p.Nombre
                                        select new Linea(p.Id, p.Nombre, p.Unidad, l.Contado)).AsNoTracking().ToListAsync(ct);
                    return Results.Ok(new Response(conteo.Id, conteo.Estado, conteo.AlmacenId, lineas));
                })
                .RequirePermiso(Permisos.InventarioContar).WithTags("Inventario");
    }
}
