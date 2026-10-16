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
    public static XmlDocument? Parse(string xml) => xml.Length > MaxBytes ? null : Parse(settings => XmlReader.Create(new StringReader(xml), settings));

    /// <summary>The bytes as they came: the encoding is the one the document declares (R9 #17).</summary>
    public static XmlDocument? Parse(byte[] xml) => xml.Length > MaxBytes ? null : Parse(settings => XmlReader.Create(new MemoryStream(xml), settings));

    private static XmlDocument? Parse(Func<XmlReaderSettings, XmlReader> open)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = open(settings);
            doc.Load(reader);
            return doc.DocumentElement is null ? null : doc;
        }
        catch (XmlException) { return null; }
    }

    /// <summary>The XSD of an e-CF type we have (31 to 34), or null for the others (41, 43…: not downloaded yet).</summary>
    public static string? EcfSchema(int type) => type is >= 31 and <= 34 ? $"e-CF {type} v.1.0.xsd" : null;

    /// <summary>XSD errors; empty when valid.</summary>
    public static IReadOnlyList<string> Validate(string xml, string schema) => Validate(settings => XmlReader.Create(new StringReader(xml), settings), schema);

    public static IReadOnlyList<string> Validate(byte[] xml, string schema) => Validate(settings => XmlReader.Create(new MemoryStream(xml), settings), schema);

    /// <remarks>R9 #2: no lock. A compiled <see cref="XmlSchemaSet"/> is only read while validating, so requests validate in parallel.</remarks>
    private static List<string> Validate(Func<XmlReaderSettings, XmlReader> open, string schema)
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
        try
        {
            using var reader = open(settings);
            while (reader.Read()) { }
        }
        catch (XmlException e) { errors.Add(e.Message); }
        return errors;
    }

    /// <summary>The certificate that made the signature as the DGII asks for it: one enveloped XMLDSig, last child of the root, RSA-SHA256, one
    /// <c>Reference URI=""</c> (the whole document, so nothing outside the signature can be swapped in), valid with a certificate it carries.
    /// Null when there is no such signature. Who that certificate is, and whether to trust it, is <see cref="FeTrust"/>'s question.</summary>
    /// <param name="carried">Every certificate in KeyInfo: a signer may send its chain.</param>
    public static X509Certificate2? Signer(XmlDocument doc, out IReadOnlyList<X509Certificate2> carried)
    {
        carried = [];
        var signatures = doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        var last = doc.DocumentElement!.ChildNodes.OfType<XmlElement>().LastOrDefault();
        if (signatures.Count != 1 || last is null || !ReferenceEquals(last, signatures[0])) return null;
        try
        {
            var signed = new SignedXml(doc);
            signed.LoadXml(last);
            if (signed.SignedInfo is not { References.Count: 1 } info || ((Reference)info.References[0]!).Uri != ""
                || info.SignatureMethod != SignedXml.XmlDsigRSASHA256Url) return null;
            carried = [.. signed.KeyInfo.OfType<KeyInfoX509Data>().SelectMany(d => d.Certificates?.OfType<X509Certificate2>() ?? [])];
            return carried.FirstOrDefault(c => signed.CheckSignature(c, verifySignatureOnly: true));
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Only the signature itself, against the certificate it carries (Toki's own documents, tests).</summary>
    public static bool SignatureIsValid(XmlDocument doc) => Signer(doc, out _) is not null;

    /// <summary>Same signature as the store's <c>CertificateEcfSigner</c> (enveloped, C14N 1.0, RSA-SHA256, SHA-256, <c>Reference URI=""</c>, X509 in KeyInfo),
    /// with the RSA operation done by <paramref name="key"/> asynchronously (R9 #2: a Key Vault call never blocks a thread).</summary>
    /// <remarks>shortcut: a copy of the store's 20 lines; the cloud only references module contracts. <c>SignedXml</c> signs synchronously, so it runs
    /// once with a key that only records the SignedInfo digest, and the real signature replaces the placeholder (SignatureValue is outside what is signed).</remarks>
    public static async Task<string> SignAsync(XDocument xml, FeKey key, CancellationToken ct)
    {
        var doc = new XmlDocument { PreserveWhitespace = false };
        using (var reader = xml.CreateReader()) doc.Load(reader);
        using var publicKey = key.Certificate.GetRSAPublicKey() ?? throw new InvalidOperationException("The e-CF certificate has no RSA key.");
        using var digest = new DigestOnlyRsa(publicKey);
        var signed = new SignedXml(doc) { SigningKey = digest };
        signed.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        signed.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference("") { DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(key.Certificate));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();
        var hash = digest.Hash ?? throw new InvalidOperationException("SignedXml did not ask for the signature.");
        var signature = await key.SignSha256Async(hash, ct);
        if (!publicKey.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("The vault key does not match the certificate.");
        var element = signed.GetXml();
        element.GetElementsByTagName("SignatureValue", SignedXml.XmlDsigNamespaceUrl)[0]!.InnerText = Convert.ToBase64String(signature);
        doc.DocumentElement!.AppendChild(doc.ImportNode(element, true));
        doc.InsertBefore(doc.CreateXmlDeclaration("1.0", "utf-8", null), doc.DocumentElement);
        return doc.OuterXml;
    }

    public static string? Text(XmlDocument doc, string path) => doc.SelectSingleNode(path)?.InnerText.Trim() is { Length: > 0 } s ? s : null;

    public static byte[] Zip(string text) => Zip(Encoding.UTF8.GetBytes(text));

    public static byte[] Zip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize)) gzip.Write(bytes);
        return output.ToArray();
    }

    public static byte[] UnzipBytes(byte[] gzip)
    {
        using var z = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Text Toki wrote (UTF-8).</summary>
    public static string Unzip(byte[] gzip) => Encoding.UTF8.GetString(UnzipBytes(gzip));

    /// <summary>A received document as text, decoded with the encoding it declares (UTF-8 when it declares none or cannot be read).</summary>
    public static string ReceivedText(byte[] gzip)
    {
        var bytes = UnzipBytes(gzip);
        var encoding = Encoding.UTF8;
        try
        {
            using var reader = new XmlTextReader(new MemoryStream(bytes)) { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            reader.Read();
            encoding = reader.Encoding ?? encoding;
        }
        catch (XmlException) { }
        return encoding.GetString(bytes).TrimStart('﻿');
    }

    /// <summary>Only records the digest <c>SignedXml</c> asks it to sign; the vault signs it afterwards (see <see cref="SignAsync"/>).</summary>
    private sealed class DigestOnlyRsa : RSA
    {
        private readonly RSA _public;

        public DigestOnlyRsa(RSA publicKey)
        {
            _public = publicKey;
            KeySizeValue = publicKey.KeySize;
            LegalKeySizesValue = [new KeySizes(publicKey.KeySize, publicKey.KeySize, 0)];
        }

        public byte[]? Hash { get; private set; }

        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
        {
            if (hashAlgorithm != HashAlgorithmName.SHA256 || padding != RSASignaturePadding.Pkcs1)
                throw new NotSupportedException("The e-CF signature is RSA-SHA256 with PKCS#1 padding.");
            Hash = hash;
            return new byte[KeySize / 8];
        }

        public override RSAParameters ExportParameters(bool includePrivateParameters) =>
            includePrivateParameters ? throw new CryptographicException("The key is not exportable.") : _public.ExportParameters(false);

        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();
    }
}

/// <summary>R9 #1: who may sign what the receiver takes. A signature counts only when its certificate chains to an authority in <c>Fe:TrustedRoots</c>
/// (the INDOTEL certification authorities, docs/ecf-propio.md §11 question 34) and, where the document names a taxpayer, belongs to that RNC.
/// With no root configured nothing is trusted: the receiver fails closed.</summary>
/// <remarks><c>Fe:TrustedRoots</c>: the authorities' certificates in Base64 (DER), self-signed roots and their intermediates alike.
/// <c>Fe:CheckRevocation</c> (true; off only outside Production): the CRL or OCSP of the chain is checked online and an unknown status is a refusal.</remarks>
internal sealed class FeTrust
{
    private readonly X509Certificate2Collection _roots = [];
    private readonly X509Certificate2Collection _intermediates = [];
    private readonly bool _checkRevocation;
    private readonly TimeProvider _clock;

    public FeTrust(IConfiguration config, IHostEnvironment env, TimeProvider clock, ILogger<FeTrust> log)
    {
        _clock = clock;
        foreach (var value in config.GetSection("Fe:TrustedRoots").Get<string[]>() ?? [])
        {
            var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value.Trim()));
            (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData) ? _roots : _intermediates).Add(certificate);
        }
        _checkRevocation = config.GetValue("Fe:CheckRevocation", true);
        if (!_checkRevocation && env.IsProduction())
            throw new InvalidOperationException("Fe:CheckRevocation cannot be off in Production.");
        if (_roots.Count == 0)
            log.LogWarning("Fe:TrustedRoots has no certification authority: the e-CF receiver refuses every signature until the INDOTEL roots are loaded");
    }

    /// <summary>The signer of <paramref name="doc"/>, when its signature is valid, it chains to a trusted authority and (with <paramref name="rnc"/>)
    /// it belongs to that RNC.</summary>
    public bool SignedByTrusted(XmlDocument doc, string? rnc = null) =>
        FeXml.Signer(doc, out var carried) is { } signer && (rnc is null || BelongsTo(signer, rnc)) && IsTrusted(signer, carried);

    public bool IsTrusted(X509Certificate2 certificate, IEnumerable<X509Certificate2>? carried = null)
    {
        if (_roots.Count == 0) return false;
        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.AddRange(_roots);
        policy.ExtraStore.AddRange(_intermediates);
        foreach (var c in carried ?? []) policy.ExtraStore.Add(c);
        policy.RevocationMode = _checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
        policy.VerificationTime = _clock.GetUtcNow().UtcDateTime;
        policy.VerificationTimeIgnored = false;
        return chain.Build(certificate);
    }

    /// <summary>The RNC or cédula on the certificate (subject SERIALNUMBER or organizationIdentifier, as the store reads it) is <paramref name="rnc"/>,
    /// or it is a cédula on a company's document: the delegated signer (R8 #4). A certificate without one belongs to no one.</summary>
    /// <remarks>shortcut: that the cédula is delegated for that company is not checked (only the DGII knows it, question 34).</remarks>
    public static bool BelongsTo(X509Certificate2 certificate, string rnc) =>
        TaxIdOf(certificate) is { } taxId && (taxId == rnc || (rnc.Length == 9 && taxId.Length == 11));

    public static string? TaxIdOf(X509Certificate2 certificate) => certificate.SubjectName.EnumerateRelativeDistinguishedNames()
        .Where(n => n.GetSingleElementType().Value is "2.5.4.5" or "2.5.4.97")
        .Select(n => new string([.. (n.GetSingleElementValue() ?? "").Where(char.IsAsciiDigit)]))
        .FirstOrDefault(d => d.Length is 9 or 11);
}

