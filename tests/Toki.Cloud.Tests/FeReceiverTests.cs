using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Fiscal;
using Toki.Fiscal.Contracts;
using Toki.Host.Cloud.Fe;
using Toki.Host.Cloud.Persistence;
using Toki.Settings.Contracts;
using Toki.SharedKernel;

namespace Toki.Cloud.Tests;

/// <summary>The cloud with the outside world it talks to for a tenant (the DGII and a supplier's receiver) in memory.</summary>
public sealed class FeCloudApp : CloudApp
{
    public FakeFeOutside Outside { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Fe:TrustedRoots:0", TestPki.RootBase64); // R9 #1: the test authority stands in for INDOTEL's
        builder.UseSetting("Fe:CheckRevocation", "false"); // the test authority publishes no CRL
        builder.ConfigureTestServices(s => s.AddHttpClient(FeOutbound.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Outside));
    }
}

/// <summary>A certification authority for the tests, in place of the INDOTEL ones (Avansi, Digifirma) the cloud trusts through <c>Fe:TrustedRoots</c>.</summary>
public static class TestPki
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly X509Certificate2 Root = CreateRoot();

    public static string RootBase64 => Convert.ToBase64String(Root.RawData);

    private static X509Certificate2 CreateRoot()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Entidad de Certificacion de Prueba, O=Toki Pruebas", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(From, To);
    }

    /// <summary>A signing certificate issued by <see cref="Root"/>, with its key (and as a .p12 with password "prueba").</summary>
    public static X509Certificate2 Issue(string subject, out byte[] pfx)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
        using var issued = request.Create(Root, From.AddDays(1), To.AddDays(-1), RandomNumberGenerator.GetBytes(12));
        using var withKey = issued.CopyWithPrivateKey(rsa);
        pfx = withKey.Export(X509ContentType.Pkcs12, "prueba");
        return X509CertificateLoader.LoadPkcs12(pfx, "prueba", X509KeyStorageFlags.EphemeralKeySet);
    }

    public static X509Certificate2 Issue(string subject) => Issue(subject, out _);
}

/// <summary>The DGII (seed, token, directory, approval copy) and a supplier's receiver (seed authentication, approval URL), per the documented contracts.</summary>
public sealed class FakeFeOutside : HttpMessageHandler
{
    public const string SupplierHost = "proveedor.test";
    public List<(string Url, string? Authorization, string? Xml)> Requests { get; } = [];
    /// <summary>RNCs the directory lists, with the supplier's URLs.</summary>
    public HashSet<string> Electronic { get; } = [];

