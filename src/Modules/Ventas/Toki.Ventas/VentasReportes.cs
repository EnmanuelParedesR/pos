using Microsoft.EntityFrameworkCore;
using Toki.Caja.Contracts;
using Toki.SharedKernel;
using Toki.Ventas.Contracts;
using Toki.Ventas.Domain;

namespace Toki.Ventas;

/// <summary>Sales figures for reports. Voided sales are left out; returns are subtracted on the day they happened.
/// Profit = revenue without ITBIS − historical cost (returned goods put back on the shelf give their cost back).</summary>
/// <remarks>shortcut: aggregates in memory over the date range; fine for a store's month (thousands of sales), move to SQL GROUP BY for multi-year ranges.</remarks>
internal sealed class VentasReportes(VentasDbContext db) : IVentasReportes
{
    public async Task<IReadOnlyList<VentaDia>> PorDiaAsync(DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        var (d, h) = DiaNegocio.RangoUtc(desde, hasta);
        var ventas = await db.Ventas.Where(v => v.FechaUtc >= d && v.FechaUtc < h && v.Estado == EstadoVenta.Completada)
            .Include(v => v.Lineas).AsNoTracking().ToListAsync(ct);
        var devoluciones = await DevolucionesAsync(d, h, ct);

        var dias = ventas.Select(v => DiaNegocio.De(v.FechaUtc)).Concat(devoluciones.Select(x => DiaNegocio.De(x.Devolucion.FechaUtc))).Distinct().Order();
        return dias.Select(dia =>
        {
            var delDia = ventas.Where(v => DiaNegocio.De(v.FechaUtc) == dia).ToList();
            var devs = devoluciones.Where(x => DiaNegocio.De(x.Devolucion.FechaUtc) == dia).ToList();
            var totalDev = devs.Sum(x => x.Devolucion.Total);
            var itbis = delDia.Sum(v => v.Itbis) - devs.Sum(x => x.Devolucion.Itbis);
            var neto = delDia.Sum(v => v.Total) - totalDev;
            var costo = delDia.SelectMany(v => v.Lineas).Sum(l => l.CostoUnitario * l.Cantidad) - devs.Sum(x => x.CostoRepuesto);
            return new VentaDia(dia, delDia.Count, delDia.Sum(v => v.Bruto), delDia.Sum(v => v.Descuento), itbis, totalDev, neto,
                Dinero.Redondear(costo), Dinero.Redondear(neto - itbis - costo));
        }).ToList();
    }

    public async Task<IReadOnlyList<VentaProducto>> PorProductoAsync(DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        var (d, h) = DiaNegocio.RangoUtc(desde, hasta);
        var lineas = await (from l in db.Lineas
                            join v in db.Ventas on l.VentaId equals v.Id
                            where v.FechaUtc >= d && v.FechaUtc < h && v.Estado == EstadoVenta.Completada
                            select l).AsNoTracking().ToListAsync(ct);
        var ids = lineas.Select(l => l.Id).ToList();
        var devuelto = await db.Set<LineaDevolucion>().Where(x => ids.Contains(x.LineaVentaId)).AsNoTracking().ToListAsync(ct);

        return lineas.GroupBy(l => l.ProductoId).Select(g =>
        {
            var devs = devuelto.Where(x => g.Any(l => l.Id == x.LineaVentaId)).ToList();
            decimal CostoDe(LineaDevolucion x) => x.Repuesto ? g.First(l => l.Id == x.LineaVentaId).CostoUnitario * x.Cantidad : 0;
            var cantidad = g.Sum(l => l.Cantidad * l.Factor) - devs.Sum(x => x.Cantidad * g.First(l => l.Id == x.LineaVentaId).Factor);
            var neto = g.Sum(l => l.Total - l.Itbis) - devs.Sum(x => x.Total - x.Itbis);
            var costo = g.Sum(l => l.CostoUnitario * l.Cantidad) - devs.Sum(CostoDe);
            var nombre = g.OrderByDescending(l => l.PresentacionId is null).First().Descripcion;
            return new VentaProducto(g.Key, g.First().CategoriaId, nombre, cantidad, neto, Dinero.Redondear(costo), Dinero.Redondear(neto - costo));
        }).OrderByDescending(p => p.Neto).ToList();
    }

