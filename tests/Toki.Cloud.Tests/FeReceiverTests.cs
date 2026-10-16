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
        builder.ConfigureTestServices(s => s.AddHttpClient(FeOutbound.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Outside));
    }
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
    private static readonly X509Certificate2 SupplierCertificate = Certificate("CN=Firmante Proveedor, SERIALNUMBER=00198765432", out _);

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
        Certificate("CN=Otro", out var pfx);
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

        var toSupplier = app.Outside.XmlTo("/fe/aprobacioncomercial/api/ecf").ShouldHaveSingleItem()!;
        app.Outside.XmlTo("/aprobacioncomercial/api/aprobacioncomercial").ShouldHaveSingleItem().ShouldBe(toSupplier);
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
        app.Outside.XmlTo("/fe/aprobacioncomercial/api/ecf").Count().ShouldBe(1); // nothing missing: nothing sent again
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

    // --- helpers ---

    private async Task<(StoreNode Node, Owner Owner, string Rnc, X509Certificate2 Certificate)> ReceiverAsync()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var rnc = NewRnc();
        var certificate = Certificate("CN=Ana Pérez, SERIALNUMBER=00112345678", out var pfx);
        var r = await node.Client.PostAsJsonAsync("/fiscal/v1/receiver/certificate",
            new { file = Convert.ToBase64String(pfx), password = "prueba", rnc, ownerName = "Ana Pérez", holderName = "Ana Pérez", accepted = true, consentVersion = FeConsent.Version });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (node, owner, rnc, certificate);
    }

    /// <summary>A supplier authenticated at the tenant's receiver: seed → signed with its certificate → token.</summary>
    private async Task<HttpClient> SupplierClientAsync(string rnc)
    {
        var client = app.CreateClient();
        var seed = XDocument.Parse(await client.GetStringAsync($"/testecf/{rnc}/fe/autenticacion/api/semilla"));
        var bad = await client.PostAsync($"/testecf/{rnc}/fe/autenticacion/api/validacioncertificado", Xml(seed.ToString()));
        bad.StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // unsigned
        var signed = new CertificateEcfSigner(SupplierCertificate).Sign(seed).SignedXml;
        var token = await (await client.PostAsync($"/testecf/{rnc}/fe/autenticacion/api/validacioncertificado", Xml(signed))).Json();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("token").GetString());
        return client;
    }

    private static async Task<(string Estado, string? Reason, string? SignedBy)> AckAsync(HttpClient supplier, string rnc, string ecf)
    {
        var r = await supplier.PostAsync($"/testecf/{rnc}/fe/recepcion/api/ecf", Xml(ecf));
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await r.Content.ReadAsStringAsync();
        FeXml.Validate(text, FeXml.Arecf).ShouldBeEmpty();
        var doc = FeXml.Parse(text)!;
        FeXml.SignatureIsValid(doc).ShouldBeTrue();
        var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(doc.GetElementsByTagName("X509Certificate", "http://www.w3.org/2000/09/xmldsig#")[0]!.InnerText));
        return (FeXml.Text(doc, "/ARECF/DetalleAcusedeRecibo/Estado")!, FeXml.Text(doc, "/ARECF/DetalleAcusedeRecibo/CodigoMotivoNoRecibido"), certificate.Thumbprint);
    }

    /// <summary>A 31 from the supplier to <paramref name="buyerRnc"/>: RD$1,000 + 18 % ITBIS, built and signed by the store's own code.</summary>
    private static string SupplierEcf(string buyerRnc, string encf, bool sign = true)
    {
        var line = new FiscalLine("Res para guisar (caja)", 1, 1180m, 0, ItbisRate.General18, 180m, 1180m);
        var doc = new DocumentToIssue(EcfType.FiscalCredit31, Guid.NewGuid(), buyerRnc, "Carnicería de prueba", [line], 180m, 1180m, null, null, EcfPaymentType.Cash,
            [new FiscalPayment(EcfPaymentForm.Cash, 1180m)]);
        var supplier = new BusinessProfile("Distribuidora del Cibao SRL", SupplierRnc, "Calle del Sol 10, Santiago", null, null, 10m);
        var xml = EcfXml.Build(doc, encf, supplier, new DateTime(2026, 10, 15, 15, 0, 0, DateTimeKind.Utc), new DateOnly(2028, 12, 31), null);
        return sign ? new CertificateEcfSigner(SupplierCertificate).Sign(xml).SignedXml : xml.ToString(SaveOptions.DisableFormatting);
    }

    private static MultipartFormDataContent Xml(string xml)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(xml));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        return new MultipartFormDataContent { { file, "xml", "documento.xml" } };
    }

    private static X509Certificate2 Certificate(string subject, out byte[] pfx)
    {
        using var rsa = RSA.Create(2048);
        var cert = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        pfx = cert.Export(X509ContentType.Pkcs12, "prueba");
        return X509CertificateLoader.LoadPkcs12(pfx, "prueba", X509KeyStorageFlags.EphemeralKeySet);
    }

    private static string NewRnc() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
