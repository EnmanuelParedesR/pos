using System.Globalization;
using Abastalia.Fiscal.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal.Domain;

/// <summary>Where documents go: <c>Simulated</c> (nowhere, the default) or one of the three DGII environments.
/// Ranges and documents carry it: a number of one environment is never used in another, and only <c>eCF</c> is fiscal.</summary>
internal enum FiscalEnvironment { Simulated, TesteCF, CerteCF, eCF }

/// <summary>An authorized e-NCF range for one receipt type.</summary>
internal sealed class NcfSequence
{
    public Guid Id { get; set; }
    public EcfType Type { get; set; }
    public FiscalEnvironment Environment { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    /// <summary>Next number to assign. Moved only by an atomic update inside the sale's transaction, so a rollback leaves no gap.
    /// A void (ANECF) moves it past the voided numbers.</summary>
    public long Next { get; set; }
    /// <summary>Required for 31 and 33 (it goes in their XML); 32 and 34 may have none.</summary>
    public DateOnly? ExpiresOn { get; set; }
    public DateTime CreatedUtc { get; set; }

    public long Used => Next - From;
    public long Total => To - From + 1;
    public decimal PercentUsed => Total == 0 ? 100 : Math.Round(100m * Used / Total, 1);
}

/// <summary>Pending → (e-CF) Sent with a TrackId → Accepted / AcceptedConditional / Rejected; (RFCE) straight to the final state.
/// Simulated: signed by the stand-in signer or queued with the simulated provider; never sent to the DGII.
/// Voided (TK-205): signed but never sent, and voided with an ANECF instead; the queue never sends it.</summary>
internal enum SubmissionStatus { Pending, Sent, Accepted, AcceptedConditional, Rejected, Simulated, Voided }

/// <summary>How the DGII receives it: the whole e-CF (reception + TrackId) or, for a 32 under RD$250,000, the signed summary (RFCE).</summary>
internal enum EcfChannel { Ecf, Rfce }

/// <summary>A signed e-CF. Never deleted or edited: corrections are credit notes; one the DGII rejected is replaced by a new one (TK-147).</summary>
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
    /// <summary>When it was signed (TK-156: for one that waited for the clock, the issue time until it is signed).</summary>
    public DateTime SignedUtc { get; set; }
    /// <summary>Empty while <see cref="AwaitingSignature"/>.</summary>
    public string SecurityCode { get; set; } = "";
    /// <summary>TK-156: issued while the clock was off (<see cref="DgiiClock"/>): <see cref="SignedXml"/> holds the unsigned XML until the clock is
    /// fixed. Every signature has a security code, so an empty one marks it (queries use <c>SecurityCode == ""</c>).</summary>
    public bool AwaitingSignature => SecurityCode.Length == 0;
    /// <summary>GZip of the signed XML.</summary>
    public byte[] SignedXml { get; set; } = [];
    public SubmissionStatus Status { get; set; }
    /// <summary>DGII deadline: 72 hours after signing.</summary>
    public DateTime SubmitDeadlineUtc { get; set; }
    public string? TrackId { get; set; }
    /// <summary>Failed sends in a row, or result polls while Sent; drives the backoff.</summary>
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public string? LastError { get; set; }
    public FiscalEnvironment Environment { get; set; }
    public EcfChannel Channel { get; set; }
    /// <summary>GZip of the signed RFCE (32 under RD$250,000); the full e-CF stays in <see cref="SignedXml"/> for 10 years.</summary>
    public byte[]? RfceXml { get; set; }
    public DateOnly? SequenceExpiresOn { get; set; }
    /// <summary>The receipt was printed with the contingency legend (the DGII could not be reached at the time).</summary>
    public bool Contingency { get; set; }
    /// <summary>DGII code (0 not found, 1 accepted, 2 rejected, 3 in process, 4 accepted with conditions) and its messages.</summary>
    public int? DgiiCode { get; set; }
    public string? DgiiMessages { get; set; }
    /// <summary>With a rejection: false means the DGII did not consume the e-NCF.</summary>
    public bool? SequenceUsed { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime? ResultUtc { get; set; }
    /// <summary>When the cloud archive got the XML and the result (only with the DGII provider).</summary>
    public DateTime? ArchivedUtc { get; set; }
    /// <summary>TK-147: the rejected e-NCF this one replaces, and the replacement of a rejected one.</summary>
    public string? ReplacesENcf { get; set; }
    public string? ReplacedByENcf { get; set; }
}

/// <summary>An ANECF (TK-150): the unused numbers of a range, voided at the DGII. Signed when created (works offline), sent by the queue.</summary>
internal sealed class EcfVoid
{
    public Guid Id { get; set; }
    public Guid SequenceId { get; set; }
    public EcfType Type { get; set; }
    public FiscalEnvironment Environment { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>GZip of the signed ANECF.</summary>
    public byte[] SignedXml { get; set; } = [];
    /// <summary>Pending, Accepted, Rejected or Simulated.</summary>
    public SubmissionStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public string? Message { get; set; }
    public DateTime? ResultUtc { get; set; }
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
        Error.Conflict("Fiscal.NoSequence", $"No hay números e-NCF disponibles para {Name(type)}. El dueño los carga en Administración → Configuración → Comprobantes, con la secuencia que autorizó la DGII en la OFV.");

    public static readonly Error OverlappingRange = Error.Conflict("Fiscal.OverlappingRange", "Ese rango se cruza con otro ya cargado.");
    public static readonly Error MissingIssuerRnc = Error.Conflict("Fiscal.MissingIssuerRnc", "Configure el RNC del negocio antes de emitir comprobantes.");
    public static readonly Error ReferenceRequired = Error.Validation("Fiscal.ReferenceRequired", "Una nota de crédito necesita el e-NCF que modifica.");
    public static readonly Error ReferenceNotFound = Error.Validation("Fiscal.ReferenceNotFound", "El e-NCF que modifica la nota no existe en este negocio.");
    public static readonly Error MissingIssuerAddress = Error.Conflict("Fiscal.MissingIssuerAddress", "Configure la dirección del negocio antes de emitir comprobantes electrónicos.");
    public static readonly Error ExpiryRequired = Error.Validation("Fiscal.ExpiryRequired", "Las secuencias de crédito fiscal (31) y nota de débito (33) necesitan fecha de vencimiento.");
    public static readonly Error NotFound = Error.NotFound("Fiscal.NotFound", "El comprobante no existe.");
    public static readonly Error NotRejected = Error.Conflict("Fiscal.NotRejected", "Solo se reemplaza un comprobante rechazado por la DGII o anulado sin enviar.");
    public static readonly Error AlreadySent = Error.Conflict("Fiscal.AlreadySent",
        "Ese comprobante ya salió hacia la DGII: no se anula con un ANECF. Corríjalo con una nota de crédito.");
    public static readonly Error HasNotes = Error.Conflict("Fiscal.HasNotes", "Ese comprobante tiene notas de crédito o débito: anúlelas o corríjalas primero.");
    public static readonly Error DgiiUnreachable = Error.Conflict("Fiscal.DgiiUnreachable",
        "Un envío anterior pudo haber llegado y la DGII no responde para confirmarlo. Intente cuando haya conexión.");
    public static readonly Error CertificateUnreadable = Error.Validation("Fiscal.CertificateUnreadable",
        "No se pudo abrir el certificado. Revise que sea el archivo .p12 o .pfx y que la contraseña sea la correcta.");
    public static readonly Error CertificateWithoutKey = Error.Validation("Fiscal.CertificateWithoutKey",
        "Ese archivo no trae la llave privada (o no es RSA): pida a la entidad certificadora el .p12 completo.");
    public static readonly Error CertificateExpired = Error.Validation("Fiscal.CertificateExpired", "Ese certificado ya venció. Renuévelo con la entidad certificadora.");
    public static readonly Error CertificateNotYetValid = Error.Validation("Fiscal.CertificateNotYetValid",
        "Ese certificado todavía no es válido (su vigencia empieza más adelante). Revise la fecha de la PC o espere a que entre en vigencia.");
    public static Error CertificateWrongRnc(string certificate, string business) => Error.Validation("Fiscal.CertificateWrongRnc",
        $"Ese certificado es de {certificate} y el negocio es {business}: la DGII rechazaría todo lo firmado con él. Importe el certificado de este negocio.");
    public static readonly Error AlreadyReplaced = Error.Conflict("Fiscal.AlreadyReplaced", "Ese comprobante ya tiene un reemplazo.");
    public static readonly Error SequenceNotFound = Error.NotFound("Fiscal.SequenceNotFound", "La secuencia no existe en el ambiente actual.");
    public static readonly Error NothingToVoid = Error.Conflict("Fiscal.NothingToVoid", "Esa secuencia no tiene números sin usar para anular.");
    public static readonly Error InvalidVoidRange = Error.Validation("Fiscal.InvalidVoidRange", "La anulación va desde el próximo número sin usar hasta un número dentro del rango.");

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
