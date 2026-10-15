using Toki.SharedKernel;

namespace Toki.Fiscal.Contracts;

/// <summary>Electronic receipt types (e-CF) used in V1.</summary>
public enum TipoEcf { CreditoFiscal31 = 31, Consumo32 = 32, NotaDebito33 = 33, NotaCredito34 = 34 }

public sealed record LineaFiscal(string Descripcion, decimal Cantidad, decimal PrecioUnitario, decimal Descuento, TasaItbis Tasa, decimal Itbis, decimal Total);

/// <summary>DGII modification code of a credit note: 1 cancels the whole document (a void), 3 corrects amounts (a return).</summary>
public enum CodigoModificacion { Anulacion = 1, CorreccionMontos = 3 }

public enum TipoPagoEcf { Contado = 1, Credito = 2 }

/// <summary>Consumer receipts (32) of this amount or more must identify the buyer.</summary>
public static class LimitesEcf
{
    public const decimal ConsumoIdentificado = 250_000m;
}

/// <param name="ENcfModificado">For credit notes: the e-NCF being corrected (required).</param>
public sealed record DocumentoAEmitir(
    TipoEcf Tipo, Guid ReferenciaId, string? RncComprador, string? NombreComprador,
    IReadOnlyList<LineaFiscal> Lineas, decimal Itbis, decimal Total, string? ENcfModificado = null,
    CodigoModificacion? CodigoModificacion = null, TipoPagoEcf TipoPago = TipoPagoEcf.Contado);

/// <param name="UrlQr">What the printed QR code points to.</param>
public sealed record ComprobanteEmitido(Guid DocumentoId, string ENcf, string CodigoSeguridad, DateTime FechaFirmaUtc, string UrlQr);

/// <summary>Issues e-CF inside the caller's transaction: reserves the next e-NCF (no gaps), signs locally and queues it for the 72-hour submission.</summary>
public interface IComprobantesFiscales
{
    Task<Result<ComprobanteEmitido>> EmitirAsync(DocumentoAEmitir documento, CancellationToken ct);
}

public sealed record ComprobanteEmitidoV1(Guid DocumentoId, string ENcf, TipoEcf Tipo, Guid ReferenciaId, decimal Total, decimal Itbis) : IntegrationEvent(DocumentoId);
