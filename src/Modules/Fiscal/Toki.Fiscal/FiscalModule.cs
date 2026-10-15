using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.Settings.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.SharedKernel;

namespace Toki.Fiscal;

public static class FiscalModule
{
    /// <summary><c>Fiscal:Provider</c> picks who sends the e-CF: <c>Simulated</c> (default, e-CF on hold) or <c>Relay</c> (the cloud relay, ADR-0008).
    /// <c>Fiscal:Signer</c> picks who signs: <c>Simulated</c> (default, no certificate) or <c>Certificate</c> (XMLDSig, see <see cref="CertificateEcfSigner"/>).</summary>
    public static IServiceCollection AddFiscalModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddModuleDbContext<FiscalDbContext>();
        services.AddScoped<IFiscalDocuments, FiscalDocuments>();
        _ = (config["Fiscal:Signer"] ?? "Simulated") switch
        {
            "Simulated" => services.AddSingleton<IEcfSigner, SimulatedEcfSigner>(),
            "Certificate" => services.AddSingleton<IEcfSigner>(_ => CertificateEcfSigner.FromConfiguration(config)),
            var other => throw new InvalidOperationException($"Unknown Fiscal:Signer '{other}': use Simulated or Certificate."),
        };
        _ = (config["Fiscal:Provider"] ?? "Simulated") switch
        {
            "Simulated" => services.AddSingleton<IEcfProvider, SimulatedEcfProvider>(),
            "Relay" => services.AddScoped<IEcfProvider, RelayEcfProvider>(),
            var other => throw new InvalidOperationException($"Unknown Fiscal:Provider '{other}': use Simulated or Relay."),
        };
        services.AddScoped<EcfSubmissionQueue>();
        services.AddHostedService<EcfSubmissionWorker>();
        services.AddHealthChecks().AddCheck<EcfQueueHealthCheck>("ecf-queue");
        return services;
    }
}

internal sealed class FiscalDbContext(DbContextOptions<FiscalDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "fiscal";

    public DbSet<NcfSequence> Sequences => Set<NcfSequence>();
    public DbSet<FiscalDocument> Documents => Set<FiscalDocument>();
    public DbSet<Taxpayer> Taxpayers => Set<Taxpayer>();
}

internal sealed class FiscalConfiguration : IEntityTypeConfiguration<NcfSequence>, IEntityTypeConfiguration<FiscalDocument>, IEntityTypeConfiguration<Taxpayer>
{
    public void Configure(EntityTypeBuilder<Taxpayer> b)
    {
        b.ToTable("Taxpayers");
        b.HasKey(c => c.Rnc);
        b.Property(c => c.Rnc).HasMaxLength(11);
        b.Property(c => c.Name).HasMaxLength(250);
        b.Property(c => c.TradeName).HasMaxLength(250);
    }

    public void Configure(EntityTypeBuilder<NcfSequence> b)
    {
        b.ToTable("EcfSequences");
        b.Property(s => s.Type).HasConversion<int>();
        b.Ignore(s => s.Used);
        b.Ignore(s => s.Total);
        b.Ignore(s => s.PercentUsed);
    }

    public void Configure(EntityTypeBuilder<FiscalDocument> b)
    {
        b.ToTable("EcfDocuments");
        b.Property(d => d.ENcf).HasMaxLength(13);
        b.HasIndex(d => d.ENcf).IsUnique();
        b.HasIndex(d => d.ReferenceId);
        b.HasIndex(d => new { d.Status, d.SubmitDeadlineUtc });
        b.Property(d => d.Type).HasConversion<int>();
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.SecurityCode).HasMaxLength(6);
        b.Property(d => d.ModifiedENcf).HasMaxLength(13);
        b.Property(d => d.BuyerRnc).HasMaxLength(11);
        b.Property(d => d.SignedXml).HasMaxLength(ModuleDbContext.LongText * 4);
        b.Property(d => d.LastError).HasMaxLength(2000);
    }
}

/// <summary>Signs the e-CF XML. Returns the signed document and its security code (first 6 characters of the signature).</summary>
internal interface IEcfSigner
{
    /// <summary>True for the stand-in signer: its documents are stored as Simulated and never sent.</summary>
    bool IsSimulated { get; }
    (string SignedXml, string SecurityCode) Sign(XDocument xml);
}

/// <summary>Stand-in while the store has no certificate (<c>Fiscal:Signer</c> = <c>Simulated</c>): a hash instead of XMLDSig.
/// Its documents are never sent; the real one is <see cref="CertificateEcfSigner"/>.</summary>
internal sealed class SimulatedEcfSigner : IEcfSigner
{
    public bool IsSimulated => true;

    public (string SignedXml, string SecurityCode) Sign(XDocument xml)
    {
        var signature = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting))));
        xml.Root!.Add(new XElement("SimulatedSignature", signature));
        return (xml.ToString(SaveOptions.DisableFormatting), signature[..6]);
    }
}