    public async Task<IReadOnlyList<VentaCajero>> PorCajeroAsync(DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        var (d, h) = DiaNegocio.RangoUtc(desde, hasta);
        var ventas = await db.Ventas.Where(v => v.FechaUtc >= d && v.FechaUtc < h).AsNoTracking().ToListAsync(ct);
        return ventas.GroupBy(v => v.CajeroId).Select(g => new VentaCajero(g.Key, g.First().Cajero,
            g.Count(v => v.Estado == EstadoVenta.Completada), g.Count(v => v.Estado == EstadoVenta.Anulada),
            g.Where(v => v.Estado == EstadoVenta.Completada).Sum(v => v.Total), g.Where(v => v.Estado == EstadoVenta.Completada).Sum(v => v.Descuento)))
            .OrderByDescending(c => c.Neto).ToList();
    }

    public async Task<IReadOnlyList<Documento607>> Documentos607Async(DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        var (d, h) = DiaNegocio.RangoUtc(desde, hasta);
        // Every issued e-CF counts, even of a voided sale: the void issued its own credit note.
        var ventas = await db.Ventas.Where(v => v.FechaUtc >= d && v.FechaUtc < h).Include(v => v.Pagos).AsNoTracking().ToListAsync(ct);
        var anuladas = await db.Ventas.Where(v => v.AnuladaUtc >= d && v.AnuladaUtc < h).AsNoTracking().ToListAsync(ct);
        var devoluciones = await (from x in db.Devoluciones
                                  join v in db.Ventas on x.VentaId equals v.Id
                                  where x.FechaUtc >= d && x.FechaUtc < h
                                  select new { x, v.ENcf, v.RncCliente }).AsNoTracking().ToListAsync(ct);

        static decimal Medio(Venta v, params MedioPago[] medios) => v.Pagos.Where(p => medios.Contains(p.Medio)).Sum(p => p.Monto);
        var lista = ventas.Select(v => new Documento607(v.RncCliente, v.ENcf, null, DiaNegocio.De(v.FechaUtc), v.Total, v.Itbis,
                Medio(v, MedioPago.Efectivo), Medio(v, MedioPago.Tarjeta, MedioPago.Transferencia), Medio(v, MedioPago.Fiado), Medio(v, MedioPago.SaldoFavor)))
            .Concat(anuladas.Select(v => new Documento607(v.RncCliente, v.ENcfAnulacion!, v.ENcf, DiaNegocio.De(v.AnuladaUtc!.Value), v.Total, v.Itbis, 0, 0, 0, 0)))
            .Concat(devoluciones.Select(x => new Documento607(x.RncCliente, x.x.ENcf, x.ENcf, DiaNegocio.De(x.x.FechaUtc), x.x.Total, x.x.Itbis, 0, 0, 0, 0)));
        return lista.OrderBy(x => x.Fecha).ThenBy(x => x.ENcf, StringComparer.Ordinal).ToList();
    }

    private async Task<List<(Devolucion Devolucion, decimal CostoRepuesto)>> DevolucionesAsync(DateTime d, DateTime h, CancellationToken ct)
    {
        var devoluciones = await db.Devoluciones.Where(x => x.FechaUtc >= d && x.FechaUtc < h).Include(x => x.Lineas).AsNoTracking().ToListAsync(ct);
        var ids = devoluciones.SelectMany(x => x.Lineas).Select(l => l.LineaVentaId).Distinct().ToList();
        var costos = await db.Lineas.Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.CostoUnitario, ct);
        return devoluciones.Select(x => (x, x.Lineas.Where(l => l.Repuesto).Sum(l => costos[l.LineaVentaId] * l.Cantidad))).ToList();
    }
}
