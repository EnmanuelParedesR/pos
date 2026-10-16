using System.Globalization;
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
    /// <summary>Until the owner picks an environment on screen (TK-238, <see cref="FiscalMode"/>): <c>Fiscal:Signer</c> picks who signs: <c>Simulated</c> (default, no certificate) or <c>Certificate</c> (XMLDSig, see <see cref="CertificateEcfSigner"/>).
    /// <c>Fiscal:Provider</c> picks who receives: <c>Simulated</c> (default) or <c>Dgii</c> (the DGII web services, directly, in
    /// <c>Fiscal:Dgii:Environment</c> = <c>TesteCF</c> | <c>CerteCF</c> | <c>eCF</c>; needs the certificate signer). <c>Fiscal:Dgii:TimeoutSeconds</c> (30).</summary>
    public static IServiceCollection AddFiscalModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddModuleDbContext<FiscalDbContext>();
        services.AddScoped<IFiscalDocuments, FiscalDocuments>();
        services.AddScoped<FiscalDocuments>();
        services.AddSingleton<EcfCertificate>();
        services.AddSingleton<IEcfCertificateHolder>(sp => sp.GetRequiredService<EcfCertificate>());
        services.AddScoped<IFiscalReports, FiscalReports>();
        services.AddScoped<ITaxpayerRegistry, Features.TaxpayerNames>();
        var signer = config["Fiscal:Signer"] ?? "Simulated";
        if (signer is not ("Simulated" or "Certificate")) throw new InvalidOperationException($"Unknown Fiscal:Signer '{signer}': use Simulated or Certificate.");
        var configured = (config["Fiscal:Provider"] ?? "Simulated") switch
        {
            "Simulated" => FiscalEnvironment.Simulated,
            // The DGII authenticates with the seed signed by the certificate: there is no DGII without it.
            "Dgii" when signer != "Certificate" => throw new InvalidOperationException("Fiscal:Provider Dgii needs Fiscal:Signer Certificate."),
            "Dgii" => DgiiClient.UrlsFrom(config).Environment,
            var other => throw new InvalidOperationException($"Unknown Fiscal:Provider '{other}': use Simulated or Dgii."),
        };
        // TK-238: the environment chosen on screen (FiscalMode) wins over these settings, without a restart.
        services.AddSingleton<FiscalMode>();
        services.AddScoped<FiscalModeChecks>();
        services.AddHostedService<FiscalModeLoader>(); // before the workers: they read it
        services.AddSingleton(sp => new CertificateEcfSigner(sp.GetRequiredService<EcfCertificate>()));
        services.AddSingleton<IEcfSigner>(sp => new ModeEcfSigner(sp.GetRequiredService<FiscalMode>(),
            signer == "Certificate" ? sp.GetRequiredService<CertificateEcfSigner>() : new SimulatedEcfSigner(), sp.GetRequiredService<CertificateEcfSigner>()));
        services.AddHttpClient(DgiiClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(config.GetValue("Fiscal:Dgii:TimeoutSeconds", 30)));
        services.AddSingleton<IEcfProvider>(sp => new ModeEcfProvider(sp.GetRequiredService<FiscalMode>(), configured, env =>
            new DgiiClient(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<CertificateEcfSigner>(), new DgiiUrls(env), sp.GetRequiredService<TimeProvider>())));
        services.AddScoped<EcfSubmissionQueue>();
        services.AddScoped<IRestoreGuard, FiscalRestoreGuard>();
        services.AddSingleton<Features.TaxpayerImport>();
        services.AddHostedService<EcfSubmissionWorker>();
        EcfExchange.Register(services); // TK-153/154: 31 deliveries to buyers and received e-CF
        services.AddHealthChecks().AddCheck<EcfQueueHealthCheck>("ecf-queue");
        services.AddEcfOperations();
        return services;
    }
}

internal sealed class FiscalDbContext(DbContextOptions<FiscalDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "fiscal";

    public DbSet<NcfSequence> Sequences => Set<NcfSequence>();
    public DbSet<FiscalDocument> Documents => Set<FiscalDocument>();
    public DbSet<EcfVoid> Voids => Set<EcfVoid>();
    public DbSet<Taxpayer> Taxpayers => Set<Taxpayer>();
}

