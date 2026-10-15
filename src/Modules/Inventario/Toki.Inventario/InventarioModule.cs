using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Inventario.Contracts;
using Toki.Inventario.Domain;
using Toki.Inventario.Persistence;
using Toki.SharedKernel;

namespace Toki.Inventario;

public static class InventarioModule
{
    public static IServiceCollection AddInventarioModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<InventarioDbContext>();
        services.AddScoped<ICatalogoConsultas, CatalogoConsultas>();
        services.AddScoped<MovimientosInventario>();
        services.AddScoped<IMovimientosInventario>(sp => sp.GetRequiredService<MovimientosInventario>());
        return services;
    }
}

internal sealed class CatalogoConsultas(InventarioDbContext db) : ICatalogoConsultas
{
    public async Task<IReadOnlyList<ProductoParaVenta>> ObtenerParaVentaAsync(IReadOnlyCollection<(Guid ProductoId, Guid? PresentacionId)> items, CancellationToken ct)
    {
        var productoIds = items.Select(i => i.ProductoId).Distinct().ToList();
        var presentacionIds = items.Where(i => i.PresentacionId is not null).Select(i => i.PresentacionId!.Value).Distinct().ToList();

        var productos = await (from p in db.Productos
                               join c in db.Categorias on p.CategoriaId equals c.Id
                               where productoIds.Contains(p.Id)
                               select new { p, Categoria = c.Nombre }).AsNoTracking().ToDictionaryAsync(x => x.p.Id, ct);
        var presentaciones = await db.Presentaciones.Where(x => presentacionIds.Contains(x.Id)).AsNoTracking().ToDictionaryAsync(x => x.Id, ct);

        var resultado = new List<ProductoParaVenta>();
        foreach (var (productoId, presentacionId) in items.Distinct())
        {
            if (!productos.TryGetValue(productoId, out var x)) continue;
            var p = x.p;
            if (presentacionId is { } pid)
            {
                if (!presentaciones.TryGetValue(pid, out var pr) || pr.ProductoId != productoId) continue;
                resultado.Add(new ProductoParaVenta(p.Id, pr.Id, $"{p.Nombre} ({pr.Nombre})", p.CategoriaId, x.Categoria, p.Unidad, p.TasaItbis,
                    pr.Precio, pr.PrecioMayorista, pr.Factor, p.Costo, p.VenderSinExistencia, p.Activo && pr.Activa));
            }
            else
            {
                resultado.Add(new ProductoParaVenta(p.Id, null, p.Nombre, p.CategoriaId, x.Categoria, p.Unidad, p.TasaItbis,
                    p.Precio, p.PrecioMayorista, 1, p.Costo, p.VenderSinExistencia, p.Activo));
            }
        }
        return resultado;
    }
}

