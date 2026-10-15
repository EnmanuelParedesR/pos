using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;

namespace Toki.Fiscal;

/// <summary>Real e-CF signature per the DGII "Firmado de e-CF": enveloped XMLDSig, C14N 1.0, RSA-SHA256, SHA-256 digest,
/// one <c>Reference URI=""</c> and the signer's certificate in <c>KeyInfo/X509Data</c>. Whitespace is not preserved, and once
/// signed the XML is never touched again.</summary>
internal sealed class CertificateEcfSigner(X509Certificate2 certificate) : IEcfSigner
{
    private readonly RSA key = certificate.GetRSAPrivateKey()
        ?? throw new InvalidOperationException($"The e-CF certificate {certificate.Thumbprint} has no RSA private key.");

    public bool IsSimulated => false;

    /// <summary><c>Fiscal:Certificate:Thumbprint</c> (LocalMachine\My, production) or, for development and tests,
    /// <c>Fiscal:Certificate:Path</c> + <c>Fiscal:Certificate:Password</c> (a .p12/.pfx file).</summary>
    /// <remarks>shortcut: the .p12 import screen (password typed once, non-exportable key, read access only for the service account)
    /// and the 30-day expiry warning are still to do (TK-142 remainder); today the certificate is installed by hand.</remarks>
    public static CertificateEcfSigner FromConfiguration(IConfiguration config)
    {
        if (config["Fiscal:Certificate:Thumbprint"] is { Length: > 0 } thumbprint)
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", "", StringComparison.Ordinal), validOnly: false);
            return found.Count == 1
                ? new CertificateEcfSigner(found[0])
                : throw new InvalidOperationException($"e-CF certificate {thumbprint} not found in LocalMachine\\My.");
        }
        if (config["Fiscal:Certificate:Path"] is { Length: > 0 } path)
            return new CertificateEcfSigner(X509CertificateLoader.LoadPkcs12FromFile(path, config["Fiscal:Certificate:Password"], X509KeyStorageFlags.EphemeralKeySet));
        throw new InvalidOperationException("Fiscal:Signer is Certificate: set Fiscal:Certificate:Thumbprint (or Path and Password for development).");
    }

    public (string SignedXml, string SecurityCode) Sign(XDocument xml)
    {
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
}