internal sealed class FiscalConfiguration : IEntityTypeConfiguration<NcfSequence>, IEntityTypeConfiguration<FiscalDocument>, IEntityTypeConfiguration<Taxpayer>,
    IEntityTypeConfiguration<EcfVoid>
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
        b.Property(s => s.Environment).HasConversion<string>().HasMaxLength(10);
        b.Ignore(s => s.Used);
        b.Ignore(s => s.Total);
        b.Ignore(s => s.PercentUsed);
    }

    public void Configure(EntityTypeBuilder<FiscalDocument> b)
    {
        b.ToTable("EcfDocuments");
        b.Property(d => d.ENcf).HasMaxLength(13);
        // TK-251: each environment numbers from 1 (a TesteCF E320000000001 and the eCF one are different documents).
        b.HasIndex(d => new { d.Environment, d.ENcf }).IsUnique();
        b.HasIndex(d => d.ReferenceId);
        b.HasIndex(d => new { d.Status, d.SubmitDeadlineUtc });
        b.Property(d => d.Type).HasConversion<int>();
        // R8 #2: the queue and a void never both win; the loser of a race fails instead of overwriting the other's status.
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20).IsConcurrencyToken();
        b.Property(d => d.SecurityCode).HasMaxLength(6);
        b.Property(d => d.ModifiedENcf).HasMaxLength(13);
        b.Property(d => d.BuyerRnc).HasMaxLength(11);
        b.Property(d => d.SignedXml).HasMaxLength(ModuleDbContext.LongText * 4);
        b.Property(d => d.LastError).HasMaxLength(2000);
        b.Property(d => d.Environment).HasConversion<string>().HasMaxLength(10);
        b.Property(d => d.Channel).HasConversion<string>().HasMaxLength(10);
        b.Property(d => d.RfceXml).HasMaxLength(ModuleDbContext.LongText);
        b.Property(d => d.DgiiMessages).HasMaxLength(2000);
        b.Property(d => d.ReplacesENcf).HasMaxLength(13);
        b.Property(d => d.ReplacedByENcf).HasMaxLength(13);
    }

    public void Configure(EntityTypeBuilder<EcfVoid> b)
    {
        b.ToTable("EcfVoids");
        b.Property(v => v.Type).HasConversion<int>();
        b.Property(v => v.Environment).HasConversion<string>().HasMaxLength(10);
        b.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(v => v.SignedXml).HasMaxLength(ModuleDbContext.LongText);
        b.Property(v => v.Message).HasMaxLength(2000);
    }
}