/// <summary>TK-151/TK-246: who signs for the receiver. <b>Only</b> acknowledgements (ARECF) and commercial approvals (ACECF): anything else (an e-CF, an
/// RFCE, an ANECF, and a SemillaModel, which signed is a DGII token for the RNC) is refused here, in code, whoever asks. StoreSigned: the store signs
/// over the node channel (<see cref="FeSignBroker"/>) and its answer is checked (<see cref="FeStoreSignature"/>); CloudCustody: the vault key.
/// Every signature is written to <see cref="FeSignature"/> (the panel shows them). A withdrawn consent stops it at the next signature.</summary>
internal sealed class FeSigner(CloudDbContext db, IFeCertificateVault vault, FeSignBroker broker, FeTrust trust, TimeProvider clock, ILogger<FeSigner> log)
{
    public static readonly IReadOnlySet<string> AllowedRoots = new HashSet<string>(StringComparer.Ordinal) { "ARECF", "ACECF" };

    /// <returns>The signed XML, or null when the consent was withdrawn or the store did not sign in time (an outage is added: the caller saves).</returns>
    /// <param name="keepXml">Keep the signed document in the log (the acknowledgements of what was received: they have no other home).</param>
    public async Task<string?> SignAsync(FeCertificate certificate, XDocument xml, string? encf, bool keepXml, CancellationToken ct)
    {
        var root = xml.Root ?? throw new InvalidOperationException("Empty document.");
        if (root.Name.Namespace != XNamespace.None || !AllowedRoots.Contains(root.Name.LocalName))
            throw new InvalidOperationException($"The receiver never signs a {root.Name.LocalName}: only ARECF and ACECF.");
        if (!await db.FeCertificates.AnyAsync(c => c.Id == certificate.Id && c.RevokedUtc == null, ct)) return null;

        string? signed;
        if (certificate.SigningMode == FeSigningModes.StoreSigned)
        {
            signed = await broker.SignAsync(certificate.ConsentedByNodeId, xml.ToString(SaveOptions.DisableFormatting), ct);
            if (signed is not null && !FeStoreSignature.Matches(signed, xml, certificate.Rnc, trust))
            {
                log.LogWarning("The store of tenant {TenantId} answered a signature that is not the document asked for, or not of RNC {Rnc}", certificate.TenantId, certificate.Rnc);
                signed = null;
            }
            if (signed is null)
            {
                await OutageAsync(certificate, ct);
                return null;
            }
        }
        else signed = await FeXml.SignAsync(xml, await vault.KeyAsync(certificate.KeyName, ct), ct);
        db.FeSignatures.Add(new FeSignature
        {
            TenantId = certificate.TenantId, CertificateId = certificate.Id, AtUtc = clock.GetUtcNow().UtcDateTime, Root = root.Name.LocalName, ENcf = encf,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signed))), XmlGzip = keepXml ? FeXml.Zip(signed) : null,
        });
        await db.SaveChangesAsync(ct); // with whatever the caller added: the received document and its acknowledgement go together
        return signed;
    }

    /// <summary>Adds the PC's outage unless one is open (the caller saves it, with the document it keeps pending); <see cref="FeRemoteSigningEndpoints"/>
    /// closes it at the next poll.</summary>
    private async Task OutageAsync(FeCertificate certificate, CancellationToken ct)
    {
        var node = certificate.ConsentedByNodeId;
        if (!await db.FeReceiverOutages.AnyAsync(o => o.TenantId == certificate.TenantId && o.NodeId == node && o.ToUtc == null, ct))
            db.FeReceiverOutages.Add(new FeReceiverOutage { TenantId = certificate.TenantId, NodeId = node, FromUtc = clock.GetUtcNow().UtcDateTime });
    }
}
