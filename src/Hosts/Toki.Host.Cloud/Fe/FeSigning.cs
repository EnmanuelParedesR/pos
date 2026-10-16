using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Microsoft.EntityFrameworkCore;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Fe;

/// <summary>XML of the receiver services: hardened parsing (no DTD, no resolver, size cap), the DGII XSDs, the XMLDSig of the DGII
/// "Firmado de e-CF" and its verification.</summary>
internal static class FeXml
{
    public const int MaxBytes = 1_000_000;
    public const string Arecf = "ARECF v1.0.xsd";
    public const string Acecf = "ACECF v.1.0.xsd";

    private static readonly ConcurrentDictionary<string, XmlSchemaSet> Schemas = new();

    /// <summary>The DGII date and time (RD, UTC−4): dd-MM-yyyy HH:mm:ss.</summary>
    public static string DateTimeText(DateTime utc) => (utc + BusinessDay.Offset).ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Null when it is not well-formed XML, has a DTD or is too big.</summary>
    public static XmlDocument? Parse(string xml)
    {
        if (xml.Length > MaxBytes) return null;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc.Load(reader);
            return doc.DocumentElement is null ? null : doc;
        }
        catch (XmlException) { return null; }
    }

    /// <summary>The XSD of an e-CF type we have (31 to 34), or null for the others (41, 43…: not downloaded yet).</summary>
    public static string? EcfSchema(int type) => type is >= 31 and <= 34 ? $"e-CF {type} v.1.0.xsd" : null;

    /// <summary>XSD errors; empty when valid.</summary>
    public static IReadOnlyList<string> Validate(string xml, string schema)
    {
        var set = Schemas.GetOrAdd(schema, name =>
        {
            using var stream = typeof(FeXml).Assembly.GetManifestResourceStream($"Toki.Fe.Schemas.{name}")
                ?? throw new InvalidOperationException($"Missing embedded schema {name}.");
            var s = new XmlSchemaSet { XmlResolver = null };
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            s.Add(XmlSchema.Read(reader, null)!);
            s.Compile();
            return s;
        });
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = set, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        settings.ValidationEventHandler += (_, e) => { if (errors.Count < 5) errors.Add(e.Message); };
        lock (set) // like the store's EcfXml: one validation at a time per schema set
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(xml), settings);
                while (reader.Read()) { }
            }
            catch (XmlException e) { errors.Add(e.Message); }
        }
        return errors;
    }

    /// <summary>The issuer's signature as the DGII asks for it: one enveloped XMLDSig, last child of the root, RSA-SHA256, one <c>Reference URI=""</c>
    /// (the whole document, so nothing outside the signature can be swapped in), and valid with the certificate it carries.</summary>
    /// <remarks>shortcut: the certificate chain is not checked against the INDOTEL authorities (docs/ecf-propio.md §11, question 30).</remarks>
    public static bool SignatureIsValid(XmlDocument doc)
    {
        var signatures = doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        var last = doc.DocumentElement!.ChildNodes.OfType<XmlElement>().LastOrDefault();
        if (signatures.Count != 1 || last is null || !ReferenceEquals(last, signatures[0])) return false;
        try
        {
            var signed = new SignedXml(doc);
            signed.LoadXml(last);
            if (signed.SignedInfo is not { References.Count: 1 } info || ((Reference)info.References[0]!).Uri != ""
                || info.SignatureMethod != SignedXml.XmlDsigRSASHA256Url) return false;
            var certificate = signed.KeyInfo.OfType<KeyInfoX509Data>().SelectMany(d => d.Certificates?.OfType<X509Certificate2>() ?? []).FirstOrDefault();
            return certificate is not null && signed.CheckSignature(certificate, verifySignatureOnly: true);
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Same signature as the store's <c>CertificateEcfSigner</c> (enveloped, C14N 1.0, RSA-SHA256, SHA-256, <c>Reference URI=""</c>, X509 in KeyInfo).</summary>
    /// <remarks>shortcut: a copy of the store's 20 lines; the cloud only references module contracts.</remarks>
    public static string Sign(XDocument xml, X509Certificate2 certificate, RSA key)
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
        return doc.OuterXml;
    }

    public static string? Text(XmlDocument doc, string path) => doc.SelectSingleNode(path)?.InnerText.Trim() is { Length: > 0 } s ? s : null;

    public static byte[] Zip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    public static string Unzip(byte[] gzip)
    {
        using var z = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var reader = new StreamReader(z, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>TK-151: the cloud signer. It signs with a tenant's certificate in the vault, and <b>only</b> acknowledgements (ARECF), commercial approvals
/// (ACECF) and authentication seeds (SemillaModel): anything else (an e-CF, an RFCE, an ANECF) is refused here, in code, whoever asks.
/// Every signature is written to <see cref="FeSignature"/> (the panel shows them). A withdrawn consent stops it at the next signature.</summary>
internal sealed class FeSigner(CloudDbContext db, IFeCertificateVault vault, TimeProvider clock)
{
    public static readonly IReadOnlySet<string> AllowedRoots = new HashSet<string>(StringComparer.Ordinal) { "ARECF", "ACECF", "SemillaModel" };

    /// <returns>The signed XML, or null when the consent was withdrawn.</returns>
    /// <param name="keepXml">Keep the signed document in the log (the acknowledgements: they have no other home).</param>
    public async Task<string?> SignAsync(FeCertificate certificate, XDocument xml, string? encf, bool keepXml, CancellationToken ct)
    {
        var root = xml.Root ?? throw new InvalidOperationException("Empty document.");
        if (root.Name.Namespace != XNamespace.None || !AllowedRoots.Contains(root.Name.LocalName))
            throw new InvalidOperationException($"The cloud signer never signs a {root.Name.LocalName}: only ARECF, ACECF and SemillaModel.");
        if (!await db.FeCertificates.AnyAsync(c => c.Id == certificate.Id && c.RevokedUtc == null, ct)) return null;

        var (x509, key) = await vault.KeyAsync(certificate.KeyName, ct);
        string signed;
        using (x509)
        using (key)
            signed = FeXml.Sign(xml, x509, key);
        db.FeSignatures.Add(new FeSignature
        {
            TenantId = certificate.TenantId, CertificateId = certificate.Id, AtUtc = clock.GetUtcNow().UtcDateTime, Root = root.Name.LocalName, ENcf = encf,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signed))), XmlGzip = keepXml ? FeXml.Zip(signed) : null,
        });
        await db.SaveChangesAsync(ct); // with whatever the caller added: the received document and its acknowledgement go together
        return signed;
    }
}
