using Microsoft.EntityFrameworkCore;
using Toki.Caja.Contracts;
using Toki.SharedKernel;
using Toki.Ventas.Contracts;
using Toki.Ventas.Domain;

namespace Toki.Ventas;

/// <summary>Sales figures for reports. Voided sales are left out; returns are subtracted on the day they happened, in every report,
/// so a closed period never changes and the reports agree with each other.
/// Profit = revenue without ITBIS − historical cost (returned goods put back on the shelf give their cost back).</summary>
/// <remarks>shortcut: aggregates in memory over at most a year of one-row-per-sale projections; move to SQL GROUP BY if stores keep years online.</remarks>
internal sealed class VentasReportes(VentasDbContext db) : IVentasReportes
{
    public async Task<IReadOnlyList<VentaDia>> PorDiaAsync(DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        var (d, h) = DiaNegocio.RangoUtc(desde, hasta);
        var ventas = await db.Ventas.Where(v => v.FechaUtc >= d && v.FechaUtc < h && v.Estado == EstadoVenta.Completada)
            .Select(v => new { v.FechaUtc, v.Total, v.Itbis, v.Descuento, Costo = v.Lineas.Sum(l => l.CostoUnitario * l.Cantidad) })
            .AsNoTracking().ToListAsync(ct);
        var devoluciones = await DevueltoAsync(d, h, ct);

        var dias = ventas.Select(v => DiaNegocio.De(v.FechaUtc)).Concat(devoluciones.Select(x => DiaNegocio.De(x.FechaUtc))).Distinct().Order();
        return dias.Select(dia =>
        {
            var delDia = ventas.Where(v => DiaNegocio.De(v.FechaUtc) == dia).ToList();
            var devs = devoluciones.Where(x => DiaNegocio.De(x.FechaUtc) == dia).ToList();
            var itbis = delDia.Sum(v => v.Itbis) - devs.Sum(x => x.Itbis);
            var neto = delDia.Sum(v => v.Total - v.Itbis) - devs.Sum(x => x.Total - x.Itbis);
            var costo = Dinero.Redondear(delDia.Sum(v => v.Costo) - devs.Sum(x => x.CostoRepuesto));
            return new VentaDia(dia, delDia.Count, delDia.Sum(v => v.Total), delDia.Sum(v => v.Descuento), devs.Sum(x => x.Total), itbis, neto, costo, neto - costo);
        }).ToList();
    }

    public async Task<IReadOnlyList<VentaProducto>> PorProductoAsync(DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        var (d, h) = DiaNegocio.RangoUtc(desde, hasta);
        var vendidas = await (from l in db.Lineas
                              join v in db.Ventas on l.VentaId equals v.Id
                              where v.FechaUtc >= d && v.FechaUtc < h && v.Estado == EstadoVenta.Completada
                              select new Movida(l.ProductoId, l.CategoriaId, l.Descripcion, l.PresentacionId == null, l.Cantidad * l.Factor, l.Total - l.Itbis, l.CostoUnitario * l.Cantidad))
            .AsNoTracking().ToListAsync(ct);
        var devueltas = (await DevueltoAsync(d, h, ct)).SelectMany(x => x.Lineas);

        return vendidas.Concat(devueltas).GroupBy(m => m.ProductoId).Select(g =>
        {
            var neto = g.Sum(m => m.Neto);
            var costo = Dinero.Redondear(g.Sum(m => m.Costo));
            var nombre = g.OrderByDescending(m => m.SinPresentacion).First().Descripcion;
            return new VentaProducto(g.Key, g.First().CategoriaId, nombre, g.Sum(m => m.Cantidad), neto, costo, neto - costo);
        }).OrderByDescending(p => p.NetoSinItbis).ToList();
    }

    public async Task<IReadOnlyList<VentaCajero>> PorCajeroAsync(DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        var (d, h) = DiaNegocio.RangoUtc(desde, hasta);
        var ventas = await db.Ventas.Where(v => v.FechaUtc >= d && v.FechaUtc < h).Select(v => new { v.CajeroId, v.Cajero, v.Estado, v.Total, v.Descuento })
            .AsNoTracking().ToListAsync(ct);
        return ventas.GroupBy(v => v.CajeroId).Select(g => new VentaCajero(g.Key, g.First().Cajero,
            g.Count(v => v.Estado == EstadoVenta.Completada), g.Count(v => v.Estado == EstadoVenta.Anulada),
            g.Where(v => v.Estado == EstadoVenta.Completada).Sum(v => v.Total), g.Where(v => v.Estado == EstadoVenta.Completada).Sum(v => v.Descuento)))
            .OrderByDescending(c => c.VendidoConItbis).ToList();
    }