/// <summary>Signs the e-CF XML. Returns the signed document and its security code (first 6 characters of the signature).</summary>
internal interface IEcfSigner
{
    /// <summary>True for the stand-in signer: its documents are stored as Simulated and never sent.</summary>
    bool IsSimulated { get; }
    /// <summary>TK-157: why nothing can be signed right now, or null when it can.</summary>
    string? CannotSign(DateTime nowUtc) => null;
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

/// <summary>The printed QR (DGII "Representación Impresa"; docs/ecf-propio.md §5): <c>consultatimbre</c> with every field for a full e-CF,
/// <c>consultatimbrefc</c> with four for a 32 under RD$250,000, on the host of the environment. The security code is URL-escaped (Base64 has + / =).</summary>
internal static class EcfQr
{
    public static string Url(FiscalEnvironment env, EcfChannel channel, string? issuerRnc, string? buyerRnc, string encf, decimal total, DateTime signedUtc, string code)
    {
        // A simulated receipt points to the test environment: it must never look like a production document.
        var urls = new DgiiUrls(env == FiscalEnvironment.Simulated ? FiscalEnvironment.TesteCF : env);
        var local = signedUtc + BusinessDay.Offset;
        var amount = total.ToString("0.00", CultureInfo.InvariantCulture);
        if (channel == EcfChannel.Rfce)
            return $"{urls.Fc}/consultatimbrefc?RncEmisor={issuerRnc}&ENCF={encf}&MontoTotal={amount}&CodigoSeguridad={Uri.EscapeDataString(code)}";
        return $"{urls.Ecf}/consultatimbre?RncEmisor={issuerRnc}"
            + (string.IsNullOrEmpty(buyerRnc) ? "" : $"&RncComprador={buyerRnc}")
            + $"&ENCF={encf}&FechaEmision={local:dd-MM-yyyy}&MontoTotal={amount}"
            + $"&FechaFirma={Uri.EscapeDataString(local.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture))}&CodigoSeguridad={Uri.EscapeDataString(code)}";
    }
}

internal sealed class FiscalDocuments(FiscalDbContext db, ISettingsQueries settings, IEcfSigner signer, IEcfProvider provider, IOutbox outbox, TimeProvider clock)
    : IFiscalDocuments
{
    public static readonly TimeSpan SubmitDeadline = TimeSpan.FromHours(72);

    /// <summary>A document still unsent this long after signing means the queue cannot reach the DGII.</summary>
    public static readonly TimeSpan ContingencyAfter = TimeSpan.FromMinutes(5);

    /// <summary>Where this store's documents go now. Simulated when either the signer or the provider is the stand-in.</summary>
    public FiscalEnvironment Environment => signer.IsSimulated || provider.IsSimulated ? FiscalEnvironment.Simulated : provider.Environment;

    /// <summary>TK-157: why the store cannot sign an e-CF now (no certificate, or an expired one), or null when it can.</summary>
    public string? CannotSign() => signer.CannotSign(clock.GetUtcNow().UtcDateTime);

    public async Task<Result<IssuedDocument>> IssueAsync(DocumentToIssue doc, CancellationToken ct)
    {
        if (doc.Type == EcfType.FiscalCredit31 && !Rnc.IsValid(doc.BuyerRnc)) return FiscalErrors.RncRequired;
        if (doc.Type == EcfType.Consumer32 && doc.Total >= EcfLimits.IdentifiedConsumer && !Rnc.IsValid(doc.BuyerRnc)) return FiscalErrors.RncRequired;
        if (doc.Type is EcfType.CreditNote34 or EcfType.DebitNote33 && (doc.ModifiedENcf is null || doc.ModificationCode is null))
            return FiscalErrors.ReferenceRequired;
        var business = await settings.GetBusinessProfileAsync(ct);
        if (!Rnc.IsValid(business.Rnc)) return FiscalErrors.MissingIssuerRnc;
        // TK-157: series B only while the store cannot sign and the contingency is declared; never while it can sign.
        if (CannotSign() is { } why)
            return await db.Set<EcfContingency>().AnyAsync(c => c.EndedUtc == null, ct)
                ? await Features.PaperContingency.IssueAsync(db, doc, clock.GetUtcNow().UtcDateTime, ct)
                : OperationsErrors.CannotSign(why);
        if (!signer.IsSimulated && string.IsNullOrWhiteSpace(business.Address)) return FiscalErrors.MissingIssuerAddress;

        DateOnly? modifiedIssuedOn = null;
        if (doc.ModifiedENcf is not null)
        {
            var env = Environment;
            var original = await db.Documents.Where(d => d.Environment == env && d.ENcf == doc.ModifiedENcf).Select(d => (DateTime?)d.SignedUtc).FirstOrDefaultAsync(ct)
                ?? await db.Set<PaperDocument>().Where(d => d.Ncf == doc.ModifiedENcf).Select(d => (DateTime?)d.IssuedUtc).FirstOrDefaultAsync(ct);
            if (original is null) return FiscalErrors.ReferenceNotFound;
            modifiedIssuedOn = BusinessDay.Of(original.Value);
        }
        // The 31 needs the buyer's name: the DGII registry has it when the cashier only typed the RNC.
        if (doc.BuyerName is null && doc.BuyerRnc is not null)
            doc = doc with { BuyerName = await db.Taxpayers.Where(t => t.Rnc == doc.BuyerRnc).Select(t => t.Name).FirstOrDefaultAsync(ct) };

        var now = clock.GetUtcNow().UtcDateTime;
        var reserved = await ReserveAsync(doc.Type, BusinessDay.Of(now), ct);
        if (reserved is not { } r) return FiscalErrors.NoSequence(doc.Type);
        var encf = ENcf.Format(doc.Type, r.Number);

        var document = new FiscalDocument
        {
            Id = Guid.CreateVersion7(), ENcf = encf, Type = doc.Type, ReferenceId = doc.ReferenceId, ModifiedENcf = doc.ModifiedENcf,
            BuyerRnc = doc.BuyerRnc, BuyerName = doc.BuyerName, Itbis = doc.Itbis, Total = doc.Total, SequenceExpiresOn = r.ExpiresOn,
        };
        return await SignAndStoreAsync(document, EcfXml.Build(doc, encf, business, now, r.ExpiresOn, modifiedIssuedOn), business, now, ct);
    }

    /// <summary>TK-147: a document the DGII rejected is not corrected with a credit note (it never was valid): a new e-CF with the same data and a
    /// new e-NCF replaces it, linked both ways. Sales, voids and returns follow it through <see cref="FiscalDocumentReplacedV1"/>.</summary>
    /// <remarks>shortcut: always a new number, even when the DGII says the rejected one was not used (<c>secuenciaUtilizada</c> = false);
    /// that number can then be voided with an ANECF. TK-205: one voided before it was sent (<see cref="SubmissionStatus.Voided"/>) is replaced the same way.</remarks>
    public async Task<Result<IssuedDocument>> ReplaceAsync(Guid documentId, CancellationToken ct)
    {
        var rejected = await db.Documents.SingleOrDefaultAsync(d => d.Id == documentId, ct);
        if (rejected is null) return FiscalErrors.NotFound;
        if (rejected.Status is not (SubmissionStatus.Rejected or SubmissionStatus.Voided)) return FiscalErrors.NotRejected;
        if (rejected.ReplacedByENcf is not null) return FiscalErrors.AlreadyReplaced;
        var business = await settings.GetBusinessProfileAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var reserved = await ReserveAsync(rejected.Type, BusinessDay.Of(now), ct);
        if (reserved is not { } r) return FiscalErrors.NoSequence(rejected.Type);
        var encf = ENcf.Format(rejected.Type, r.Number);

        var replacement = new FiscalDocument
        {
            Id = Guid.CreateVersion7(), ENcf = encf, Type = rejected.Type, ReferenceId = rejected.ReferenceId, ModifiedENcf = rejected.ModifiedENcf,
            BuyerRnc = rejected.BuyerRnc, BuyerName = rejected.BuyerName, Itbis = rejected.Itbis, Total = rejected.Total, SequenceExpiresOn = r.ExpiresOn,
            ReplacesENcf = rejected.ENcf,
        };
        var issued = await SignAndStoreAsync(replacement, EcfXml.Reissue(EcfSubmissionQueue.Unzip(rejected.SignedXml), encf, now, r.ExpiresOn), business, now, ct);
        if (issued.IsFailure) return issued;
        rejected.ReplacedByENcf = encf;
        var i = issued.Value;
        outbox.Add(new FiscalDocumentReplacedV1(i.DocumentId, rejected.ENcf, encf, rejected.Type, rejected.ReferenceId, i.SecurityCode, i.QrUrl, i.SignedUtc,
            i.SequenceExpiresOn));
        return issued;
    }

    public async Task<IReadOnlySet<string>> UnreplacedENcfsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        (await db.Documents.Where(d => d.SignedUtc >= fromUtc && d.SignedUtc < toUtc && d.ReplacedByENcf == null
                && (d.Status == SubmissionStatus.Voided || d.Status == SubmissionStatus.Rejected))
            .Select(d => d.ENcf).ToListAsync(ct))
        // R9 #18: a B receipt of a contingency not regularized yet holds the 607 too.
        .Concat(await db.Set<PaperDocument>().Where(d => d.IssuedUtc >= fromUtc && d.IssuedUtc < toUtc && d.RegularizedByENcf == null)
            .Select(d => d.Ncf).ToListAsync(ct))
        .ToHashSet(StringComparer.Ordinal);

    public async Task<IssuedIn> IssuedInAsync(string ncf, CancellationToken ct)
    {
        if (PaperNcf.Is(ncf)) return IssuedIn.Production;
        // TK-251: the same e-NCF can exist once per environment: the current one first, else the newest.
        var current = Environment;
        var env = await db.Documents.Where(d => d.ENcf == ncf).OrderByDescending(d => d.Environment == current).ThenByDescending(d => d.SignedUtc)
            .Select(d => (FiscalEnvironment?)d.Environment).FirstOrDefaultAsync(ct);
        return env switch
        {
            FiscalEnvironment.eCF => IssuedIn.Production,
            FiscalEnvironment.TesteCF or FiscalEnvironment.CerteCF => IssuedIn.Test,
            _ => IssuedIn.Simulated,
        };
    }

    public async Task<IReadOnlySet<string>> TestENcfsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        (await db.Documents.Where(d => d.SignedUtc >= fromUtc && d.SignedUtc < toUtc
                && (d.Environment == FiscalEnvironment.TesteCF || d.Environment == FiscalEnvironment.CerteCF))
            .Select(d => d.ENcf).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Validates (before signing: an XML the DGII would reject is neither signed nor uses up a number, the caller rolls back), signs,
    /// builds and signs the RFCE of a 32 under RD$250,000, and stores the document for the queue.
    /// The simulated signer's documents are never sent, so they skip validation (a store without an address can keep selling).</summary>
    internal async Task<Result<IssuedDocument>> SignAndStoreAsync(FiscalDocument d, XDocument unsigned, BusinessProfile business, DateTime now, CancellationToken ct)
    {
        d.Channel = d.Type == EcfType.Consumer32 && d.Total < EcfLimits.IdentifiedConsumer ? EcfChannel.Rfce : EcfChannel.Ecf;
        if (!signer.IsSimulated && EcfXml.ValidateUnsigned(unsigned, d.Type) is [var first, ..]) return FiscalErrors.InvalidXml(first);
        var (xml, code) = signer.Sign(unsigned);
        if (d.Channel == EcfChannel.Rfce && !signer.IsSimulated)
        {
            var summary = EcfXml.Rfce(xml, code);
            if (EcfXml.ValidateUnsigned(summary, EcfXml.RfceSchema) is [var error, ..]) return FiscalErrors.InvalidXml(error);
            d.RfceXml = EcfSubmissionQueue.Zip(signer.Sign(summary).SignedXml);
        }

        d.Environment = Environment;
        d.Contingency = d.Environment != FiscalEnvironment.Simulated && await db.Documents.AnyAsync(p => p.Status == SubmissionStatus.Pending
            && (p.Attempts > 0 || p.SignedUtc < now - ContingencyAfter), ct);
        (d.SignedUtc, d.SecurityCode, d.SignedXml, d.SubmitDeadlineUtc) = (now, code, EcfSubmissionQueue.Zip(xml), now + SubmitDeadline);
        d.Status = signer.IsSimulated ? SubmissionStatus.Simulated : SubmissionStatus.Pending;
        db.Documents.Add(d);
        outbox.Add(new FiscalDocumentIssuedV1(d.Id, d.ENcf, d.Type, d.ReferenceId, d.Total, d.Itbis));
        var qr = EcfQr.Url(d.Environment, d.Channel, business.Rnc, d.BuyerRnc, d.ENcf, d.Total, now, code);
        // Printed on 31 and 33 only (the 32 and 34 do not carry it).
        var expiry = d.Type is EcfType.FiscalCredit31 or EcfType.DebitNote33 ? d.SequenceExpiresOn : null;
        return new IssuedDocument(d.Id, d.ENcf, code, now, qr, expiry, d.Contingency);
    }

    /// <summary>Takes the next number of the oldest valid range of the current environment. The update locks the range row until commit;
    /// a rollback gives the number back. In TesteCF the DGII range (1 to 10,000,000; the 32 to 50,000,000) is loaded on first use.</summary>
    internal async Task<(long Number, DateOnly? ExpiresOn)?> ReserveAsync(EcfType type, DateOnly today, CancellationToken ct)
    {
        var env = Environment;
        var candidates = await db.Sequences.Where(s => s.Type == type && s.Environment == env && s.Next <= s.To && (s.ExpiresOn == null || s.ExpiresOn >= today))
            .OrderBy(s => s.From).Select(s => new { s.Id, s.ExpiresOn }).ToListAsync(ct);
        if (candidates.Count == 0 && env == FiscalEnvironment.TesteCF && !await db.Sequences.AnyAsync(s => s.Type == type && s.Environment == env, ct))
        {
            // shortcut: two first sales at the same instant could both load it; harmless in a test environment.
            var range = new NcfSequence
            {
                Id = Guid.CreateVersion7(), Type = type, Environment = env, From = 1, To = type == EcfType.Consumer32 ? 50_000_000 : 10_000_000, Next = 1,
                ExpiresOn = type is EcfType.FiscalCredit31 or EcfType.DebitNote33 ? new DateOnly(2028, 12, 31) : null, CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.Sequences.Add(range);
            await db.SaveChangesAsync(ct);
            candidates.Add(new { range.Id, range.ExpiresOn });
        }
        foreach (var c in candidates)
        {
            var rows = await db.Sequences.Where(s => s.Id == c.Id && s.Next <= s.To)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Next, x => x.Next + 1), ct);
            if (rows == 1) return (await db.Sequences.Where(s => s.Id == c.Id).Select(s => s.Next - 1).SingleAsync(ct), c.ExpiresOn);
        }
        return null;
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class FiscalDesignTimeFactory : Toki.BuildingBlocks.Persistence.DesignTimeFactory<FiscalDbContext>;
