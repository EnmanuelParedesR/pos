using Toki.SharedKernel;

namespace Toki.Inventario.Contracts;

public enum UnidadMedida { Unidad, Libra }

public enum TipoMovimiento
{
    Venta, AnulacionVenta, Devolucion,
    Compra, AnulacionCompra,
    Ajuste, Conteo,
    TransferenciaSalida, TransferenciaEntrada,
}

/// <summary>What a sale needs to price and record a line. <see cref="Factor"/> is base units per sold unit (1 without a presentation).</summary>
public sealed record ProductoParaVenta(
    Guid ProductoId, Guid? PresentacionId, string Nombre, Guid CategoriaId, string Categoria, UnidadMedida Unidad,
    TasaItbis TasaItbis, decimal Precio, decimal? PrecioMayorista, decimal Factor, decimal CostoBase,
    bool VenderSinExistencia, bool Activo);

public interface ICatalogoConsultas
{
    /// <summary>Products (or presentations) by id. Missing ids are simply absent from the result.</summary>
    Task<IReadOnlyList<ProductoParaVenta>> ObtenerParaVentaAsync(IReadOnlyCollection<(Guid ProductoId, Guid? PresentacionId)> items, CancellationToken ct);
}

/// <param name="Cantidad">Base units (units or pounds), always positive.</param>
/// <param name="CostoUnitario">Cost per base unit for entries (purchases); ignored on exits.</param>
public sealed record LineaMovimiento(Guid ProductoId, decimal Cantidad, decimal? CostoUnitario = null);

/// <summary>Stock changes called inside another module's transaction (a sale, a purchase).</summary>
public interface IMovimientosInventario
{
    /// <summary>Takes stock out. Fails with Inventario.SinExistencia unless the product may be sold without stock (then it is flagged for counting).</summary>
    Task<Result> RegistrarSalidaAsync(TipoMovimiento tipo, Guid referenciaId, IReadOnlyList<LineaMovimiento> lineas, CancellationToken ct, Guid? almacenId = null);

    /// <summary>Puts stock in. When <paramref name="actualizarCosto"/> is true (purchases) the weighted average cost is recomputed.</summary>
    Task<Result> RegistrarEntradaAsync(TipoMovimiento tipo, Guid referenciaId, IReadOnlyList<LineaMovimiento> lineas, bool actualizarCosto, CancellationToken ct, Guid? almacenId = null);
}

public sealed record PrecioCambiadoV1(Guid ProductoId, decimal PrecioAnterior, decimal PrecioNuevo, decimal? MayoristaAnterior, decimal? MayoristaNuevo)
    : IntegrationEvent(ProductoId);

public sealed record MovimientoInventarioRegistradoV1(Guid MovimientoId, Guid ProductoId, Guid AlmacenId, TipoMovimiento Tipo, decimal Cantidad, decimal CostoUnitario, Guid? ReferenciaId)
    : IntegrationEvent(ProductoId);
