using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>TK-142/143 inside IssueAsync: with a real signer the XML is validated before signing, and notes find the date of the e-NCF they modify.</summary>
public sealed class CertificateSigningTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static readonly X509Certificate2 Certificate = new CertificateRequest("CN=Firmante de Prueba, SERIALNUMBER=00112345678", RSA.Create(2048),
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    private static readonly FiscalLine[] Lines =
    [
        new("Res para guisar", 2, 210m, 20m, ItbisRate.General18, Itbis.Included(400m, ItbisRate.General18), 400m),
        new("Arroz selecto 1 lb", 1, 45m, 0m, ItbisRate.Exempt, 0m, 45m),
    ];

    private static DocumentToIssue Document(EcfType type, string? modified = null) => new(type, Guid.NewGuid(), null, null, Lines,
        Lines.Sum(l => l.Itbis), 445m, modified, modified is null ? null : ModificationCode.Void, Payments: [new FiscalPayment(EcfPaymentForm.Cash, 445m)]);

    private Task<(Result<IssuedDocument> Result, string? Xml)> IssueAsync(DocumentToIssue doc) => app.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<FiscalDbContext>();
        var issued = await new FiscalDocuments(db, sp.GetRequiredService<ISettingsQueries>(), new CertificateEcfSigner(Certificate),
            sp.GetRequiredService<IEcfProvider>(), sp.GetRequiredService<IOutbox>(), app.Clock, sp.GetRequiredService<DgiiClock>(), sp.GetRequiredService<DeviceSigning>()).IssueAsync(doc, CancellationToken.None);
        if (issued.IsFailure) return (issued, (string?)null);
        await db.SaveChangesAsync();
        var stored = db.Documents.Local.Single(d => d.ENcf == issued.Value.ENcf);
        stored.Status.ShouldBe(SubmissionStatus.Pending);
        using var gzip = new GZipStream(new MemoryStream(stored.SignedXml), CompressionMode.Decompress);
        return (issued, (string?)new StreamReader(gzip).ReadToEnd());
    });

    [Fact]
    public async Task A_real_signer_needs_the_address_signs_valid_XML_and_notes_carry_the_original_date()
    {
        await app.LoadSequencesAsync();
        (await IssueAsync(Document(EcfType.Consumer32))).Result.Error!.Code.ShouldBe("Fiscal.MissingIssuerAddress");

        var admin = app.Client(await app.AdminAsync());
        var business = await admin.GetFromJsonAsync<JsonElement>("/api/settings/business");
        (await admin.PutAsJsonAsync("/api/settings/business", new
        {
            Name = "Carnicería de prueba", Rnc = "131245678", Address = "Av. Duarte 25, Santo Domingo",
            DiscountCapPercent = business.GetProperty("discountCapPercent").GetDecimal(),
        })).EnsureSuccessStatusCode();

        var (sale, xml) = await IssueAsync(Document(EcfType.Consumer32));
        sale.IsSuccess.ShouldBeTrue();
        EcfXml.Validate(XDocument.Parse(xml!), EcfType.Consumer32).ShouldBeEmpty();
        var doc = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
        doc.LoadXml(xml!);
        var signature = new SignedXml(doc);
        signature.LoadXml((XmlElement)doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        signature.CheckSignature(Certificate, verifySignatureOnly: true).ShouldBeTrue();
        sale.Value.SecurityCode.ShouldBe(doc.GetElementsByTagName("SignatureValue", SignedXml.XmlDsigNamespaceUrl)[0]!.InnerText[..6]);

        var (note, noteXml) = await IssueAsync(Document(EcfType.CreditNote34, sale.Value.ENcf));
        note.IsSuccess.ShouldBeTrue();
        XDocument.Parse(noteXml!).Descendants("FechaNCFModificado").Single().Value
            .ShouldBe(BusinessDay.Of(sale.Value.SignedUtc).ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture));

        (await IssueAsync(Document(EcfType.CreditNote34, "E310000099999"))).Result.Error!.Code.ShouldBe("Fiscal.ReferenceNotFound");
    }
}

/// <summary>TK-251: each environment numbers from 1, so the same e-NCF lives once per environment. Own app: it issues in two environments.</summary>
public sealed class ENcfPerEnvironmentTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static readonly X509Certificate2 Certificate = new CertificateRequest("CN=Firmante de Prueba, SERIALNUMBER=00112345678", RSA.Create(2048),
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    private static readonly FiscalLine[] Lines = [new("Arroz selecto 1 lb", 1, 45m, 0m, ItbisRate.Exempt, 0m, 45m)];

    private Task<(string ENcf, IssuedIn IssuedIn)> SellAsync(FiscalEnvironment env) => app.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<FiscalDbContext>();
        var fiscal = new FiscalDocuments(db, sp.GetRequiredService<ISettingsQueries>(), new CertificateEcfSigner(Certificate),
            new TestEcfProvider { Environment = env }, sp.GetRequiredService<IOutbox>(), app.Clock, sp.GetRequiredService<DgiiClock>(), sp.GetRequiredService<DeviceSigning>());
        var issued = await fiscal.IssueAsync(new DocumentToIssue(EcfType.Consumer32, Guid.NewGuid(), null, null, Lines, 0m, 45m, null, null,
            Payments: [new FiscalPayment(EcfPaymentForm.Cash, 45m)]), CancellationToken.None);
        await db.SaveChangesAsync();
        return (issued.Value.ENcf, await fiscal.IssuedInAsync(issued.Value.ENcf, CancellationToken.None));
    });

    [Fact]
    public async Task A_sale_in_TesteCF_then_production_with_a_range_from_1_reuses_the_number()
    {
        await app.WithAddressAsync();
        (await SellAsync(FiscalEnvironment.TesteCF)).ShouldBe(("E320000000001", IssuedIn.Test));

        await app.InScopeAsync(sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            db.Sequences.Add(new NcfSequence
            {
                Id = Guid.CreateVersion7(), Type = EcfType.Consumer32, Environment = FiscalEnvironment.eCF, From = 1, To = 100, Next = 1, CreatedUtc = DateTime.UtcNow,
            });
            return db.SaveChangesAsync();
        });
        (await SellAsync(FiscalEnvironment.eCF)).ShouldBe(("E320000000001", IssuedIn.Production));
    }
}
