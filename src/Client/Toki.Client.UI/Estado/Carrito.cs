using Toki.Client.UI.Api;

namespace Toki.Client.UI.Estado;

public sealed record LineaCarrito(Guid ProductoId, Guid? PresentacionId, string Nombre, string Unidad, decimal PrecioLista, decimal Cantidad,
    string? CodigoEtiqueta = null, decimal? MontoEtiqueta = null, Descuento? Descuento = null, decimal? PrecioCambiado = null)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public decimal Precio => PrecioCambiado ?? PrecioLista;
    /// <summary>A price label carries the line total; otherwise price × quantity to the centavo.</summary>
    public decimal Bruto => MontoEtiqueta ?? Math.Round(Precio * Cantidad, 2, MidpointRounding.AwayFromZero);
    public decimal MontoDescuento => Descuento is null ? 0m
        : Descuento.EsPorcentaje ? Math.Round(Bruto * Descuento.Valor / 100m, 2, MidpointRounding.AwayFromZero) : Math.Min(Descuento.Valor, Bruto);
    public decimal Total => Bruto - MontoDescuento;
    public bool PorPeso => Unidad == nameof(UnidadMedida.Libra);

    public LineaVenta ParaVenta() => new(ProductoId, PresentacionId, Cantidad,
        PrecioCambiado, Descuento, null, CodigoEtiqueta);
}

/// <summary>The sale being rung up. The server recomputes everything; this total is what the cashier sees and what it confirms (TotalEsperado).</summary>
public sealed class Carrito
{
    private readonly List<LineaCarrito> _lineas = [];

    /// <summary>Also the idempotency key of the sale: a retry after a timeout never charges twice.</summary>
    public Guid VentaId { get; private set; } = Guid.CreateVersion7();
    public IReadOnlyList<LineaCarrito> Lineas => _lineas;
    public Guid? Seleccionada { get; set; }
    public ClienteDetalle? Cliente { get; set; }
    public TipoEcf Comprobante { get; set; } = TipoEcf.Consumo32;
    public string? RncManual { get; set; }
    public string? NombreManual { get; set; }

    public decimal Total => _lineas.Sum(l => l.Total);
    public decimal Descuentos => _lineas.Sum(l => l.MontoDescuento);
    public int Articulos => _lineas.Count;
    public bool Vacio => _lineas.Count == 0;
    public LineaCarrito? LineaSeleccionada => _lineas.Find(l => l.Id == Seleccionada);

    /// <summary>A label already in the cart (the design's "etiqueta repetida"): the server would refuse it too.</summary>
    public bool TieneEtiqueta(string codigo) => _lineas.Exists(l => l.CodigoEtiqueta == codigo);

    /// <summary>Adds a scanned or tapped product. The same per-unit product without a label adds up on its line.</summary>
    public LineaCarrito Agregar(ProductoPorCodigo p, string? codigoEtiqueta, decimal cantidad = 1m)
    {
        LineaCarrito linea;
        if (codigoEtiqueta is not null && (p.CantidadEtiqueta is not null || p.MontoEtiqueta is not null))
        {
            // The API ignores Cantidad for labels but validates it > 0; a price label's weight is shown as its equivalent.
            var cantidadEtiqueta = p.CantidadEtiqueta ?? Math.Max(0.001m, Math.Round(p.MontoEtiqueta!.Value / p.Precio, 3));
            linea = new(p.ProductoId, null, p.Nombre, p.Unidad, p.Precio, cantidadEtiqueta, codigoEtiqueta, p.MontoEtiqueta);
            _lineas.Add(linea);
        }
        else if (p.Unidad == nameof(UnidadMedida.Unidad) && _lineas.FindLastIndex(l => l.ProductoId == p.ProductoId && l.PresentacionId == p.PresentacionId
                     && l.CodigoEtiqueta is null && l.Descuento is null && l.PrecioCambiado is null) is var i and >= 0)
        {
            linea = _lineas[i] with { Cantidad = _lineas[i].Cantidad + cantidad };
            _lineas[i] = linea;
        }
        else
        {
            linea = new(p.ProductoId, p.PresentacionId, p.Nombre, p.Unidad, p.Precio, cantidad);
            _lineas.Add(linea);
        }
        Seleccionada = linea.Id;
        return linea;
    }

    public void Cambiar(LineaCarrito linea)
    {
        var i = _lineas.FindIndex(l => l.Id == linea.Id);
        if (i >= 0) _lineas[i] = linea;
    }

    public void Quitar(Guid id)
    {
        var i = _lineas.FindIndex(l => l.Id == id);
        if (i < 0) return;
        _lineas.RemoveAt(i);
        Seleccionada = _lineas.Count == 0 ? null : _lineas[Math.Min(i, _lineas.Count - 1)].Id;
    }

    public void Vaciar()
    {
        _lineas.Clear();
        (Seleccionada, Cliente, Comprobante, RncManual, NombreManual) = (null, null, TipoEcf.Consumo32, null, null);
        VentaId = Guid.CreateVersion7();
    }

    public RegistrarVenta ParaRegistrar(Guid turnoId, List<Pago> pagos, decimal totalEsperado, List<Guid> autorizaciones) =>
        new(VentaId, turnoId, [.. _lineas.Select(l => l.ParaVenta())], pagos, totalEsperado, Comprobante, Cliente?.Id,
            Cliente is null ? RncManual : null, Cliente is null ? NombreManual : null, NivelPrecio.Detalle, null, autorizaciones);

    /// <summary>What a held sale stores on the server, and how it comes back.</summary>
    public CarritoGuardado Guardar() => new([.. _lineas], Cliente, Comprobante, RncManual, NombreManual);

    public void Restaurar(CarritoGuardado g)
    {
        Vaciar();
        _lineas.AddRange(g.Lineas);
        (Cliente, Comprobante, RncManual, NombreManual) = (g.Cliente, g.Comprobante, g.RncManual, g.NombreManual);
        Seleccionada = _lineas.LastOrDefault()?.Id;
    }
}

public sealed record CarritoGuardado(List<LineaCarrito> Lineas, ClienteDetalle? Cliente, TipoEcf Comprobante, string? RncManual, string? NombreManual);