internal sealed class FiscalDocuments(FiscalDbContext db, ISettingsQueries settings, IEcfSigner signer, IOutbox outbox, TimeProvider clock)
    : IFiscalDocuments
{
    public static readonly TimeSpan SubmitDeadline = TimeSpan.FromHours(72);

    public async Task<Result<IssuedDocument>> IssueAsync(DocumentToIssue doc, CancellationToken ct)
    {
        if (doc.Type == EcfType.FiscalCredit31 && !Rnc.IsValid(doc.BuyerRnc)) return FiscalErrors.RncRequired;
        if (doc.Type == EcfType.Consumer32 && doc.Total >= EcfLimits.IdentifiedConsumer && !Rnc.IsValid(doc.BuyerRnc)) return FiscalErrors.RncRequired;
        if (doc.Type is EcfType.CreditNote34 or EcfType.DebitNote33 && (doc.ModifiedENcf is null || doc.ModificationCode is null))
            return FiscalErrors.ReferenceRequired;
        var business = await settings.GetBusinessProfileAsync(ct);
        if (!Rnc.IsValid(business.Rnc)) return FiscalErrors.MissingIssuerRnc;
        if (!signer.IsSimulated && string.IsNullOrWhiteSpace(business.Address)) return FiscalErrors.MissingIssuerAddress;

        DateOnly? modifiedIssuedOn = null;
        if (doc.ModifiedENcf is not null)
        {
            var original = await db.Documents.Where(d => d.ENcf == doc.ModifiedENcf).Select(d => (DateTime?)d.SignedUtc).FirstOrDefaultAsync(ct);
            if (original is null) return FiscalErrors.ReferenceNotFound;
            modifiedIssuedOn = BusinessDay.Of(original.Value);
        }
        // The 31 needs the buyer's name: the DGII registry has it when the cashier only typed the RNC.
        if (doc.BuyerName is null && doc.BuyerRnc is not null)
            doc = doc with { BuyerName = await db.Taxpayers.Where(t => t.Rnc == doc.BuyerRnc).Select(t => t.Name).FirstOrDefaultAsync(ct) };

        var now = clock.GetUtcNow().UtcDateTime;
        var reserved = await ReserveAsync(doc.Type, BusinessDay.Of(now), ct);
        if (reserved is not { } r) return FiscalErrors.NoSequence(doc.Type);
        var (number, expiresOn) = r;

        var encf = ENcf.Format(doc.Type, number);
        var unsigned = EcfXml.Build(doc, encf, business, now, expiresOn, modifiedIssuedOn);
        // Validated before signing: an XML the DGII would reject is neither signed nor uses up a number (the sale rolls back).
        // The simulated signer's documents are never sent, so they skip it (a store without an address can keep selling).
        if (!signer.IsSimulated && EcfXml.ValidateUnsigned(unsigned, doc.Type) is [var first, ..]) return FiscalErrors.InvalidXml(first);
        var (xml, code) = signer.Sign(unsigned);

        var document = new FiscalDocument
        {
            Id = Guid.CreateVersion7(), ENcf = encf, Type = doc.Type, ReferenceId = doc.ReferenceId, ModifiedENcf = doc.ModifiedENcf,
            BuyerRnc = doc.BuyerRnc, BuyerName = doc.BuyerName, Itbis = doc.Itbis, Total = doc.Total,
            SignedUtc = now, SecurityCode = code, SignedXml = Compress(xml), Status = signer.IsSimulated ? SubmissionStatus.Simulated : SubmissionStatus.Pending,
            SubmitDeadlineUtc = now + SubmitDeadline,
        };
        db.Documents.Add(document);
        outbox.Add(new FiscalDocumentIssuedV1(document.Id, encf, doc.Type, doc.ReferenceId, doc.Total, doc.Itbis));
        return new IssuedDocument(document.Id, encf, code, now, QrUrl(business.Rnc, doc, encf, now, code));
    }

    /// <summary>Takes the next number of the oldest valid range. The update locks the range row until commit; a rollback gives the number back.</summary>
    private async Task<(long Number, DateOnly ExpiresOn)?> ReserveAsync(EcfType type, DateOnly today, CancellationToken ct)
    {
        var candidates = await db.Sequences.Where(s => s.Type == type && s.Next <= s.To && s.ExpiresOn >= today)
            .OrderBy(s => s.From).Select(s => new { s.Id, s.ExpiresOn }).ToListAsync(ct);
        foreach (var c in candidates)
        {
            var rows = await db.Sequences.Where(s => s.Id == c.Id && s.Next <= s.To)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Next, x => x.Next + 1), ct);
            if (rows == 1) return (await db.Sequences.Where(s => s.Id == c.Id).Select(s => s.Next - 1).SingleAsync(ct), c.ExpiresOn);
        }
        return null;
    }

    private static string QrUrl(string? issuerRnc, DocumentToIssue doc, string encf, DateTime dateUtc, string code)
    {
        var local = dateUtc + BusinessDay.Offset;
        return "https://ecf.dgii.gov.do/ecf/ConsultaTimbre"
            + $"?RncEmisor={issuerRnc}&RncComprador={doc.BuyerRnc}&ENCF={encf}"
            + $"&FechaEmision={local:dd-MM-yyyy}&MontoTotal={doc.Total.ToString("0.00", CultureInfo.InvariantCulture)}"
            + $"&FechaFirma={Uri.EscapeDataString(local.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture))}&CodigoSeguridad={Uri.EscapeDataString(code)}";
    }

    private static byte[] Compress(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class FiscalDesignTimeFactory : Toki.BuildingBlocks.Persistence.DesignTimeFactory<FiscalDbContext>;
