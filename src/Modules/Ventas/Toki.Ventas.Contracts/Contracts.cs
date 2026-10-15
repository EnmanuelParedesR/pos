using Toki.SharedKernel;

namespace Toki.Ventas.Contracts;

public sealed record LineaVentaV1(Guid ProductoId, Guid? PresentacionId, string Descripcion, decimal Cantidad, decimal Total, decimal Itbis, decimal Costo);

public sealed record PagoVentaV1(string Medio, decimal Monto);

public sealed record VentaRegistradaV1(
    Guid VentaId, long Numero, Guid TurnoId, Guid CajeroId, Guid? ClienteId, string ENcf, decimal Total, decimal Itbis, decimal Descuento,
    IReadOnlyList<LineaVentaV1> Lineas, IReadOnlyList<PagoVentaV1> Pagos) : IntegrationEvent(VentaId);

public sealed record VentaAnuladaV1(Guid VentaId, string ENcfNotaCredito, string Motivo, Guid? SupervisorId) : IntegrationEvent(VentaId);

public sealed record DevolucionRegistradaV1(Guid DevolucionId, Guid VentaId, string ENcfNotaCredito, string Medio, decimal Total, decimal Itbis)
    : IntegrationEvent(VentaId);
