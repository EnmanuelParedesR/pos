using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Toki.BuildingBlocks.Persistence;
using Toki.Fiscal.Contracts;
using Toki.SharedKernel;

namespace Toki.Fiscal.Domain;

/// <summary>TK-157: a period in which the store cannot issue e-CF (no certificate, expired certificate, signing broken) and sells with paper
/// NCF of series B (Decreto 587-24 art. 41: declared in the OFV, at most 15 calendar days). After it ends, every B receipt is replaced by an
/// e-CF (<c>CodigoModificacion</c> 4) within 30 calendar days.</summary>
internal sealed class EcfContingency
{
    public const int MaxDays = 15;
    public const int RegularizeDays = 30;

    public Guid Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>A range of paper NCF (series B) the DGII authorized, loaded beforehand for a contingency.</summary>
internal sealed class PaperSequence
{
    public Guid Id { get; set; }
    public EcfType Type { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    public long Next { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>A B receipt issued during a contingency. <see cref="Data"/> keeps what the sale sent (JSON of <see cref="DocumentToIssue"/>) so
/// the regularizing e-CF carries the same buyer, lines, totals and payments.</summary>
internal sealed class PaperDocument
{
    public Guid Id { get; set; }
    public string Ncf { get; set; } = "";
    public EcfType Type { get; set; }
    public Guid ReferenceId { get; set; }
    public Guid ContingencyId { get; set; }
    public DateTime IssuedUtc { get; set; }
    public decimal Total { get; set; }
    public string Data { get; set; } = "";
    /// <summary>The e-CF that replaced it before the DGII (sent only to the DGII, never to the buyer).</summary>
    public string? RegularizedByENcf { get; set; }
    public DateTime? RegularizedUtc { get; set; }
}

/// <summary>TK-155: the owner's progress on one step of the DGII certification portal (the step list lives in the screen).</summary>
internal sealed class CertificationStep
{
    public int Step { get; set; }
    public bool Done { get; set; }
    public DateTime? DoneUtc { get; set; }
}

/// <summary>TK-155: one row of the DGII test set (the Excel of step 2), generated, signed and sent to CerteCF.</summary>
internal sealed class CertificationCase
{
    public Guid Id { get; set; }
    public int Row { get; set; }
    public string ENcf { get; set; } = "";
    public EcfType Type { get; set; }
    public EcfChannel Channel { get; set; }
    /// <summary>GZip of the signed e-CF, and of its RFCE when it goes by the summary.</summary>
    public byte[] SignedXml { get; set; } = [];
    public byte[]? RfceXml { get; set; }
    public string SecurityCode { get; set; } = "";
    /// <summary>Pending, Sent, Accepted, AcceptedConditional or Rejected.</summary>
    public SubmissionStatus Status { get; set; }
    public string? TrackId { get; set; }
    public string? Messages { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? ResultUtc { get; set; }
}

/// <summary>Paper NCF: <c>B</c> + 2-digit type (01 crédito fiscal, 02 consumo, 03 nota de débito, 04 nota de crédito) + 8-digit sequence.</summary>
internal static class PaperNcf
{
    public static string Format(EcfType type, long number) => $"B{Code(type)}{number.ToString("D8", CultureInfo.InvariantCulture)}";

    public static bool Is(string? ncf) => ncf is ['B', ..];

    private static string Code(EcfType type) => type switch
    {
        EcfType.FiscalCredit31 => "01",
        EcfType.Consumer32 => "02",
        EcfType.DebitNote33 => "03",
        _ => "04",
    };
}

internal static class OperationsErrors
{
    public static Error CannotSign(string why) => Error.Conflict("Fiscal.CannotSign",
        $"No se puede firmar el comprobante electrónico ({why}). Quien maneje los comprobantes puede activar la contingencia (serie B) en Comprobantes electrónicos.");
    public static Error NoPaperSequence(EcfType type) => Error.Conflict("Fiscal.NoPaperSequence",
        $"Contingencia activa y no quedan comprobantes serie B para {FiscalErrors.Name(type).Replace(" Electrónica", "", StringComparison.Ordinal)}. Cargue un rango B.");
    public static readonly Error CanStillSign = Error.Conflict("Fiscal.CanStillSign",
        "La tienda puede firmar comprobantes electrónicos: la serie B solo se usa cuando no se puede emitir e-CF.");
    public static readonly Error OverLimit = Error.Conflict("Fiscal.ContingencyOverLimit",
        $"La contingencia pasó el límite de {EcfContingency.MaxDays} días: ya no se pueden emitir comprobantes serie B. Resuelva el certificado digital para volver a emitir e-CF, o consulte con la DGII.");
    public static readonly Error AlreadyActive =Error.Conflict("Fiscal.ContingencyActive", "La contingencia ya está activa.");
    public static readonly Error NotActive = Error.Conflict("Fiscal.ContingencyNotActive", "No hay una contingencia activa.");
    public static readonly Error StillActive = Error.Conflict("Fiscal.ContingencyStillActive",
        "Termine la contingencia antes de regularizar: los e-CF de reemplazo se emiten cuando la tienda vuelve a firmar.");
    public static readonly Error NotCerteCF = Error.Conflict("Fiscal.NotCerteCF",
        "El set de pruebas se envía solo en el ambiente de Certificación (CerteCF). Cambie el ambiente de la DGII y vuelva a intentar.");
    public static readonly Error NoCertificate = Error.Conflict("Fiscal.NoCertificate", "Importe primero el certificado digital (Configuración → Comprobantes).");
    public static Error UnreadableFile(string why) => Error.Validation("Fiscal.UnreadableFile", $"No se pudo leer el archivo: {why}");
    public static readonly Error SignNotAllowed = Error.Validation("Fiscal.SignNotAllowed",
        "Ese archivo es un comprobante (e-CF, RFCE, ANECF o acuse): esos se firman solo desde la venta o la cola, no con esta herramienta.");
}

internal sealed class OperationsConfiguration : IEntityTypeConfiguration<EcfContingency>, IEntityTypeConfiguration<PaperSequence>,
    IEntityTypeConfiguration<PaperDocument>, IEntityTypeConfiguration<CertificationStep>, IEntityTypeConfiguration<CertificationCase>
{
    public void Configure(EntityTypeBuilder<EcfContingency> b)
    {
        b.ToTable("Contingencies");
        b.Property(c => c.Reason).HasMaxLength(500);
    }

    public void Configure(EntityTypeBuilder<PaperSequence> b)
    {
        b.ToTable("PaperSequences");
        b.Property(s => s.Type).HasConversion<int>();
    }

    public void Configure(EntityTypeBuilder<PaperDocument> b)
    {
        b.ToTable("PaperDocuments");
        b.Property(d => d.Ncf).HasMaxLength(11);
        b.HasIndex(d => d.Ncf).IsUnique();
        b.Property(d => d.Type).HasConversion<int>();
        b.Property(d => d.RegularizedByENcf).HasMaxLength(13);
        b.Property(d => d.Data).HasMaxLength(ModuleDbContext.LongText); // the whole document as JSON: lines included, far over the 200 default
    }

    public void Configure(EntityTypeBuilder<CertificationStep> b)
    {
        b.ToTable("CertificationSteps");
        b.HasKey(s => s.Step);
        b.Property(s => s.Step).ValueGeneratedNever();
    }

    public void Configure(EntityTypeBuilder<CertificationCase> b)
    {
        b.ToTable("CertificationCases");
        b.Property(c => c.ENcf).HasMaxLength(13);
        b.Property(c => c.Type).HasConversion<int>();
        b.Property(c => c.Channel).HasConversion<string>().HasMaxLength(10);
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(c => c.SecurityCode).HasMaxLength(6);
        b.Property(c => c.Messages).HasMaxLength(2000);
    }
}