    public IEnumerable<string?> XmlTo(string pathEnd)
    {
        lock (Requests) return Requests.Where(r => r.Url.EndsWith(pathEnd, StringComparison.OrdinalIgnoreCase)).Select(r => r.Xml).ToList();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? xml = null;
        if (request.Content is MultipartFormDataContent multipart)
            foreach (var part in multipart) xml = await part.ReadAsStringAsync(cancellationToken);
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        lock (Requests) Requests.Add((url, request.Headers.Authorization?.ToString(), xml));
        var path = request.RequestUri.AbsolutePath.ToLowerInvariant();
        var bearer = request.Headers.Authorization?.Scheme == "Bearer";

        if (path.EndsWith("/semilla", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"<SemillaModel><valor>{Guid.NewGuid():N}</valor><fecha>2026-10-15T11:00:00</fecha></SemillaModel>") };
        if (path.EndsWith("/validarsemilla", StringComparison.Ordinal) || path.EndsWith("/validacioncertificado", StringComparison.Ordinal))
            return xml!.Contains("<SignatureValue>", StringComparison.Ordinal) ? Json(new { token = "tok", expira = "2026-10-15T16:00:00Z" }) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (!bearer) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (path.EndsWith("/obtenerdirectorioporrnc", StringComparison.Ordinal))
        {
            var rnc = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["RNC"]!;
            return Electronic.Contains(rnc)
                ? Json(new[] { new { urlRecepcion = $"https://{SupplierHost}/fe/recepcion/api/ecf", urlAceptacion = $"https://{SupplierHost}/fe/aprobacioncomercial/api/ecf", urlOpcional = $"https://{SupplierHost}/fe/autenticacion" } })
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        if (path.EndsWith("/aprobacioncomercial/api/aprobacioncomercial", StringComparison.Ordinal)) return Json(new { mensaje = Array.Empty<string>(), estado = "Recibido", codigo = 1 });
        if (path.EndsWith("/fe/aprobacioncomercial/api/ecf", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

/// <summary>TK-151 to TK-154 in the cloud: consent and the vault copy, the restricted signer, the receiver services with their authentication,
/// the store's pull and the commercial approval. The supplier's e-CF are built and signed exactly as a Toki store does.</summary>
public sealed class FeReceiverTests(FeCloudApp app) : IClassFixture<FeCloudApp>
{
    private const string SupplierRnc = "101672919";
    /// <summary>The supplier's delegated signer (a cédula on a company's e-CF, R8 #4), issued by the trusted test authority.</summary>
    private static readonly X509Certificate2 SupplierCertificate = TestPki.Issue("CN=Firmante Proveedor, SERIALNUMBER=00198765432");

    [Fact]
    public async Task Without_consent_the_receiver_answers_503_and_consent_needs_both_names_and_the_right_text()
    {
        var rnc = NewRnc();
        (await app.CreateClient().PostAsync($"/ecf/{rnc}/fe/recepcion/api/ecf", Xml("<ECF/>"))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await app.CreateClient().GetAsync($"/TesteCF/{rnc}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await app.CreateClient().GetAsync($"/otro/{rnc}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        Certificate("CN=Dueña", out var pfx);
        var noConsent = await node.Client.PostAsJsonAsync("/fiscal/v1/receiver/certificate",
            new { file = Convert.ToBase64String(pfx), password = "prueba", rnc, ownerName = "Ana Pérez", holderName = "", accepted = true, consentVersion = FeConsent.Version });
        (await noConsent.ErrorCode()).ShouldBe("Fe.ConsentRequired");
        var wrongPassword = await node.Client.PostAsJsonAsync("/fiscal/v1/receiver/certificate",
            new { file = Convert.ToBase64String(pfx), password = "otra", rnc, ownerName = "Ana Pérez", holderName = "Ana Pérez", accepted = true, consentVersion = FeConsent.Version });
        (await wrongPassword.ErrorCode()).ShouldBe("Fe.CertificateUnreadable");
        (await app.InDbAsync(db => db.FeCertificates.CountAsync(c => c.Rnc == rnc))).ShouldBe(0);
    }

    [Fact]
    public async Task A_supplier_e_CF_is_acknowledged_with_a_signed_ARECF_kept_and_pulled_by_the_store()
    {
        var (node, owner, rnc, ours) = await ReceiverAsync();
        var status = await (await node.Client.PostAsync("/fiscal/v1/receiver/status", null)).Json();
        status.GetProperty("active").GetBoolean().ShouldBeTrue();
        status.GetProperty("urls").EnumerateArray().First(u => u.GetProperty("environment").GetString() == "testecf")
            .GetProperty("reception").GetString().ShouldEndWith($"/testecf/{rnc}/fe/recepcion/api/ecf");

        var ecf = SupplierEcf(rnc, "E310000000001");
        (await app.CreateClient().PostAsync($"/testecf/{rnc}/fe/recepcion/api/ecf", Xml(ecf))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var supplier = await SupplierClientAsync(rnc);
        var ack = await AckAsync(supplier, rnc, ecf);
        ack.Estado.ShouldBe("0");
        ack.Reason.ShouldBeNull();
        ack.SignedBy.ShouldBe(ours.Thumbprint);

        // Duplicate, tampered amount, another buyer, a document that fails the XSD: each one acknowledged as not received, with its reason.
        (await AckAsync(supplier, rnc, ecf)).Reason.ShouldBe("3");
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000002").Replace("<MontoTotal>", "<MontoTotal>1", StringComparison.Ordinal))).Reason.ShouldBe("2");
        (await AckAsync(supplier, rnc, SupplierEcf(NewRnc(), "E310000000003"))).Reason.ShouldBe("4");
        var noTotals = XDocument.Parse(SupplierEcf(rnc, "E310000000004", sign: false));
        noTotals.Descendants("Totales").Remove();
        (await AckAsync(supplier, rnc, new CertificateEcfSigner(SupplierCertificate).Sign(noTotals).SignedXml)).Reason.ShouldBe("1");

        var pulled = await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json();
        var item = pulled.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("eNcf").GetString().ShouldBe("E310000000001");
        item.GetProperty("issuerRnc").GetString().ShouldBe(SupplierRnc);
        item.GetProperty("issuerName").GetString().ShouldBe("Distribuidora del Cibao SRL");
        item.GetProperty("total").GetDecimal().ShouldBe(1180m);
        item.GetProperty("itbis").GetDecimal().ShouldBe(180m);
        item.GetProperty("issuedOn").GetString().ShouldBe("2026-10-15");
        (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = item.GetProperty("id").GetInt64() })).Json())
            .GetProperty("items").GetArrayLength().ShouldBe(0);

        // Another tenant's store sees nothing of it.
        var other = await app.StoreWithNodeAsync(await app.OwnerAsync());
        (await (await other.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);

        // Every cloud signature is in the panel: the seed is the supplier's (signed at the supplier), so five acknowledgements.
        var panel = await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe");
        panel.GetProperty("signatures").EnumerateArray().Count(s => s.GetProperty("root").GetString() == "ARECF").ShouldBe(5);
        panel.GetProperty("status").GetProperty("rnc").GetString().ShouldBe(rnc);
    }

    [Fact]
    public async Task The_cloud_signer_refuses_anything_but_acknowledgements_approvals_and_seeds()
    {
        var (_, owner, rnc, _) = await ReceiverAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var certificate = await db.FeCertificates.SingleAsync(c => c.TenantId == owner.TenantId && c.RevokedUtc == null);
        var signer = scope.ServiceProvider.GetRequiredService<FeSigner>();

        foreach (var root in new[] { "ECF", "RFCE", "ANECF" })
            await Should.ThrowAsync<InvalidOperationException>(() => signer.SignAsync(certificate, new XDocument(new XElement(root)), null, false, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() =>
            signer.SignAsync(certificate, new XDocument(new XElement(XName.Get("ARECF", "urn:otro"))), null, false, CancellationToken.None));
        (await signer.SignAsync(certificate, new XDocument(new XElement("SemillaModel", new XElement("valor", "x"))), null, false, CancellationToken.None)).ShouldNotBeNull();

        // The same RNC cannot be claimed by a second tenant.
        var intruder = await app.StoreWithNodeAsync(await app.OwnerAsync());
        TestPki.Issue("CN=Otro, SERIALNUMBER=00100000002", out var pfx);
        var taken = await intruder.Client.PostAsJsonAsync("/fiscal/v1/receiver/certificate",
            new { file = Convert.ToBase64String(pfx), password = "prueba", rnc, ownerName = "Otro", holderName = "Otro", accepted = true, consentVersion = FeConsent.Version });
        (await taken.ErrorCode()).ShouldBe("Fe.RncTaken");
    }

    [Fact]
    public async Task Withdrawing_the_consent_stops_the_receiver_at_once_and_deletes_the_key()
    {
        var (_, owner, rnc, _) = await ReceiverAsync();
        var supplier = await SupplierClientAsync(rnc);
        var keyName = await app.InDbAsync(db => db.FeCertificates.Where(c => c.Rnc == rnc).Select(c => c.KeyName).SingleAsync());

        (await owner.Client.PostAsync("/api/panel/fe/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await supplier.PostAsync($"/testecf/{rnc}/fe/recepcion/api/ecf", Xml(SupplierEcf(rnc, "E310000000009")))).StatusCode
            .ShouldBe(HttpStatusCode.ServiceUnavailable);
        await Should.ThrowAsync<InvalidOperationException>(() => app.Services.GetRequiredService<IFeCertificateVault>().KeyAsync(keyName, CancellationToken.None));
        (await (await owner.Client.GetAsync("/api/panel/fe")).Json()).GetProperty("status").GetProperty("active").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task The_owners_commercial_approval_is_signed_once_and_sent_to_the_supplier_and_the_DGII()
    {
        app.Outside.Electronic.Add(SupplierRnc);
        var (node, _, rnc, ours) = await ReceiverAsync();
        var supplier = await SupplierClientAsync(rnc);
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000021"))).Estado.ShouldBe("0");
        var id = (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items")[0].GetProperty("id").GetInt64();

        (await (await node.Client.PostAsJsonAsync($"/fiscal/v1/received/{id}/approval", new { approve = false })).ErrorCode()).ShouldBe("Fe.ApprovalReasonRequired");
        var r = await node.Client.PostAsJsonAsync($"/fiscal/v1/received/{id}/approval", new { approve = false, reason = "Llegó carne en mal estado" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = await r.Json();
        answer.GetProperty("approval").GetString().ShouldBe("Rejected");
        answer.GetProperty("approvalDeliveredUtc").ValueKind.ShouldBe(JsonValueKind.String);
        answer.GetProperty("approvalDgiiUtc").ValueKind.ShouldBe(JsonValueKind.String);

        // Other tests of this class send approvals too: only this e-NCF's count.
        IEnumerable<string?> Sent(string path) => app.Outside.XmlTo(path).Where(x => x!.Contains("E310000000021", StringComparison.Ordinal));
        var toSupplier = Sent("/fe/aprobacioncomercial/api/ecf").ShouldHaveSingleItem()!;
        Sent("/aprobacioncomercial/api/aprobacioncomercial").ShouldHaveSingleItem().ShouldBe(toSupplier);
        FeXml.Validate(toSupplier, FeXml.Acecf).ShouldBeEmpty();
        var acecf = FeXml.Parse(toSupplier)!;
        FeXml.SignatureIsValid(acecf).ShouldBeTrue();
        FeXml.Text(acecf, "/ACECF/DetalleAprobacionComercial/Estado").ShouldBe("2");
        FeXml.Text(acecf, "/ACECF/DetalleAprobacionComercial/RNCComprador").ShouldBe(rnc);
        acecf.OuterXml.ShouldContain(Convert.ToBase64String(ours.RawData));
        // The supplier asked for its seed authentication: the approval went with its token.
        app.Outside.Requests.ShouldContain(q => q.Url.EndsWith("/fe/aprobacioncomercial/api/ecf", StringComparison.Ordinal) && q.Authorization == "Bearer tok");

        (await (await node.Client.PostAsJsonAsync($"/fiscal/v1/received/{id}/approval", new { approve = true })).ErrorCode()).ShouldBe("Fe.ApprovalAlreadySigned");
        (await node.Client.PostAsJsonAsync($"/fiscal/v1/received/{id}/approval", new { approve = false, reason = "otra vez" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        Sent("/fe/aprobacioncomercial/api/ecf").Count().ShouldBe(1); // nothing missing: nothing sent again
    }

    [Fact]
    public async Task A_buyers_commercial_approval_of_an_e_CF_the_tenant_issued_is_kept()
    {
        var (_, owner, rnc, _) = await ReceiverAsync();
        var buyer = await SupplierClientAsync(rnc);
        string Acecf(string issuer) => new CertificateEcfSigner(SupplierCertificate).Sign(new XDocument(new XElement("ACECF", new XElement("DetalleAprobacionComercial",
            new XElement("Version", "1.0"), new XElement("RNCEmisor", issuer), new XElement("eNCF", "E310000000077"), new XElement("FechaEmision", "15-10-2026"),
            new XElement("MontoTotal", "1180.00"), new XElement("RNCComprador", SupplierRnc), new XElement("Estado", "1"),
            new XElement("FechaHoraAprobacionComercial", "15-10-2026 11:00:00"))))).SignedXml;

        (await buyer.PostAsync($"/testecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml(Acecf(rnc)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await buyer.PostAsync($"/testecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml(Acecf(NewRnc())))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await buyer.PostAsync($"/testecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml(Acecf(rnc).Replace("1180.00", "1.00", StringComparison.Ordinal))))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var approval = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe")).GetProperty("approvals").EnumerateArray().ShouldHaveSingleItem();
        approval.GetProperty("eNcf").GetString().ShouldBe("E310000000077");
        approval.GetProperty("approved").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task R9_1_an_e_CF_signed_outside_the_trusted_chain_or_by_someone_not_bound_to_the_issuer_RNC_is_not_received()
    {
        var (node, _, rnc, _) = await ReceiverAsync();
        var supplier = await SupplierClientAsync(rnc);

        // Self-signed with the right cédula, from a trusted authority with another company's RNC, or with no RNC or cédula at all: signature reason 2.
        var selfSigned = Certificate("CN=Falso, SERIALNUMBER=00198765432", out _);
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000101", signer: selfSigned))).Reason.ShouldBe("2");
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000102", signer: TestPki.Issue("CN=Otra empresa, SERIALNUMBER=130000001")))).Reason.ShouldBe("2");
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000103", signer: TestPki.Issue("CN=Sin RNC")))).Reason.ShouldBe("2");
        // The supplier's own RNC on the certificate is fine.
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000104", signer: TestPki.Issue($"CN=Distribuidora, SERIALNUMBER={SupplierRnc}")))).Estado.ShouldBe("0");
        (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items").GetArrayLength().ShouldBe(1);

        // The seed authentication and a buyer's approval need the trusted chain too.
        var client = app.CreateClient();
        var seed = XDocument.Parse(await client.GetStringAsync($"/testecf/{rnc}/fe/autenticacion/api/semilla"));
        (await client.PostAsync($"/testecf/{rnc}/fe/autenticacion/api/validacioncertificado", Xml(new CertificateEcfSigner(selfSigned).Sign(seed).SignedXml)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        string Acecf(X509Certificate2 signer) => new CertificateEcfSigner(signer).Sign(new XDocument(new XElement("ACECF", new XElement("DetalleAprobacionComercial",
            new XElement("Version", "1.0"), new XElement("RNCEmisor", rnc), new XElement("eNCF", "E310000000078"), new XElement("FechaEmision", "15-10-2026"),
            new XElement("MontoTotal", "1180.00"), new XElement("RNCComprador", SupplierRnc), new XElement("Estado", "1"),
            new XElement("FechaHoraAprobacionComercial", "15-10-2026 11:00:00"))))).SignedXml;
        (await supplier.PostAsync($"/testecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml(Acecf(selfSigned)))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await supplier.PostAsync($"/testecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml(Acecf(TestPki.Issue("CN=Otra, SERIALNUMBER=130000001")))))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await supplier.PostAsync($"/testecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml(Acecf(SupplierCertificate)))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task R9_1_the_same_eNCF_with_other_content_is_kept_as_a_conflict_instead_of_answered_as_a_duplicate()
    {
        var (node, owner, rnc, _) = await ReceiverAsync();
        var supplier = await SupplierClientAsync(rnc);
        var first = SupplierEcf(rnc, "E310000000201");
        (await AckAsync(supplier, rnc, first)).Estado.ShouldBe("0");
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000201", item: "Pollo entero (caja)"))).Estado.ShouldBe("0");
        (await AckAsync(supplier, rnc, first)).Reason.ShouldBe("3"); // the very same document again is a duplicate

        var items = (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.GetProperty("eNcf").GetString() == "E310000000201" && i.GetProperty("conflict").GetBoolean());
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe/received")).EnumerateArray().ShouldAllBe(i => i.GetProperty("conflict").GetBoolean());
    }

    [Fact]
    public async Task R9_5_the_same_eNCF_in_testecf_and_in_ecf_are_two_documents()
    {
        var (node, _, rnc, _) = await ReceiverAsync();
        var ecf = SupplierEcf(rnc, "E310000000301");
        (await AckAsync(await SupplierClientAsync(rnc), rnc, ecf)).Estado.ShouldBe("0");
        (await AckAsync(await SupplierClientAsync(rnc, "ecf"), rnc, ecf, "ecf")).Estado.ShouldBe("0");
        var items = (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("environment").GetString()).ShouldBe(["testecf", "ecf"]);
        items.ShouldAllBe(i => !i.GetProperty("conflict").GetBoolean());
    }

    [Fact]
    public async Task R9_17_the_received_XML_is_kept_byte_for_byte_in_its_own_encoding()
    {
        var (_, owner, rnc, _) = await ReceiverAsync();
        var supplier = await SupplierClientAsync(rnc);
        var signed = SupplierEcf(rnc, "E310000000401", supplierName: "Distribuidora Peña SRL");
        var latin1 = Encoding.Latin1.GetBytes(signed.Replace("encoding=\"utf-8\"", "encoding=\"ISO-8859-1\"", StringComparison.OrdinalIgnoreCase));
        latin1.ShouldNotBe(Encoding.UTF8.GetBytes(signed));

        (await AckAsync(supplier, rnc, Xml(latin1), "testecf")).Estado.ShouldBe("0");

        var saved = await app.InDbAsync(db => db.FeReceived.SingleAsync(d => d.TenantId == owner.TenantId && d.ENcf == "E310000000401"));
        saved.IssuerName.ShouldBe("Distribuidora Peña SRL");
        using (var unzip = new System.IO.Compression.GZipStream(new MemoryStream(saved.XmlGzip), System.IO.Compression.CompressionMode.Decompress))
        using (var bytes = new MemoryStream())
        {
            await unzip.CopyToAsync(bytes);
            bytes.ToArray().ShouldBe(latin1);
        }
        (await owner.Client.GetFromJsonAsync<JsonElement>($"/api/panel/fe/received/{saved.Id}/xml")).GetProperty("xml").GetString()!.ShouldContain("Distribuidora Peña SRL");
    }

    [Fact]
    public async Task R9_2_a_refusal_is_signed_but_its_XML_is_not_kept_and_the_schemas_validate_in_parallel()
    {
        var (_, owner, rnc, _) = await ReceiverAsync();
        var supplier = await SupplierClientAsync(rnc);
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000501"))).Estado.ShouldBe("0");
        (await AckAsync(supplier, rnc, SupplierEcf(NewRnc(), "E310000000502"))).Reason.ShouldBe("4");

        var kept = await app.InDbAsync(db => db.FeSignatures.Where(s => s.TenantId == owner.TenantId && s.Root == "ARECF")
            .ToDictionaryAsync(s => s.ENcf!, s => s.XmlGzip is not null));
        kept.ShouldBe(new Dictionary<string, bool> { ["E310000000501"] = true, ["E310000000502"] = false });

        var good = SupplierEcf(rnc, "E310000000503");
        var bad = XDocument.Parse(SupplierEcf(rnc, "E310000000504", sign: false));
        bad.Descendants("Totales").Remove();
        var badText = bad.ToString(SaveOptions.DisableFormatting);
        var results = new System.Collections.Concurrent.ConcurrentBag<(bool Good, int Errors)>();
        await Parallel.ForAsync(0, 64, async (i, _) =>
        {
            await Task.Yield();
            var isGood = i % 2 == 0;
            results.Add((isGood, FeXml.Validate(isGood ? good : badText, FeXml.EcfSchema(31)!).Count));
        });
        results.ShouldAllBe(r => r.Good ? r.Errors == 0 : r.Errors > 0);
    }

    [Fact]
    public async Task R9_2_the_receiver_limits_requests_per_IP_and_per_RNC()
    {
        using var perIp = app.WithWebHostBuilder(b => b.UseSetting("Fe:RequestsPerMinutePerIp", "3").UseSetting("Fe:RequestsPerMinutePerRnc", "100"));
        var client = perIp.CreateClient();
        foreach (var _ in Enumerable.Range(0, 3))
            (await client.GetAsync($"/testecf/{NewRnc()}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync($"/testecf/{NewRnc()}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK); // only the receiver is limited

        using var perRnc = app.WithWebHostBuilder(b => b.UseSetting("Fe:RequestsPerMinutePerIp", "100").UseSetting("Fe:RequestsPerMinutePerRnc", "2"));
        client = perRnc.CreateClient();
        var rnc = NewRnc();
        (await client.GetAsync($"/testecf/{rnc}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.PostAsync($"/ecf/{rnc}/fe/recepcion/api/ecf", Xml("<ECF/>"))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync($"/TesteCF/{rnc}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await client.GetAsync($"/testecf/{NewRnc()}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task R9_6_the_certificate_must_chain_to_a_trusted_authority_and_belong_to_the_RNC_and_one_RNC_has_one_active_receiver()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var rnc = NewRnc();
        Task<HttpResponseMessage> Consent(byte[] pfx, string forRnc) => node.Client.PostAsJsonAsync("/fiscal/v1/receiver/certificate",
            new { file = Convert.ToBase64String(pfx), password = "prueba", rnc = forRnc, ownerName = "Ana", holderName = "Ana", accepted = true, consentVersion = FeConsent.Version });

        Certificate("CN=Ana, SERIALNUMBER=00112345678", out var selfSigned);
        (await (await Consent(selfSigned, rnc)).ErrorCode()).ShouldBe("Fe.CertificateUntrusted");
        TestPki.Issue("CN=Otra empresa, SERIALNUMBER=130000001", out var otherCompany);
        (await (await Consent(otherCompany, rnc)).ErrorCode()).ShouldBe("Fe.CertificateRncMismatch");
        TestPki.Issue("CN=Sin RNC", out var noTaxId);
        (await (await Consent(noTaxId, rnc)).ErrorCode()).ShouldBe("Fe.CertificateRncMismatch");
        TestPki.Issue($"CN=Empresa, SERIALNUMBER={rnc}", out var own);
        (await Consent(own, rnc)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The database refuses a second active receiver for the RNC, whatever races past the check.
        var tenant = Guid.NewGuid();
        await Should.ThrowAsync<DbUpdateException>(() => app.InDbAsync(db =>
        {
            db.FeCertificates.Add(new FeCertificate { Id = Guid.NewGuid(), TenantId = tenant, Rnc = rnc, KeyName = "x", ConsentVersion = FeConsent.Version });
            return db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task R9_10_the_panel_and_the_store_deciding_at_once_sign_one_decision()
    {
        var (node, owner, rnc, _) = await ReceiverAsync();
        (await AckAsync(await SupplierClientAsync(rnc), rnc, SupplierEcf(rnc, "E310000000601"))).Estado.ShouldBe("0");
        var id = (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items")[0].GetProperty("id").GetInt64();

        var answers = await Task.WhenAll(
            node.Client.PostAsJsonAsync($"/fiscal/v1/received/{id}/approval", new { approve = true }),
            owner.Client.PostAsJsonAsync($"/api/panel/fe/received/{id}/approval", new { approve = false, reason = "No pedimos esto" }));

        var saved = await app.InDbAsync(db => db.FeReceived.SingleAsync(d => d.Id == id));
        var winner = saved.Approval == "Approved" ? 0 : 1;
        answers[winner].StatusCode.ShouldBe(HttpStatusCode.OK);
        (await answers[1 - winner].ErrorCode()).ShouldBe("Fe.ApprovalAlreadySigned");
        FeXml.Text(FeXml.Parse(FeXml.Unzip(saved.ApprovalXmlGzip!))!, "/ACECF/DetalleAprobacionComercial/Estado").ShouldBe(winner == 0 ? "1" : "2");
    }

    // --- helpers ---

    private async Task<(StoreNode Node, Owner Owner, string Rnc, X509Certificate2 Certificate)> ReceiverAsync()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var rnc = NewRnc();
        var certificate = TestPki.Issue("CN=Ana Pérez, SERIALNUMBER=00112345678", out var pfx);
        var r = await node.Client.PostAsJsonAsync("/fiscal/v1/receiver/certificate",
            new { file = Convert.ToBase64String(pfx), password = "prueba", rnc, ownerName = "Ana Pérez", holderName = "Ana Pérez", accepted = true, consentVersion = FeConsent.Version });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (node, owner, rnc, certificate);
    }

    /// <summary>A supplier authenticated at the tenant's receiver: seed → signed with its certificate → token.</summary>
    private async Task<HttpClient> SupplierClientAsync(string rnc, string env = "testecf")
    {
        var client = app.CreateClient();
        var seed = XDocument.Parse(await client.GetStringAsync($"/{env}/{rnc}/fe/autenticacion/api/semilla"));
        var bad = await client.PostAsync($"/{env}/{rnc}/fe/autenticacion/api/validacioncertificado", Xml(seed.ToString()));
        bad.StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // unsigned
        var signed = new CertificateEcfSigner(SupplierCertificate).Sign(seed).SignedXml;
        var token = await (await client.PostAsync($"/{env}/{rnc}/fe/autenticacion/api/validacioncertificado", Xml(signed))).Json();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("token").GetString());
        return client;
    }

    private static Task<(string Estado, string? Reason, string? SignedBy)> AckAsync(HttpClient supplier, string rnc, string ecf, string env = "testecf") =>
        AckAsync(supplier, rnc, Xml(ecf), env);

    private static async Task<(string Estado, string? Reason, string? SignedBy)> AckAsync(HttpClient supplier, string rnc, MultipartFormDataContent ecf, string env)
    {
        var r = await supplier.PostAsync($"/{env}/{rnc}/fe/recepcion/api/ecf", ecf);
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await r.Content.ReadAsStringAsync();
        FeXml.Validate(text, FeXml.Arecf).ShouldBeEmpty();
        var doc = FeXml.Parse(text)!;
        FeXml.SignatureIsValid(doc).ShouldBeTrue();
        var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(doc.GetElementsByTagName("X509Certificate", "http://www.w3.org/2000/09/xmldsig#")[0]!.InnerText));
        return (FeXml.Text(doc, "/ARECF/DetalleAcusedeRecibo/Estado")!, FeXml.Text(doc, "/ARECF/DetalleAcusedeRecibo/CodigoMotivoNoRecibido"), certificate.Thumbprint);
    }

    /// <summary>A 31 from the supplier to <paramref name="buyerRnc"/>: RD$1,000 + 18 % ITBIS, built and signed by the store's own code.</summary>
    private static string SupplierEcf(string buyerRnc, string encf, bool sign = true, string item = "Res para guisar (caja)", X509Certificate2? signer = null,
        string supplierName = "Distribuidora del Cibao SRL")
    {
        var line = new FiscalLine(item, 1, 1180m, 0, ItbisRate.General18, 180m, 1180m);
        var doc = new DocumentToIssue(EcfType.FiscalCredit31, Guid.NewGuid(), buyerRnc, "Carnicería de prueba", [line], 180m, 1180m, null, null, EcfPaymentType.Cash,
            [new FiscalPayment(EcfPaymentForm.Cash, 1180m)]);
        var supplier = new BusinessProfile(supplierName, SupplierRnc, "Calle del Sol 10, Santiago", null, null, 10m);
        var xml = EcfXml.Build(doc, encf, supplier, new DateTime(2026, 10, 15, 15, 0, 0, DateTimeKind.Utc), new DateOnly(2028, 12, 31), null);
        return sign ? new CertificateEcfSigner(signer ?? SupplierCertificate).Sign(xml).SignedXml : xml.ToString(SaveOptions.DisableFormatting);
    }

    private static MultipartFormDataContent Xml(string xml) => Xml(Encoding.UTF8.GetBytes(xml));

    private static MultipartFormDataContent Xml(byte[] xml)
    {
        var file = new ByteArrayContent(xml);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        return new MultipartFormDataContent { { file, "xml", "documento.xml" } };
    }

    private static X509Certificate2 Certificate(string subject, out byte[] pfx)
    {
        using var rsa = RSA.Create(2048);
        var cert = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        pfx = cert.Export(X509ContentType.Pkcs12, "prueba");
        return X509CertificateLoader.LoadPkcs12(pfx, "prueba", X509KeyStorageFlags.EphemeralKeySet);
    }

    private static string NewRnc() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
