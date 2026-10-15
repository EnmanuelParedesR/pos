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
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.Fiscal;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.IntegrationTests.Infra;
using Toki.Settings.Contracts;
using Toki.SharedKernel;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>TK-142/143 inside IssueAsync: with a real signer the XML is validated before signing, and notes find the date of the e-NCF they modify.</summary>
public sealed class CertificateSigningTests(TokiApp app) : IClassFixture<TokiApp>
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
            sp.GetRequiredService<IEcfProvider>(), sp.GetRequiredService<IOutbox>(), app.Clock).IssueAsync(doc, CancellationToken.None);
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
