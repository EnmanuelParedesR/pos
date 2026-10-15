using Toki.SharedKernel;

namespace Toki.Purchasing.Contracts;

public sealed record CompraRegistradaV1(Guid CompraId, Guid SuplidorId, string Ncf, decimal Subtotal, decimal Itbis, decimal Total, bool Credito)
    : IntegrationEvent(CompraId);

public sealed record CompraAnuladaV1(Guid CompraId, string Motivo) : IntegrationEvent(CompraId);

public sealed record PagoSuplidorRegistradoV1(Guid PagoId, Guid SuplidorId, decimal Monto) : IntegrationEvent(PagoId);
