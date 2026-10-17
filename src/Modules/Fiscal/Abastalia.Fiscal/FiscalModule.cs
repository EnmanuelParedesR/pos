using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Abastalia.BuildingBlocks;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Settings.Contracts;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal;

public static class FiscalModule
{
    private static readonly string UserAgent =
        $"AbastaliaPOS/{typeof(FiscalModule).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} (+https://github.com/EnmanuelParedesR/pos)";

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
        // TK-323: Device = the Lite host. Each business's own device signs (DeviceSigned); the server holds no certificate.
        if (signer is not ("Simulated" or "Certificate" or "Device")) throw new InvalidOperationException($"Unknown Fiscal:Signer '{signer}': use Simulated, Certificate or Device.");
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
        services.AddScoped<DeviceSigning>();
        if (signer == "Device") services.AddHostedService<DeviceSignedArchiveWorker>();
        services.AddHostedService<FiscalModeLoader>(); // before the workers: they read it
        services.AddSingleton(sp => new CertificateEcfSigner(sp.GetRequiredService<EcfCertificate>()));
        services.AddSingleton<IEcfSigner>(sp => new ModeEcfSigner(sp.GetRequiredService<FiscalMode>(),
            signer == "Certificate" ? sp.GetRequiredService<CertificateEcfSigner>() : new SimulatedEcfSigner(), sp.GetRequiredService<CertificateEcfSigner>()));
        // TK-156: every DGII answer measures this PC's clock against the DGII's (DgiiClock); the same honest User-Agent as TK-252.
        services.AddSingleton<DgiiClock>();
        services.AddSingleton<IDgiiTime>(sp => sp.GetRequiredService<DgiiClock>()); // TK-279: the license clock
        services.AddTransient<DgiiClockHandler>();
        services.AddHttpClient(DgiiClient.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(config.GetValue("Fiscal:Dgii:TimeoutSeconds", 30));
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .AddHttpMessageHandler<DgiiClockHandler>();
        services.AddSingleton<IEcfProvider>(sp => new ModeEcfProvider(sp.GetRequiredService<FiscalMode>(), configured, env =>
            new DgiiClient(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<CertificateEcfSigner>(), new DgiiUrls(env), sp.GetRequiredService<TimeProvider>())));
        services.AddScoped<EcfSubmissionQueue>();
        services.AddScoped<IRestoreGuard, FiscalRestoreGuard>();
        services.AddSingleton<Features.TaxpayerImport>();
        // TK-252: the DGII public pages, with an honest User-Agent and short timeouts; the cookies of each lookup travel by hand.
        services.AddHttpClient(Features.DgiiPublicSite.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(config.GetValue("Fiscal:DgiiPublic:TimeoutSeconds", 6));
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
        services.AddSingleton<IDgiiPublicLookup, Features.DgiiPublicSite>();
        services.AddHostedService<EcfSubmissionWorker>();
        EcfExchange.Register(services); // TK-153/154: 31 deliveries to buyers and received e-CF
        services.AddHealthChecks().AddCheck<EcfQueueHealthCheck>("ecf-queue");
        services.AddEcfOperations();
        return services;
    }

    /// <summary>TK-365: the DGII web services at <paramref name="server"/> (the UI tests' fake DGII) instead of the real hosts:
    /// https://ecf.dgii.gov.do/testecf/x goes to {server}/ecf.dgii.gov.do/testecf/x. The local host calls it only in the E2E environment.</summary>
    public static IServiceCollection UseTestDgii(this IServiceCollection services, Uri server)
    {
        services.AddHttpClient(DgiiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new TestDgiiHandler(server));
        return services;
    }

    private sealed class TestDgiiHandler(Uri server) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(server, request.RequestUri!.Host + request.RequestUri.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

internal sealed class FiscalDbContext(DbContextOptions<FiscalDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "fiscal";

    public DbSet<NcfSequence> Sequences => Set<NcfSequence>();
    public DbSet<FiscalDocument> Documents => Set<FiscalDocument>();
    public DbSet<EcfVoid> Voids => Set<EcfVoid>();
    public DbSet<Taxpayer> Taxpayers => Set<Taxpayer>();
    public DbSet<Features.DgiiPublicAnswer> DgiiPublicAnswers => Set<Features.DgiiPublicAnswer>();
}

internal sealed class FiscalConfiguration : IEntityTypeConfiguration<NcfSequence>, IEntityTypeConfiguration<FiscalDocument>, IEntityTypeConfiguration<Taxpayer>,
    IEntityTypeConfiguration<EcfVoid>, IEntityTypeConfiguration<Features.DgiiPublicAnswer>
{
    public void Configure(EntityTypeBuilder<Features.DgiiPublicAnswer> b)
    {
        b.ToTable("DgiiPublicAnswers");
        b.HasKey(a => a.Key);
        b.Property(a => a.Key).HasMaxLength(40);
        b.Property(a => a.Answer).HasMaxLength(2000);
        b.HasIndex(a => a.ExpiresUtc);
    }

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
        // R12 #5: deliveries and the health list read by result date; the archive reads what is not archived yet.
        b.HasIndex(d => d.ResultUtc);
        b.HasIndex(d => d.ArchivedUtc);
        b.Property(d => d.Type).HasConversion<int>();
        // R8 #2: the queue and a void never both win; the loser of a race fails instead of overwriting the other's status.
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(30).IsConcurrencyToken();
        b.Property(d => d.SecurityCode).HasMaxLength(6);
        b.Ignore(d => d.AwaitingSignature);
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
        b.Property(v => v.Status).HasConversion<string>().HasMaxLength(30);
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
    /// <param name="issuedOn">The XML's <c>FechaEmision</c> (dd-MM-yyyy). R12 #6: not the signing day, which differs for one signed after a clock fix (TK-156).</param>
    public static string Url(FiscalEnvironment env, EcfChannel channel, string? issuerRnc, string? buyerRnc, string encf, decimal total, string issuedOn,
        DateTime signedUtc, string code)
    {
        // A simulated receipt points to the test environment: it must never look like a production document.
        var urls = new DgiiUrls(env == FiscalEnvironment.Simulated ? FiscalEnvironment.TesteCF : env);
        var local = signedUtc + BusinessDay.Offset;
        var amount = total.ToString("0.00", CultureInfo.InvariantCulture);
        if (channel == EcfChannel.Rfce)
            return $"{urls.Fc}/consultatimbrefc?RncEmisor={issuerRnc}&ENCF={encf}&MontoTotal={amount}&CodigoSeguridad={Uri.EscapeDataString(code)}";
        return $"{urls.Ecf}/consultatimbre?RncEmisor={issuerRnc}"
            + (string.IsNullOrEmpty(buyerRnc) ? "" : $"&RncComprador={buyerRnc}")
            + $"&ENCF={encf}&FechaEmision={issuedOn}&MontoTotal={amount}"
            + $"&FechaFirma={Uri.EscapeDataString(local.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture))}&CodigoSeguridad={Uri.EscapeDataString(code)}";
    }
}

internal sealed class FiscalDocuments(FiscalDbContext db, ISettingsQueries settings, IEcfSigner signer, IEcfProvider provider, IOutbox outbox, TimeProvider clock,
    DgiiClock dgiiClock, DeviceSigning device) : IFiscalDocuments
{
    public static readonly TimeSpan SubmitDeadline = TimeSpan.FromHours(72);

    /// <summary>A document still unsent this long after signing means the queue cannot reach the DGII.</summary>
    public static readonly TimeSpan ContingencyAfter = TimeSpan.FromMinutes(5);

    /// <summary>Where this store's documents go now. Simulated when either the signer or the provider is the stand-in.
    /// TK-323: in the Lite host, the business's own choice (<see cref="DeviceSigning.Environment"/>).</summary>
    public FiscalEnvironment Environment => device.Enabled ? device.Environment
        : signer.IsSimulated || provider.IsSimulated ? FiscalEnvironment.Simulated : provider.Environment;

    /// <summary>TK-323: this business's device signs its documents (the Lite host, out of Simulated): they are stored unsigned.</summary>
    internal bool DeviceSigns => device.Enabled && Environment != FiscalEnvironment.Simulated;

    /// <summary>TK-157: why the store cannot sign an e-CF now (no certificate, or an expired one), or null when it can.</summary>
    public string? CannotSign() => signer.CannotSign(clock.GetUtcNow().UtcDateTime);

    /// <summary>TK-156: this PC's clock is too far from the DGII's to sign (D17). A sale still goes on (its e-CF waits unsigned); the owner's
    /// actions that sign (replace, regularize, void) refuse. Never while simulated: nothing reaches the DGII.</summary>
    public string? ClockProblem => Environment == FiscalEnvironment.Simulated ? null : dgiiClock.Problem;

    public async Task<Result<IssuedDocument>> IssueAsync(DocumentToIssue doc, CancellationToken ct)
    {
        if (doc.Type == EcfType.FiscalCredit31 && !Rnc.IsValid(doc.BuyerRnc)) return FiscalErrors.RncRequired;
        if (doc.Type == EcfType.Consumer32 && doc.Total >= EcfLimits.IdentifiedConsumer && !Rnc.IsValid(doc.BuyerRnc)) return FiscalErrors.RncRequired;
        if (doc.Type is EcfType.CreditNote34 or EcfType.DebitNote33 && (doc.ModifiedENcf is null || doc.ModificationCode is null))
            return FiscalErrors.ReferenceRequired;
        var business = await settings.GetBusinessProfileAsync(ct);
        if (!Rnc.IsValid(business.Rnc)) return FiscalErrors.MissingIssuerRnc;
        if (business.LicensedRnc is { } licensed && licensed != business.Rnc) return FiscalErrors.RncNotLicensed(licensed);
        // TK-157: series B only while the store cannot sign and the contingency is declared; never while it can sign.
        if (CannotSign() is { } why)
            return await db.Set<EcfContingency>().AnyAsync(c => c.EndedUtc == null, ct)
                ? await Features.PaperContingency.IssueAsync(db, doc, clock.GetUtcNow().UtcDateTime, ct)
                : OperationsErrors.CannotSign(why);
        if ((!signer.IsSimulated || DeviceSigns) && string.IsNullOrWhiteSpace(business.Address)) return FiscalErrors.MissingIssuerAddress;

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
        if (ClockProblem is { } clockOff) return OperationsErrors.ClockOff(clockOff);
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
        // R12 #3: a second "Reemplazar" that read the row before this one finds it taken (0 rows) and rolls back its e-CF and number.
        // R12 #8: the cloud archive gets the link too (ArchivedUtc null: uploaded again).
        if (await db.Documents.Where(d => d.Id == rejected.Id && d.ReplacedByENcf == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReplacedByENcf, encf).SetProperty(d => d.ArchivedUtc, (DateTime?)null), ct) != 1)
            return FiscalErrors.AlreadyReplaced;
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

    public async Task<bool> HasIssuedInProductionAsync(CancellationToken ct) =>
        await db.Documents.AnyAsync(d => d.Environment == FiscalEnvironment.eCF, ct) || await db.Set<PaperDocument>().AnyAsync(ct);

    public async Task<IReadOnlySet<string>> TestENcfsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        (await db.Documents.Where(d => d.SignedUtc >= fromUtc && d.SignedUtc < toUtc
                && (d.Environment == FiscalEnvironment.TesteCF || d.Environment == FiscalEnvironment.CerteCF))
            .Select(d => d.ENcf).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Validates (before signing: an XML the DGII would reject is neither signed nor uses up a number, the caller rolls back), signs,
    /// builds and signs the RFCE of a 32 under RD$250,000, and stores the document for the queue.
    /// The simulated signer's documents are never sent, so they skip validation (a store without an address can keep selling).
    /// TK-156: with the clock off (<see cref="ClockProblem"/>) the validated XML is stored unsigned, the receipt goes without security code nor QR
    /// and with the contingency legend, and <see cref="SignWaitingAsync"/> signs it once the clock is fixed.</summary>
    internal async Task<Result<IssuedDocument>> SignAndStoreAsync(FiscalDocument d, XDocument unsigned, BusinessProfile business, DateTime now, CancellationToken ct)
    {
        d.Channel = d.Type == EcfType.Consumer32 && d.Total < EcfLimits.IdentifiedConsumer ? EcfChannel.Rfce : EcfChannel.Ecf;
        var byDevice = DeviceSigns;
        if ((!signer.IsSimulated || byDevice) && EcfXml.ValidateUnsigned(unsigned, d.Type) is [var first, ..]) return FiscalErrors.InvalidXml(first);
        var waiting = ClockProblem is not null;
        // TK-323: the device signs exactly these bytes (DeviceSigned checks the signature covers them, unchanged).
        if (waiting || byDevice) d.SignedXml = EcfSubmissionQueue.Zip(unsigned.ToString(SaveOptions.DisableFormatting));
        else if (Sign(d, unsigned) is { } error) return error;

        d.Environment = Environment;
        d.Contingency = waiting || (d.Environment != FiscalEnvironment.Simulated && await db.Documents.AnyAsync(p => p.Status == SubmissionStatus.Pending
            && (p.Attempts > 0 || p.SignedUtc < now - ContingencyAfter), ct));
        (d.SignedUtc, d.SubmitDeadlineUtc) = (now, now + SubmitDeadline);
        d.Status = byDevice ? SubmissionStatus.AwaitingDeviceSignature : signer.IsSimulated ? SubmissionStatus.Simulated : SubmissionStatus.Pending;
        db.Documents.Add(d);
        outbox.Add(new FiscalDocumentIssuedV1(d.Id, d.ENcf, d.Type, d.ReferenceId, d.Total, d.Itbis));
        // Without a signature there is no security code: the sale gets it and its QR from FiscalDocumentSignedV1.
        var qr = waiting || byDevice ? "" : Qr(d, business, unsigned);
        // Printed on 31 and 33 only (the 32 and 34 do not carry it).
        var expiry = d.Type is EcfType.FiscalCredit31 or EcfType.DebitNote33 ? d.SequenceExpiresOn : null;
        return new IssuedDocument(d.Id, d.ENcf, d.SecurityCode, now, qr, expiry, d.Contingency);
    }

    /// <summary>Signs <paramref name="unsigned"/> into <paramref name="d"/> (XML, security code and, for a 32 under RD$250,000, the signed RFCE).</summary>
    private Error? Sign(FiscalDocument d, XDocument unsigned)
    {
        var (xml, code) = signer.Sign(unsigned);
        if (d.Channel == EcfChannel.Rfce && !signer.IsSimulated)
        {
            var summary = EcfXml.Rfce(xml, code);
            if (EcfXml.ValidateUnsigned(summary, EcfXml.RfceSchema) is [var error, ..]) return FiscalErrors.InvalidXml(error);
            d.RfceXml = EcfSubmissionQueue.Zip(signer.Sign(summary).SignedXml);
        }
        (d.SecurityCode, d.SignedXml) = (code, EcfSubmissionQueue.Zip(xml));
        return null;
    }

    internal static string Qr(FiscalDocument d, BusinessProfile business, XDocument xml) =>
        EcfQr.Url(d.Environment, d.Channel, business.Rnc, d.BuyerRnc, d.ENcf, d.Total, EcfXml.IssuedOn(xml), d.SignedUtc, d.SecurityCode);

    /// <summary>TK-156: once the clock is right again, signs the e-CF that waited for it (<c>FechaHoraFirma</c> = now; the issue date stays the
    /// sale's) and gives the 72 hours from this signature. Each sale gets its code and QR through <see cref="FiscalDocumentSignedV1"/>.</summary>
    /// <returns>How many were signed.</returns>
    public async Task<int> SignWaitingAsync(CancellationToken ct)
    {
        if (ClockProblem is not null || CannotSign() is not null) return 0;
        var waiting = await db.Documents.Where(d => d.Status == SubmissionStatus.Pending && d.SecurityCode == "").OrderBy(d => d.SignedUtc).ToListAsync(ct);
        if (waiting.Count == 0) return 0;
        var business = await settings.GetBusinessProfileAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var d in waiting)
        {
            var xml = XDocument.Parse(EcfSubmissionQueue.Unzip(d.SignedXml));
            xml.Root!.Element("FechaHoraFirma")!.Value = (now + BusinessDay.Offset).ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            // The XML passed its XSD at the sale; an RFCE that fails its own stays waiting with the reason for the owner.
            if (Sign(d, xml) is { } error)
            {
                d.LastError = error.Description;
                continue;
            }
            (d.SignedUtc, d.SubmitDeadlineUtc, d.LastError) = (now, now + SubmitDeadline, null);
            outbox.Add(new FiscalDocumentSignedV1(d.Id, d.ENcf, d.ReferenceId, d.SecurityCode, Qr(d, business, xml), now));
        }
        return waiting.Count(d => !d.AwaitingSignature);
    }

    /// <summary>Environments whose ranges load themselves on first use: no owner step and no alert for a type never used there.</summary>
    internal static bool SelfLoads(FiscalEnvironment env) => env is FiscalEnvironment.Simulated or FiscalEnvironment.TesteCF;

    /// <summary>Takes the next number of the oldest valid range of the current environment. The update locks the range row until commit;
    /// a rollback gives the number back. In TesteCF the DGII range (1 to 10,000,000; the 32 to 50,000,000) is loaded on first use; TK-363: in
    /// Simulated too (nothing leaves the store), so a new store sells from day one and the owner loads real ranges only to go to CerteCF or production.</summary>
    internal async Task<(long Number, DateOnly? ExpiresOn)?> ReserveAsync(EcfType type, DateOnly today, CancellationToken ct)
    {
        var env = Environment;
        var candidates = await db.Sequences.Where(s => s.Type == type && s.Environment == env && s.Next <= s.To && (s.ExpiresOn == null || s.ExpiresOn >= today))
            .OrderBy(s => s.From).Select(s => new { s.Id, s.ExpiresOn }).ToListAsync(ct);
        if (candidates.Count == 0 && SelfLoads(env) && !await db.Sequences.AnyAsync(s => s.Type == type && s.Environment == env, ct))
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
internal sealed class FiscalDesignTimeFactory : Abastalia.BuildingBlocks.Persistence.DesignTimeFactory<FiscalDbContext>;
