using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;

namespace Toki.Fiscal;

/// <summary>Real e-CF signature per the DGII "Firmado de e-CF": enveloped XMLDSig, C14N 1.0, RSA-SHA256, SHA-256 digest,
/// one <c>Reference URI=""</c> and the signer's certificate in <c>KeyInfo/X509Data</c>. Whitespace is not preserved, and once
/// signed the XML is never touched again. It signs with whatever certificate is current, so an imported renewal (TK-176) is used at once.</summary>
internal sealed class CertificateEcfSigner : IEcfSigner
{
    private readonly Func<X509Certificate2> _certificate;

    public CertificateEcfSigner(X509Certificate2 certificate)
    {
        using var key = KeyOf(certificate); // fails now, not at the first sale
        _certificate = () => certificate;
    }

    public CertificateEcfSigner(EcfCertificate certificate) =>
        _certificate = () => certificate.Current
            ?? throw new InvalidOperationException(certificate.Problem ?? "No e-CF certificate: import it in Configuración → Comprobantes.");

    public bool IsSimulated => false;

    /// <summary>For tests and tools: the certificate of <see cref="EcfCertificate.LoadConfigured"/>, or an exception when none is configured.</summary>
    public static CertificateEcfSigner FromConfiguration(IConfiguration config) =>
        new(EcfCertificate.LoadConfigured(config)
            ?? throw new InvalidOperationException("Fiscal:Signer is Certificate: set Fiscal:Certificate:Thumbprint (or Path and Password for development)."));

    public (string SignedXml, string SecurityCode) Sign(XDocument xml)
    {
        var certificate = _certificate();
        using var key = KeyOf(certificate);
        var doc = new XmlDocument { PreserveWhitespace = false };
        using (var reader = xml.CreateReader()) doc.Load(reader);

        var signed = new SignedXml(doc) { SigningKey = key };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference("") { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(certificate));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();

        doc.DocumentElement!.AppendChild(doc.ImportNode(signed.GetXml(), true));
        doc.InsertBefore(doc.CreateXmlDeclaration("1.0", "utf-8", null), doc.DocumentElement);
        // Security code: the first 6 characters of SignatureValue (Base64), printed under the QR.
        return (doc.OuterXml, Convert.ToBase64String(signed.SignatureValue!)[..6]);
    }

    private static RSA KeyOf(X509Certificate2 certificate) =>
        certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException($"The e-CF certificate {certificate.Thumbprint} has no RSA private key.");
}
