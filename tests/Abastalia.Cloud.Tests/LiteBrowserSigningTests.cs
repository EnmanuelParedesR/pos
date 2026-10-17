using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Host.Cloud.Fe;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-317 (F0 of the Lite plan): the browser signer (src/Cloud/lite-poc/signer.js, run by Node) signs a 31, a 32, its RFCE and a DGII seed
/// with a .p12, and the result is byte for byte what the store's <see cref="CertificateEcfSigner"/> produces, passes the XSD and the cloud's
/// verifier of received e-CF (<see cref="FeXml.Signer"/>). Needs <c>node</c> and <c>npm ci</c> in src/Cloud/lite-poc (CI does it); skips otherwise.</summary>
public sealed class LiteBrowserSigningTests
{
    public static TheoryData<string> Encryptions => ["Pbes2Aes256Sha256", "Pkcs12TripleDesSha1"];

    [Theory]
    [MemberData(nameof(Encryptions))]
    public async Task The_browser_signs_like_the_store(string encryption)
    {
        using var rsa = RSA.Create(2048);
        // Throwaway self-signed certificate made here; SERIALNUMBER = the signer's cédula, as on a real one.
        using var certificate = new CertificateRequest("CN=Firmante de Prueba, SERIALNUMBER=00112345678", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pfx = certificate.ExportPkcs12(Enum.Parse<Pkcs12ExportPbeParameters>(encryption), "clave-de-prueba");

        var issuer = new BusinessProfile("Carnicería & Colmado \"La Escuadra\" SRL", "131245678", "Av. Duarte 25, Villa Consuelo", null, null, 10m);
        var now = new DateTime(2026, 10, 15, 18, 30, 5, DateTimeKind.Utc);
        var line = new FiscalLine("Jamón > 1 lb & queso <blanco>", 2, 590m, 0, ItbisRate.General18, 180m, 1180m);
        XDocument Ecf(EcfType type, string? rnc) => EcfXml.Build(new DocumentToIssue(type, Guid.NewGuid(), rnc, rnc is null ? null : "Cliente SRL", [line], 180m, 1180m,
            null, null, EcfPaymentType.Cash, [new FiscalPayment(EcfPaymentForm.Cash, 1180m)]), $"E{(int)type}0000000001", issuer, now, new DateOnly(2028, 12, 31), null);
        var store = new CertificateEcfSigner(certificate);
        var e31 = Ecf(EcfType.FiscalCredit31, "101672919");
        var e32 = Ecf(EcfType.Consumer32, null);
        var (signed32, code32) = store.Sign(e32);
        var rfce = EcfXml.Rfce(signed32, code32);        // As the DGII sends it (xsi/xsd declared on the root: they must reach the canonical SignedInfo), without the declaration as the store signs it.
        var seed = XDocument.Parse("""<SemillaModel xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema"><valor>dGVzdA==&amp;x</valor><fecha>2026-10-15T11:00:00-04:00</fecha></SemillaModel>""");
        (XDocument Xml, string Schema)[] documents = [(e31, EcfXml.SchemaOf(EcfType.FiscalCredit31)), (e32, EcfXml.SchemaOf(EcfType.Consumer32)),
            (rfce, EcfXml.RfceSchema), (seed, EcfXml.SeedSchema)];

        using var browser = await NodeSignAsync(pfx, "clave-de-prueba", [.. documents.Select(d => d.Xml.ToString(SaveOptions.DisableFormatting))]);
        browser.RootElement.GetProperty("extractable").GetBoolean().ShouldBeFalse();
        var signed = browser.RootElement.GetProperty("signed").EnumerateArray().ToArray();
        signed.Length.ShouldBe(documents.Length);
        for (var i = 0; i < documents.Length; i++)
        {
            var (xml, schema) = documents[i];
            var browserXml = signed[i].GetProperty("signedXml").GetString()!;
            var (storeXml, storeCode) = store.Sign(xml);
            browserXml.ShouldBe(storeXml); // RSA PKCS#1 v1.5 is deterministic: same key, same bytes
            signed[i].GetProperty("securityCode").GetString().ShouldBe(storeCode);
            EcfXml.Validate(XDocument.Parse(browserXml), schema).ShouldBeEmpty();
            FeXml.Signer(FeXml.Parse(browserXml)!, out _)!.Thumbprint.ShouldBe(certificate.Thumbprint);
        }
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        Poc(); // skips before the throw assertion would swallow the skip
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Firmante de Prueba", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        await Should.ThrowAsync<InvalidOperationException>(() => NodeSignAsync(
            certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, "clave-de-prueba"), "otra", ["<SemillaModel />"]));
    }

    private static async Task<JsonDocument> NodeSignAsync(byte[] pfx, string password, string[] documents)
    {
        var poc = Poc();

        var start = new ProcessStartInfo("node", "sign-cli.mjs")
        {
            WorkingDirectory = poc, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        Process node;
        try { node = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception) { Assert.Skip("node is not installed."); throw; }
        using (node)
        {
            await node.StandardInput.WriteAsync(JsonSerializer.Serialize(new { p12 = Convert.ToBase64String(pfx), password, documents }));
            node.StandardInput.Close();
            var output = node.StandardOutput.ReadToEndAsync();
            var error = await node.StandardError.ReadToEndAsync();
            await node.WaitForExitAsync();
            if (node.ExitCode != 0) throw new InvalidOperationException($"node sign-cli.mjs failed: {error}");
            return JsonDocument.Parse(await output);
        }
    }

    private static string Poc()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Abastalia.slnx"))) root = root.Parent!;
        var poc = Path.Combine(root.FullName, "src", "Cloud", "lite-poc");
        if (!Directory.Exists(Path.Combine(poc, "node_modules"))) Assert.Skip("Run npm ci in src/Cloud/lite-poc (CI does).");
        return poc;
    }
}
