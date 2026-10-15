using System.Globalization;
using Toki.Fiscal.Contracts;
using Toki.SharedKernel;

namespace Toki.Fiscal.Domain;

/// <summary>An authorized e-NCF range for one receipt type.</summary>
internal sealed class NcfSequence
{
    public Guid Id { get; set; }
    public EcfType Type { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    /// <summary>Next number to assign. Moved only by an atomic update inside the sale's transaction, so a rollback leaves no gap.</summary>
    public long Next { get; set; }
    public DateOnly ExpiresOn { get; set; }
    public DateTime CreatedUtc { get; set; }

    public long Used => Next - From;
    public long Total => To - From + 1;
    public decimal PercentUsed => Total == 0 ? 100 : Math.Round(100m * Used / Total, 1);
}

/// <summary>Simulated: signed by the stand-in signer; never sent to the DGII (the real sender only reads Pending).</summary>
internal enum SubmissionStatus { Pending, Accepted, AcceptedConditional, Rejected, Simulated }

/// <summary>A signed e-CF. Never deleted or edited: corrections are credit notes.</summary>
internal sealed class FiscalDocument
{
    public Guid Id { get; set; }
    public string ENcf { get; set; } = "";
    public EcfType Type { get; set; }
    public Guid ReferenceId { get; set; }
    public string? ModifiedENcf { get; set; }
    public string? BuyerRnc { get; set; }
    public string? BuyerName { get; set; }
    public decimal Itbis { get; set; }
    public decimal Total { get; set; }
    public DateTime SignedUtc { get; set; }
    public string SecurityCode { get; set; } = "";
    /// <summary>GZip of the signed XML.</summary>
    public byte[] SignedXml { get; set; } = [];
    public SubmissionStatus Status { get; set; }
    /// <summary>DGII deadline: 72 hours after signing.</summary>
    public DateTime SubmitDeadlineUtc { get; set; }
    public string? TrackId { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>A row of the DGII taxpayer registry (DGII_RNC.TXT), to fill the buyer's name from an RNC or cédula.</summary>
internal sealed class Taxpayer
{
    public string Rnc { get; set; } = "";
    public string Name { get; set; } = "";
    public string? TradeName { get; set; }
    public bool IsActive { get; set; }
}

internal static class ENcf
{
    /// <summary>"E" + 2-digit type + 10-digit sequence, e.g. E320000000001.</summary>
    public static string Format(EcfType type, long number) =>
        $"E{(int)type:00}{number.ToString("D10", CultureInfo.InvariantCulture)}";
}

internal static class FiscalErrors
{
    public static Error NoSequence(EcfType type) =>
        Error.Conflict("Fiscal.NoSequence", $"No hay números e-NCF disponibles para {Name(type)}. Cargue un rango nuevo.");

    public static readonly Error OverlappingRange = Error.Conflict("Fiscal.OverlappingRange", "Ese rango se cruza con otro ya cargado.");
    public static readonly Error MissingIssuerRnc = Error.Conflict("Fiscal.MissingIssuerRnc", "Configure el RNC del negocio antes de emitir comprobantes.");
    public static readonly Error ReferenceRequired = Error.Validation("Fiscal.ReferenceRequired", "Una nota de crédito necesita el e-NCF que modifica.");
    public static readonly Error ReferenceNotFound = Error.Validation("Fiscal.ReferenceNotFound", "El e-NCF que modifica la nota no existe en este negocio.");
    public static readonly Error MissingIssuerAddress = Error.Conflict("Fiscal.MissingIssuerAddress", "Configure la dirección del negocio antes de emitir comprobantes electrónicos.");

    public static Error InvalidXml(string detail) =>
        Error.Validation("Fiscal.InvalidXml", $"El comprobante no cumple el formato de la DGII y no se firmó: {detail}");
    public static readonly Error RncRequired = Error.Validation("Fiscal.RncRequired", "El crédito fiscal necesita el RNC o la cédula del cliente.");

    public static string Name(EcfType type) => type switch
    {
        EcfType.FiscalCredit31 => "Factura de Crédito Fiscal Electrónica",
        EcfType.Consumer32 => "Factura de Consumo Electrónica",
        EcfType.DebitNote33 => "Nota de Débito Electrónica",
        _ => "Nota de Crédito Electrónica",
    };
}
