using System.Globalization;
using Toki.Fiscal.Contracts;
using Toki.SharedKernel;

namespace Toki.Fiscal.Domain;

/// <summary>An authorized e-NCF range for one receipt type.</summary>
internal sealed class Secuencia
{
    public Guid Id { get; set; }
    public TipoEcf Tipo { get; set; }
    public long Desde { get; set; }
    public long Hasta { get; set; }
    /// <summary>Next number to assign. Moved only by an atomic update inside the sale's transaction, so a rollback leaves no gap.</summary>
    public long Siguiente { get; set; }
    public DateOnly VenceEl { get; set; }
    public bool Activa { get; set; } = true;
    public DateTime CreadaUtc { get; set; }

    public long Usados => Siguiente - Desde;
    public long Total => Hasta - Desde + 1;
    public decimal PorcentajeUsado => Total == 0 ? 100 : Math.Round(100m * Usados / Total, 1);
}

internal enum EstadoEnvio { Pendiente, Aceptado, AceptadoCondicional, Rechazado }

/// <summary>A signed e-CF. Never deleted or edited: corrections are credit notes.</summary>
internal sealed class DocumentoFiscal
{
    public Guid Id { get; set; }
    public string ENcf { get; set; } = "";
    public TipoEcf Tipo { get; set; }
    public Guid ReferenciaId { get; set; }
    public string? ENcfModificado { get; set; }
    public string? RncComprador { get; set; }
    public string? NombreComprador { get; set; }
    public decimal Itbis { get; set; }
    public decimal Total { get; set; }
    public DateTime FechaFirmaUtc { get; set; }
    public string CodigoSeguridad { get; set; } = "";
    /// <summary>GZip of the signed XML.</summary>
    public byte[] XmlFirmado { get; set; } = [];
    public EstadoEnvio Estado { get; set; }
    /// <summary>DGII deadline: 72 hours after signing.</summary>
    public DateTime FechaLimiteEnvioUtc { get; set; }
    public string? TrackId { get; set; }
    public int Intentos { get; set; }
    public DateTime? ProximoIntentoUtc { get; set; }
    public string? UltimoError { get; set; }
}

internal static class ENcf
{
    /// <summary>"E" + 2-digit type + 10-digit sequence, e.g. E320000000001.</summary>
    public static string Formatear(TipoEcf tipo, long numero) =>
        $"E{(int)tipo:00}{numero.ToString("D10", CultureInfo.InvariantCulture)}";

    public static bool EsValido(string? valor) =>
        valor is { Length: 13 } && valor[0] == 'E' && valor.Skip(1).All(char.IsAsciiDigit);
}

internal static class FiscalErrors
{
    public static Error SinSecuencia(TipoEcf tipo) =>
        Error.Conflict("Fiscal.SinSecuencia", $"No hay números e-NCF disponibles para {Nombre(tipo)}. Cargue un rango nuevo.");

    public static readonly Error RangoSolapado = Error.Conflict("Fiscal.RangoSolapado", "Ese rango se cruza con otro ya cargado.");
    public static readonly Error RncRequerido = Error.Validation("Fiscal.RncRequerido", "El crédito fiscal necesita el RNC o la cédula del cliente.");

    public static string Nombre(TipoEcf tipo) => tipo switch
    {
        TipoEcf.CreditoFiscal31 => "Factura de Crédito Fiscal Electrónica",
        TipoEcf.Consumo32 => "Factura de Consumo Electrónica",
        TipoEcf.NotaDebito33 => "Nota de Débito Electrónica",
        _ => "Nota de Crédito Electrónica",
    };
}