    /// <remarks>shortcut: payment columns of credit notes are left at zero and e-CF already received by DGII are listed too; the accountant confirms both (R5 #1).</remarks>
    public async Task<IReadOnlyList<Documento607>> Documentos607Async(DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        var (d, h) = DiaNegocio.RangoUtc(desde, hasta);
        // Every issued e-CF counts, even of a voided sale: the void issued its own credit note.
        var ventas = await db.Ventas.Where(v => v.FechaUtc >= d && v.FechaUtc < h).Include(v => v.Pagos).AsNoTracking().ToListAsync(ct);
        var anuladas = await db.Ventas.Where(v => v.AnuladaUtc >= d && v.AnuladaUtc < h)
            .Select(v => new { v.RncCliente, v.ENcf, v.ENcfAnulacion, v.AnuladaUtc, v.Total, v.Itbis }).AsNoTracking().ToListAsync(ct);
        var devoluciones = await (from x in db.Devoluciones
                                  join v in db.Ventas on x.VentaId equals v.Id
                                  where x.FechaUtc >= d && x.FechaUtc < h
                                  select new { x.ENcf, x.FechaUtc, x.Total, x.Itbis, Modificado = v.ENcf, v.RncCliente }).AsNoTracking().ToListAsync(ct);

        static int? TipoId(string? rnc) => rnc switch { null => null, { Length: 11 } => 2, _ => 1 };
        static Documento607 Fila(string? rnc, string ncf, string? modificado, DateTime fechaUtc, decimal total, decimal itbis,
            decimal efectivo = 0, decimal cheque = 0, decimal tarjeta = 0, decimal credito = 0, decimal otras = 0) =>
            new(rnc, TipoId(rnc), ncf, modificado, 1, DiaNegocio.De(fechaUtc), null, total - itbis, itbis, 0, 0, 0, 0, 0, 0, 0, efectivo, cheque, tarjeta, credito, 0, 0, otras);
        decimal Medio(Venta v, MedioPago medio) => v.Pagos.Where(p => p.Medio == medio).Sum(p => p.Monto);

        return ventas.Select(v => Fila(v.RncCliente, v.ENcf, null, v.FechaUtc, v.Total, v.Itbis, Medio(v, MedioPago.Efectivo), Medio(v, MedioPago.Transferencia),
                Medio(v, MedioPago.Tarjeta), Medio(v, MedioPago.Fiado), Medio(v, MedioPago.SaldoFavor)))
            .Concat(anuladas.Select(v => Fila(v.RncCliente, v.ENcfAnulacion!, v.ENcf, v.AnuladaUtc!.Value, v.Total, v.Itbis)))
            .Concat(devoluciones.Select(x => Fila(x.RncCliente, x.ENcf, x.Modificado, x.FechaUtc, x.Total, x.Itbis)))
            .OrderBy(x => x.FechaComprobante).ThenBy(x => x.Ncf, StringComparer.Ordinal).ToList();
    }

    /// <summary>A line sold (positive) or returned (negative) in the period.</summary>
    private sealed record Movida(Guid ProductoId, Guid CategoriaId, string Descripcion, bool SinPresentacion, decimal Cantidad, decimal Neto, decimal Costo);

    private sealed record Devuelto(DateTime FechaUtc, decimal Total, decimal Itbis, decimal CostoRepuesto, List<Movida> Lineas);

    /// <summary>Returns made in the range (by their own date), with their product, quantity and the cost of what went back on the shelf.</summary>
    private async Task<List<Devuelto>> DevueltoAsync(DateTime d, DateTime h, CancellationToken ct)
    {
        var lineas = await (from ld in db.Set<LineaDevolucion>()
                            join x in db.Devoluciones on ld.DevolucionId equals x.Id
                            join lv in db.Lineas on ld.LineaVentaId equals lv.Id
                            where x.FechaUtc >= d && x.FechaUtc < h
                            select new { x.Id, x.FechaUtc, DevTotal = x.Total, DevItbis = x.Itbis, lv.ProductoId, lv.CategoriaId, lv.Descripcion, lv.PresentacionId,
                                lv.Factor, lv.CostoUnitario, ld.Cantidad, ld.Total, ld.Itbis, ld.Repuesto })
            .AsNoTracking().ToListAsync(ct);
        return lineas.GroupBy(l => l.Id).Select(g =>
        {
            var movidas = g.Select(l => new Movida(l.ProductoId, l.CategoriaId, l.Descripcion, l.PresentacionId == null, -l.Cantidad * l.Factor, -(l.Total - l.Itbis),
                l.Repuesto ? -l.CostoUnitario * l.Cantidad : 0)).ToList();
            return new Devuelto(g.First().FechaUtc, g.First().DevTotal, g.First().DevItbis, -movidas.Sum(m => m.Costo), movidas);
        }).ToList();
    }
}
