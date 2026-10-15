using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Shouldly;
using Toki.Fiscal;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.SharedKernel;
using static Toki.UnitTests.Fiscal.EcfXmlTests;

namespace Toki.UnitTests.Fiscal;

/// <summary>TK-145/147/148/150: the RFCE, the replacement e-CF and the ANECF validate against the official XSD; the QR per type and environment.</summary>
public sealed class DgiiXmlTests
{
    private static readonly CertificateEcfSigner Signer = new(new CertificateRequest("CN=Firmante de Prueba, SERIALNUMBER=00112345678", RSA.Create(2048),
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)));

    private static string? Value(XDocument xml, string name) => xml.Descendants(name).SingleOrDefault()?.Value;

    [Fact]
    public void The_RFCE_of_a_signed_32_copies_its_header_totals_and_payments_and_carries_its_security_code()
    {
        var doc = Document(EcfType.Consumer32, MixedLines, payments: [new FiscalPayment(EcfPaymentForm.Cash, 500m), new FiscalPayment(EcfPaymentForm.Card, 328.50m)]);
        var (ecf, code) = Signer.Sign(EcfXml.Build(doc, "E320000000001", Issuer, Now, null, null));

        var rfce = EcfXml.Rfce(ecf, code);
        EcfXml.ValidateUnsigned(rfce, EcfXml.RfceSchema).ShouldBeEmpty();
        var (signed, _) = Signer.Sign(rfce);
        EcfXml.Validate(XDocument.Parse(signed), EcfXml.RfceSchema).ShouldBeEmpty();

        var full = XDocument.Parse(ecf);
        foreach (var name in new[] { "eNCF", "RNCEmisor", "FechaEmision", "MontoTotal", "MontoExento", "TotalITBIS1", "TotalITBIS2", "MontoGravadoI1" })
            Value(rfce, name).ShouldBe(Value(full, name), name);
        Value(rfce, "CodigoSeguridadeCF").ShouldBe(code);
        rfce.Descendants("FormaDePago").Count().ShouldBe(2);
        rfce.Descendants("ITBIS1").ShouldBeEmpty(); // the rates are not in the summary
        rfce.Root!.Element("Encabezado")!.Element("Comprador")!.HasElements.ShouldBeFalse();
    }

    [Fact]
    public void A_replacement_keeps_the_data_and_takes_the_new_number_dates_and_expiry()
    {
        var doc = Document(EcfType.FiscalCredit31, MixedLines, rnc: "131245678", name: "Colmado El Sol SRL", payments: [new FiscalPayment(EcfPaymentForm.Cash, 828.50m)]);
        var (rejected, _) = Signer.Sign(EcfXml.Build(doc, "E310000000001", Issuer, Now, new DateOnly(2027, 12, 31), null));

        var later = Now.AddDays(2);
        var replacement = EcfXml.Reissue(rejected, "E310000000007", later, new DateOnly(2028, 12, 31));
        EcfXml.ValidateUnsigned(replacement, EcfType.FiscalCredit31).ShouldBeEmpty();
        replacement.Descendants().Any(e => e.Name.LocalName == "Signature").ShouldBeFalse();
        (Value(replacement, "eNCF"), Value(replacement, "FechaVencimientoSecuencia"), Value(replacement, "FechaEmision"))
            .ShouldBe(("E310000000007", "31-12-2028", "17-10-2026"));
        Value(replacement, "FechaHoraFirma").ShouldBe("17-10-2026 14:30:05");
        var original = XDocument.Parse(rejected);
        foreach (var name in new[] { "RNCComprador", "RazonSocialComprador", "MontoTotal", "TotalITBIS" })
            Value(replacement, name).ShouldBe(Value(original, name), name);
        replacement.Descendants("Item").Count().ShouldBe(3);
    }

    [Fact]
    public void The_ANECF_of_a_range_is_valid()
    {
        var anecf = EcfXml.Anecf("131245678", EcfType.Consumer32, 101, 250, Now);
        EcfXml.ValidateUnsigned(anecf, EcfXml.AnecfSchema).ShouldBeEmpty();
        EcfXml.Validate(XDocument.Parse(Signer.Sign(anecf).SignedXml), EcfXml.AnecfSchema).ShouldBeEmpty();
        (Value(anecf, "SecuenciaeNCFDesde"), Value(anecf, "SecuenciaeNCFHasta"), Value(anecf, "FechaHoraAnulacioneNCF"))
            .ShouldBe(("E320000000101", "E320000000250", "15-10-2026 14:30:05"));
        anecf.Descendants("CantidadeNCFAnulados").Select(e => e.Value).ShouldBe(["150", "150"]);
    }

    [Fact]
    public void The_QR_points_to_consultatimbre_or_consultatimbrefc_on_the_host_of_each_environment()
    {
        // 14:30:05 in Santo Domingo; the security code is Base64 and its + / = are escaped.
        EcfQr.Url(FiscalEnvironment.eCF, EcfChannel.Ecf, "131245678", "101672919", "E310000000001", 1180.5m, Now, "a+b/c=")
            .ShouldBe("https://ecf.dgii.gov.do/ecf/consultatimbre?RncEmisor=131245678&RncComprador=101672919&ENCF=E310000000001"
                + "&FechaEmision=15-10-2026&MontoTotal=1180.50&FechaFirma=15-10-2026%2014%3A30%3A05&CodigoSeguridad=a%2Bb%2Fc%3D");
        EcfQr.Url(FiscalEnvironment.CerteCF, EcfChannel.Rfce, "131245678", null, "E320000000001", 59m, Now, "Xy12Ab")
            .ShouldBe("https://fc.dgii.gov.do/certecf/consultatimbrefc?RncEmisor=131245678&ENCF=E320000000001&MontoTotal=59.00&CodigoSeguridad=Xy12Ab");
        // A 34 without a buyer leaves RncComprador out; a simulated receipt never points to production.
        EcfQr.Url(FiscalEnvironment.TesteCF, EcfChannel.Ecf, "131245678", null, "E340000000001", 59m, Now, "Xy12Ab").ShouldNotContain("RncComprador");
        EcfQr.Url(FiscalEnvironment.Simulated, EcfChannel.Ecf, "131245678", null, "E340000000001", 59m, Now, "Xy12Ab")
            .ShouldStartWith("https://ecf.dgii.gov.do/testecf/");
        BusinessDay.Offset.ShouldBe(TimeSpan.FromHours(-4));
    }
}