internal sealed class MovimientosInventario(InventarioDbContext db, IOutbox outbox, IUserContext usuario, TimeProvider reloj) : IMovimientosInventario
{
    public async Task<Result> RegistrarSalidaAsync(TipoMovimiento tipo, Guid referenciaId, IReadOnlyList<LineaMovimiento> lineas, CancellationToken ct, Guid? almacenId = null)
    {
        var almacen = almacenId ?? Almacen.PrincipalId;
        foreach (var linea in Agrupar(lineas))
        {
            var producto = await db.Productos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == linea.ProductoId, ct);
            if (producto is null) return InventarioErrors.ProductoNoEncontrado;
            await AsegurarExistenciaAsync(linea.ProductoId, almacen, ct);

            var q = linea.Cantidad;
            var permitir = producto.VenderSinExistencia;
            // Atomic conditional decrement: two registers selling the last pound cannot both succeed.
            var filas = await db.Existencias
                .Where(e => e.ProductoId == linea.ProductoId && e.AlmacenId == almacen && (permitir || e.Cantidad >= q))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Cantidad, e => e.Cantidad - q)
                    .SetProperty(e => e.PendienteContar, e => e.PendienteContar || e.Cantidad - q < 0), ct);
            var resultante = await CantidadAsync(linea.ProductoId, almacen, ct);
            if (filas == 0) return InventarioErrors.SinExistencia(producto.Nombre, resultante);

            Registrar(tipo, referenciaId, linea.ProductoId, almacen, -q, producto.Costo, resultante, null);
        }
        return Result.Success();
    }

    public async Task<Result> RegistrarEntradaAsync(TipoMovimiento tipo, Guid referenciaId, IReadOnlyList<LineaMovimiento> lineas, bool actualizarCosto, CancellationToken ct, Guid? almacenId = null)
    {
        var almacen = almacenId ?? Almacen.PrincipalId;
        foreach (var linea in lineas)
        {
            var producto = await db.Productos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == linea.ProductoId, ct);
            if (producto is null) return InventarioErrors.ProductoNoEncontrado;
            await AsegurarExistenciaAsync(linea.ProductoId, almacen, ct);

            var costo = producto.Costo;
            if (actualizarCosto && linea.CostoUnitario is { } costoEntrada)
            {
                var total = await db.Existencias.Where(e => e.ProductoId == linea.ProductoId).SumAsync(e => e.Cantidad, ct);
                costo = CostoPromedio.Calcular(total, producto.Costo, linea.Cantidad, costoEntrada);
                await db.Productos.Where(p => p.Id == linea.ProductoId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Costo, costo), ct);
            }

            var q = linea.Cantidad;
            await db.Existencias.Where(e => e.ProductoId == linea.ProductoId && e.AlmacenId == almacen)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Cantidad, e => e.Cantidad + q), ct);
            var resultante = await CantidadAsync(linea.ProductoId, almacen, ct);
            Registrar(tipo, referenciaId, linea.ProductoId, almacen, q, linea.CostoUnitario ?? costo, resultante, null);
        }
        return Result.Success();
    }

    /// <summary>Sets the counted quantity (physical count or manual adjustment to an absolute value).</summary>
    public async Task FijarAsync(TipoMovimiento tipo, Guid referenciaId, Guid productoId, Guid almacen, decimal contado, string? motivo, CancellationToken ct)
    {
        await AsegurarExistenciaAsync(productoId, almacen, ct);
        var antes = await CantidadAsync(productoId, almacen, ct);
        await db.Existencias.Where(e => e.ProductoId == productoId && e.AlmacenId == almacen)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Cantidad, contado).SetProperty(e => e.PendienteContar, false), ct);
        var costo = await db.Productos.Where(p => p.Id == productoId).Select(p => p.Costo).SingleAsync(ct);
        Registrar(tipo, referenciaId, productoId, almacen, contado - antes, costo, contado, motivo);
    }

    private void Registrar(TipoMovimiento tipo, Guid referenciaId, Guid productoId, Guid almacen, decimal cantidad, decimal costo, decimal resultante, string? motivo)
    {
        var m = new Movimiento
        {
            Id = Guid.CreateVersion7(), ProductoId = productoId, AlmacenId = almacen, Tipo = tipo, Cantidad = cantidad,
            CostoUnitario = costo, ExistenciaResultante = resultante, ReferenciaId = referenciaId, Motivo = motivo,
            UsuarioId = usuario.UsuarioId == Guid.Empty ? null : usuario.UsuarioId, FechaUtc = reloj.GetUtcNow().UtcDateTime,
        };
        db.Movimientos.Add(m);
        outbox.Agregar(new MovimientoInventarioRegistradoV1(m.Id, productoId, almacen, tipo, cantidad, costo, referenciaId));
    }

    private async Task AsegurarExistenciaAsync(Guid productoId, Guid almacen, CancellationToken ct)
    {
        if (await db.Existencias.AnyAsync(e => e.ProductoId == productoId && e.AlmacenId == almacen, ct)) return;
        db.Existencias.Add(new Existencia { ProductoId = productoId, AlmacenId = almacen });
        await db.SaveChangesAsync(ct);
    }

    private Task<decimal> CantidadAsync(Guid productoId, Guid almacen, CancellationToken ct) =>
        db.Existencias.Where(e => e.ProductoId == productoId && e.AlmacenId == almacen).Select(e => e.Cantidad).SingleAsync(ct);

    private static IEnumerable<LineaMovimiento> Agrupar(IReadOnlyList<LineaMovimiento> lineas) =>
        lineas.GroupBy(l => l.ProductoId).Select(g => new LineaMovimiento(g.Key, g.Sum(l => l.Cantidad)));
}
