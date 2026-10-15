using Toki.CashRegister.Contracts;
using Toki.SharedKernel;

namespace Toki.Sales.Domain;

public sealed record Descuento(bool EsPorcentaje, decimal Valor);

/// <param name="MontoFijo">Line amount read from a price label: the line total is fixed, quantity is informative.</param>
internal sealed record LineaEntrada(decimal PrecioUnitario, decimal Cantidad, TasaItbis Tasa, Descuento? Descuento, decimal? MontoFijo = null);

internal sealed record LineaCalculada(decimal Bruto, decimal DescuentoLinea, decimal DescuentoGlobal, decimal Total, decimal Itbis)
{
    public decimal Descuento => DescuentoLinea + DescuentoGlobal;
}

internal sealed record Totales(IReadOnlyList<LineaCalculada> Lineas, decimal Bruto, decimal Descuento, decimal Subtotal, decimal Itbis, decimal Total)
{
    public decimal PorcentajeDescuento => Bruto == 0 ? 0 : Math.Round(Descuento / Bruto * 100, 2);
}

/// <summary>The only place where a sale's money is computed. Shelf prices include ITBIS (DR retail),
/// so ITBIS is extracted from each line's final amount. Rounding: 2 decimals, half away from zero, per line.</summary>
internal static class Calculadora
{
    public static Result<Totales> Calcular(IReadOnlyList<LineaEntrada> lineas, Descuento? global)
    {
        var brutos = new decimal[lineas.Count];
        var netos = new decimal[lineas.Count];
        var descLinea = new decimal[lineas.Count];
        for (var i = 0; i < lineas.Count; i++)
        {
            var l = lineas[i];
            brutos[i] = l.MontoFijo ?? Dinero.Redondear(l.PrecioUnitario * l.Cantidad);
            var d = Monto(l.Descuento, brutos[i]);
            if (d.IsFailure) return d.Error!;
            descLinea[i] = d.Value;
            netos[i] = brutos[i] - descLinea[i];
        }

        var sumaNetos = netos.Sum();
        var g = Monto(global, sumaNetos);
        if (g.IsFailure) return g.Error!;
        var partes = Repartir(g.Value, netos);

        var calculadas = lineas.Select((l, i) =>
        {
            var total = netos[i] - partes[i];
            return new LineaCalculada(brutos[i], descLinea[i], partes[i], total, Itbis.Incluido(total, l.Tasa));
        }).ToList();

        var totalVenta = calculadas.Sum(c => c.Total);
        var itbis = calculadas.Sum(c => c.Itbis);
        return new Totales(calculadas, brutos.Sum(), calculadas.Sum(c => c.Descuento), totalVenta - itbis, itbis, totalVenta);
    }

    private static Result<decimal> Monto(Descuento? d, decimal sobre)
    {
        if (d is null || d.Valor == 0) return 0m;
        if (d.Valor < 0 || (d.EsPorcentaje && d.Valor > 100))
            return Error.Validation("Ventas.DescuentoInvalido", "El descuento debe estar entre 0 y 100 %, o ser un monto positivo.");
        var monto = Dinero.Redondear(d.EsPorcentaje ? sobre * d.Valor / 100 : d.Valor);
        if (monto > sobre) return Error.Validation("Ventas.DescuentoInvalido", "El descuento es mayor que el importe.");
        return monto;
    }

    /// <summary>Spreads a global discount over the lines in proportion to their amount; the rounding remainder goes to the biggest line.</summary>
    private static decimal[] Repartir(decimal monto, decimal[] netos)
    {
        var partes = new decimal[netos.Length];
        var suma = netos.Sum();
        if (monto == 0 || suma == 0) return partes;
        for (var i = 0; i < netos.Length; i++) partes[i] = Dinero.Redondear(monto * netos[i] / suma);
        var mayor = Array.IndexOf(netos, netos.Max());
        partes[mayor] += monto - partes.Sum();
        return partes;
    }
}

internal sealed record PagoEntrada(MedioPago Medio, decimal Monto, string? Referencia);

/// <param name="Monto">Amount applied to the sale (cash received minus change).</param>
/// <param name="Recibido">For cash: what the customer handed over.</param>
internal sealed record PagoAplicado(MedioPago Medio, decimal Monto, decimal? Recibido, string? Referencia);

internal static class Pagos
{
    /// <summary>Mixed payments must add up to the total. Only cash may exceed it, and the excess is the change.
    /// There are no coins under RD$ 1: cash comes in whole pesos and the change is rounded down; the kept centavos are
    /// <c>Redondeo</c>, so the drawer's expected cash is always a countable amount.</summary>
    public static Result<(IReadOnlyList<PagoAplicado> Pagos, decimal Cambio, decimal Redondeo)> Aplicar(decimal total, IReadOnlyList<PagoEntrada> pagos)
    {
        if (pagos.Any(p => p.Monto <= 0 || p.Monto != Math.Round(p.Monto, 2)))
            return Error.Validation("Ventas.PagoInvalido", "Cada pago debe ser un monto positivo con hasta 2 decimales.");
        if (pagos.Any(p => p.Medio == MedioPago.Efectivo && p.Monto != Math.Truncate(p.Monto)))
            return Error.Validation("Ventas.EfectivoSinCentavos", "El efectivo se recibe en pesos enteros.");
        var efectivo = pagos.Where(p => p.Medio == MedioPago.Efectivo).Sum(p => p.Monto);
        var otros = pagos.Where(p => p.Medio != MedioPago.Efectivo).Sum(p => p.Monto);
        if (otros > total) return Error.Validation("Ventas.PagoExcedido", "Solo el efectivo puede pasar del total (la devuelta sale del efectivo).");
        if (efectivo + otros < total) return Error.Validation("Ventas.PagoIncompleto", $"Faltan RD$ {total - efectivo - otros:N2}.");

        var exceso = efectivo + otros - total;
        var cambio = Math.Floor(exceso);
        var porDescontar = exceso;
        var aplicados = new List<PagoAplicado>();
        foreach (var p in pagos.Reverse())
        {
            if (p.Medio != MedioPago.Efectivo) { aplicados.Add(new PagoAplicado(p.Medio, p.Monto, null, p.Referencia)); continue; }
            var resta = Math.Min(porDescontar, p.Monto);
            porDescontar -= resta;
            aplicados.Add(new PagoAplicado(p.Medio, p.Monto - resta, p.Monto, p.Referencia));
        }
        aplicados.Reverse();
        return (aplicados.Where(a => a.Monto > 0).ToList(), cambio, exceso - cambio);
    }
}
