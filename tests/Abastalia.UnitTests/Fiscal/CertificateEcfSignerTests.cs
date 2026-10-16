using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using static Abastalia.UnitTests.Fiscal.EcfXmlTests;

namespace Abastalia.UnitTests.Fiscal;

/// <summary>TK-142: XMLDSig per the DGII "Firmado de e-CF", with a self-signed test certificate.</summary>
public sealed class CertificateEcfSignerTests
{
    private static readonly X509Certificate2 Certificate = CreateCertificate();

    // Subject SN = the signer's cédula, as the DGII requires of the real certificate.
    private static X509Certificate2 CreateCertificate()
    {
        var request = new CertificateRequest("CN=Firmante de Prueba, SERIALNUMBER=00112345678", RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static (string Xml, string Code) Sign(CertificateEcfSigner signer, EcfType type, DocumentToIssue doc) =>
        signer.Sign(EcfXml.Build(doc, $"E{(int)type}0000000001", Issuer, Now, new DateOnly(2028, 12, 31), null));

    private static XmlDocument Load(string xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
        doc.LoadXml(xml);
        return doc;
    }

    private static SignedXml Signature(XmlDocument doc)
    {
        var check = new SignedXml(doc);
        check.LoadXml((XmlElement)doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        return check;
    }

    public static TheoryData<EcfType> Types => [EcfType.Consumer32, EcfType.FiscalCredit31];

    private static DocumentToIssue DocumentOf(EcfType type) => type == EcfType.FiscalCredit31
        ? Document(type, MixedLines, rnc: "131245678", name: "Colmado El Sol SRL", payments: [new FiscalPayment(EcfPaymentForm.Cash, 828.50m)])
        : Document(type, MixedLines, payments: [new FiscalPayment(EcfPaymentForm.Cash, 828.50m)]);

    [Theory]
    [MemberData(nameof(Types))]
    public void The_signed_e_CF_verifies_has_its_security_code_and_still_passes_the_XSD(EcfType type)
    {
        var (xml, code) = Sign(new CertificateEcfSigner(Certificate), type, DocumentOf(type));

        var doc = Load(xml);
        var signature = Signature(doc);
        signature.CheckSignature(Certificate, verifySignatureOnly: true).ShouldBeTrue();
        signature.CheckSignature().ShouldBeTrue(); // with the certificate carried in KeyInfo/X509Data

        // The DGII layout: enveloped, last child of ECF, C14N, RSA-SHA256, SHA-256, Reference URI="".
        doc.DocumentElement!.LastChild!.LocalName.ShouldBe("Signature");
        signature.SignedInfo!.CanonicalizationMethod.ShouldBe("http://www.w3.org/TR/2001/REC-xml-c14n-20010315");
        signature.SignedInfo.SignatureMethod.ShouldBe("http://www.w3.org/2001/04/xmldsig-more#rsa-sha256");
        var reference = (Reference)signature.SignedInfo.References[0]!;
        (reference.Uri, reference.DigestMethod).ShouldBe(("", "http://www.w3.org/2001/04/xmlenc#sha256"));
        reference.TransformChain[0].ShouldBeOfType<XmlDsigEnvelopedSignatureTransform>();
        doc.GetElementsByTagName("X509Certificate", SignedXml.XmlDsigNamespaceUrl)[0]!.InnerText.ShouldBe(Convert.ToBase64String(Certificate.RawData));

        // Security code: first 6 characters of SignatureValue. The signing time is in the signed content.
        code.ShouldBe(doc.GetElementsByTagName("SignatureValue", SignedXml.XmlDsigNamespaceUrl)[0]!.InnerText[..6]);
        doc.GetElementsByTagName("FechaHoraFirma")[0]!.InnerText.ShouldBe("15-10-2026 14:30:05");

        EcfXml.Validate(XDocument.Parse(xml), type).ShouldBeEmpty();
    }

    [Fact]
    public void A_changed_amount_breaks_the_signature()
    {
        var (xml, _) = Sign(new CertificateEcfSigner(Certificate), EcfType.Consumer32, DocumentOf(EcfType.Consumer32));
        var doc = Load(xml);
        doc.GetElementsByTagName("MontoTotal")[0]!.InnerText = "1.00";
        Signature(doc).CheckSignature(Certificate, verifySignatureOnly: true).ShouldBeFalse();
    }

    [Fact]
    public void The_certificate_loads_from_a_PFX_file_for_development()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abastalia-ecf-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, Certificate.Export(X509ContentType.Pfx, "clave-de-prueba"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fiscal:Certificate:Path"] = path, ["Fiscal:Certificate:Password"] = "clave-de-prueba",
            }).Build();
            var signer = CertificateEcfSigner.FromConfiguration(config);
            signer.IsSimulated.ShouldBeFalse();
            var (xml, _) = Sign(signer, EcfType.Consumer32, DocumentOf(EcfType.Consumer32));
            Signature(Load(xml)).CheckSignature(Certificate, verifySignatureOnly: true).ShouldBeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Without_a_configured_certificate_it_does_not_start()
    {
        Should.Throw<InvalidOperationException>(() => CertificateEcfSigner.FromConfiguration(new ConfigurationBuilder().Build()));
    }
}
