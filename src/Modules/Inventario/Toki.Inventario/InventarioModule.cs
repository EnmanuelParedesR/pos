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
        services.AddScoped<IInventarioReportes, InventarioReportes>();
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

    public async Task<Result<EtiquetaLeida>> LeerEtiquetaAsync(string codigo, CancellationToken ct)
    {
        var lectura = EtiquetaBalanza.Leer(codigo.Trim(), await db.FormatosEtiqueta.AsNoTracking().ToListAsync(ct));
        if (lectura is null) return Error.Validation("Inventario.NoEsEtiqueta", "Ese código no es una etiqueta de balanza.");
        if (lectura.IsFailure) return lectura.Error!;
        var plu = Producto.NormalizarPlu(lectura.Value.Plu);
        var productoId = await db.Productos.AsNoTracking().Where(p => p.Plu == plu).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (productoId is null) return InventarioErrors.CodigoNoEncontrado;
        return new EtiquetaLeida(productoId.Value, lectura.Value.Peso, lectura.Value.Precio);
    }
}

/// <summary>Every stock change goes through here: atomic relative updates on the stock row plus a kardex row.
/// Rows are always touched in ProductoId order so concurrent commands lock them in the same order (no deadlocks).</summary>
internal sealed class MovimientosInventario(InventarioDbContext db, IOutbox outbox, IUserContext usuario, TimeProvider reloj) : IMovimientosInventario
{
    public Task<Result> RegistrarSalidaAsync(TipoMovimiento tipo, Guid referenciaId, IReadOnlyList<LineaMovimiento> lineas, CancellationToken ct, Guid? almacenId = null,
        bool soloConExistencia = false) =>
        SalidaAsync(tipo, referenciaId, lineas, permitirNegativoSiProductoLoPermite: !soloConExistencia, motivo: null, ct, almacenId);

    /// <param name="permitirNegativoSiProductoLoPermite">Only sales honour "sell without stock"; adjustments and transfers never go below zero.</param>
    public async Task<Result> SalidaAsync(TipoMovimiento tipo, Guid referenciaId, IReadOnlyList<LineaMovimiento> lineas,
        bool permitirNegativoSiProductoLoPermite, string? motivo, CancellationToken ct, Guid? almacenId = null)
    {
        var almacen = almacenId ?? Almacen.PrincipalId;
        foreach (var linea in Agrupar(lineas))
        {
            var producto = await db.Productos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == linea.ProductoId, ct);
            if (producto is null) return InventarioErrors.ProductoNoEncontrado;
            if (ValidarCantidad(producto, linea.Cantidad) is { } error) return error;

            var q = linea.Cantidad;
            var permitir = permitirNegativoSiProductoLoPermite && producto.VenderSinExistencia;
            // Atomic conditional decrement: two registers selling the last pound cannot both succeed.
            var filas = await db.Existencias
                .Where(e => e.ProductoId == linea.ProductoId && e.AlmacenId == almacen && (permitir || e.Cantidad >= q))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Cantidad, e => e.Cantidad - q)
                    .SetProperty(e => e.PendienteContar, e => e.PendienteContar || e.Cantidad - q < 0), ct);
            var resultante = await CantidadAsync(linea.ProductoId, almacen, ct);
            if (filas == 0) return InventarioErrors.SinExistencia(producto.Nombre, resultante);

            Registrar(tipo, referenciaId, linea.ProductoId, almacen, -q, producto.Costo, resultante, motivo);
        }
        return Result.Success();
    }

    public Task<Result> RegistrarEntradaAsync(TipoMovimiento tipo, Guid referenciaId, IReadOnlyList<LineaMovimiento> lineas, bool actualizarCosto, CancellationToken ct, Guid? almacenId = null) =>
        EntradaAsync(tipo, referenciaId, lineas, actualizarCosto, motivo: null, ct, almacenId);

    public async Task<Result> EntradaAsync(TipoMovimiento tipo, Guid referenciaId, IReadOnlyList<LineaMovimiento> lineas, bool actualizarCosto,
        string? motivo, CancellationToken ct, Guid? almacenId = null)
    {
        var almacen = almacenId ?? Almacen.PrincipalId;
        foreach (var linea in lineas.OrderBy(l => l.ProductoId))
        {
            var producto = await db.Productos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == linea.ProductoId, ct);
            if (producto is null) return InventarioErrors.ProductoNoEncontrado;
            if (ValidarCantidad(producto, linea.Cantidad) is { } error) return error;

            // Update the stock row first: it takes the row lock before the cost is read, so two purchases cannot lose a cost update.
            var q = linea.Cantidad;
            var filas = await db.Existencias.Where(e => e.ProductoId == linea.ProductoId && e.AlmacenId == almacen)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Cantidad, e => e.Cantidad + q), ct);
            if (filas == 0) return InventarioErrors.AlmacenNoEncontrado;

            var costo = producto.Costo;
            if (actualizarCosto && linea.CostoUnitario is { } costoEntrada)
            {
                var costoActual = await db.Productos.Where(p => p.Id == linea.ProductoId).Select(p => p.Costo).SingleAsync(ct);
                var totalDespues = await db.Existencias.Where(e => e.ProductoId == linea.ProductoId).SumAsync(e => e.Cantidad, ct);
                costo = CostoPromedio.Calcular(totalDespues - q, costoActual, q, costoEntrada);
                await db.Productos.Where(p => p.Id == linea.ProductoId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Costo, costo), ct);
            }

            var resultante = await CantidadAsync(linea.ProductoId, almacen, ct);
            Registrar(tipo, referenciaId, linea.ProductoId, almacen, q, linea.CostoUnitario ?? costo, resultante, motivo);
        }
        return Result.Success();
    }

    /// <summary>Applies a counted difference as a relative change (sales after the count stay counted) and clears the count flag.</summary>
    public async Task<decimal> AplicarDiferenciaAsync(TipoMovimiento tipo, Guid referenciaId, Guid productoId, Guid almacen, decimal diferencia,
        decimal costo, string? motivo, CancellationToken ct)
    {
        await db.Existencias.Where(e => e.ProductoId == productoId && e.AlmacenId == almacen)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Cantidad, e => e.Cantidad + diferencia).SetProperty(e => e.PendienteContar, false), ct);
        var resultante = await CantidadAsync(productoId, almacen, ct);
        if (diferencia != 0) Registrar(tipo, referenciaId, productoId, almacen, diferencia, costo, resultante, motivo);
        return resultante;
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

    private Task<decimal> CantidadAsync(Guid productoId, Guid almacen, CancellationToken ct) =>
        db.Existencias.Where(e => e.ProductoId == productoId && e.AlmacenId == almacen).Select(e => e.Cantidad).SingleOrDefaultAsync(ct);

    private static Error? ValidarCantidad(Producto p, decimal cantidad) =>
        cantidad <= 0 || cantidad != Math.Round(cantidad, 3) || (p.Unidad == UnidadMedida.Unidad && cantidad != Math.Truncate(cantidad))
            ? Error.Validation("Inventario.CantidadInvalida", $"La cantidad de {p.Nombre} no es válida (se vende por {p.Unidad.ToString().ToLowerInvariant()}).")
            : null;

    private static IEnumerable<LineaMovimiento> Agrupar(IReadOnlyList<LineaMovimiento> lineas) =>
        lineas.GroupBy(l => l.ProductoId).OrderBy(g => g.Key).Select(g => new LineaMovimiento(g.Key, g.Sum(l => l.Cantidad)));
}
