using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Fiscal;
using Toki.Fiscal.Domain;
using Toki.IntegrationTests.Infra;
using Toki.IntegrationTests.Sync;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>A certified store in TesteCF whose buyers' receiver services answer from memory.</summary>
public sealed class ExchangeTokiApp : DgiiTokiApp
{
    public FakeBuyerReceiver Buyer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddHttpClient(EcfDeliveryQueue.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Buyer));
    }
}

/// <summary>An electronic buyer's receiver as the DGII describes it (§2.6): seed authentication, then reception answering a signed ARECF.</summary>
public sealed class FakeBuyerReceiver : HttpMessageHandler
{
    public const string Url = "https://comprador.test";
    private static readonly CertificateEcfSigner Signer = new(Certificate());

    public bool Down { get; set; }
    public List<(string Path, string? Authorization, string? FileName, string? Xml)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down) throw new HttpRequestException("No such host is known. (comprador.test:443)");
        var (file, xml) = ((string?)null, (string?)null);
        if (request.Content is MultipartFormDataContent multipart)
            foreach (var part in multipart) (file, xml) = (part.Headers.ContentDisposition!.FileName?.Trim('"'), await part.ReadAsStringAsync(cancellationToken));
        var path = request.RequestUri!.AbsolutePath;
        lock (Requests) Requests.Add((path, request.Headers.Authorization?.ToString(), file, xml));

        if (path == "/fe/autenticacion/api/semilla")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"<SemillaModel><valor>{Guid.NewGuid():N}</valor><fecha>2026-10-15T11:00:00</fecha></SemillaModel>") };
        if (path == "/fe/autenticacion/api/validacioncertificado")
            return xml!.Contains("<SignatureValue>", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { token = "buyer-token", expira = "2099-01-01T00:00:00Z" }) }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (path != "/fe/recepcion/api/ecf") return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.Headers.Authorization?.ToString() != "Bearer buyer-token") return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ecf = XDocument.Parse(xml!);
        string Value(string name) => ecf.Descendants(name).First().Value;
        var ack = new XDocument(new XElement("ARECF", new XElement("DetalleAcusedeRecibo", new XElement("Version", "1.0"),
            new XElement("RNCEmisor", Value("RNCEmisor")), new XElement("RNCComprador", Value("RNCComprador")), new XElement("eNCF", Value("eNCF")),
            new XElement("Estado", 0), new XElement("FechaHoraAcuseRecibo", "15-10-2026 11:00:00"))));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Signer.Sign(ack).SignedXml) };
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=Firmante Comprador", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.EphemeralKeySet);
    }
}

/// <summary>TK-153 (an accepted 31 delivered to an electronic buyer) and TK-154 (received e-CF pulled from the cloud, the commercial approval and the
/// receiver consent, all through the node's cloud channel), against the fake DGII, buyer and cloud.</summary>
public sealed class EcfExchangeTests(ExchangeTokiApp app) : IClassFixture<ExchangeTokiApp>
{
    private const string ElectronicBuyer = "101672919";

    [Fact]
    public async Task An_accepted_31_reaches_an_electronic_buyer_retrying_while_it_is_down_and_its_ARECF_is_kept()
    {
        var admin = await app.SetUpAsync();
        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);
        app.Dgii.Directory[ElectronicBuyer] = FakeBuyerReceiver.Url;
        app.Buyer.Down = true;

        var electronic = (await cashier.SellAsync(soda, 59m, "FiscalCredit31", ElectronicBuyer)).GetProperty("eNcf").GetString()!;
        var paper = (await cashier.SellAsync(soda, 59m, "FiscalCredit31", "131313131")).GetProperty("eNcf").GetString()!;
        await DeliverAsync(); // not accepted by the DGII yet: nothing to deliver
        (await DeliveriesAsync()).ShouldBeEmpty();
        await app.DrainAsync();

        await DeliverAsync();
        var first = await DeliveriesAsync();
        first.Single(d => d.ENcf == paper).Status.ShouldBe(DeliveryStatus.NotElectronic);
        var waiting = first.Single(d => d.ENcf == electronic);
        (waiting.Status, waiting.Attempts).ShouldBe((DeliveryStatus.Pending, 1));
        waiting.NextAttemptUtc.ShouldNotBeNull();
        waiting.Message.ShouldNotBeNull().ShouldContain("comprador.test");

        app.Buyer.Down = false;
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await DeliverAsync();
        var delivered = (await DeliveriesAsync()).Single(d => d.ENcf == electronic);
        (delivered.Status, delivered.DeliveredUtc is null).ShouldBe((DeliveryStatus.Delivered, false));
        XDocument.Parse(EcfSubmissionQueue.Unzip(delivered.AckXml!)).Descendants("Estado").Single().Value.ShouldBe("0");
        var sent = app.Buyer.Requests.Single(r => r.Path == "/fe/recepcion/api/ecf" && r.Authorization is not null);
        sent.FileName.ShouldBe($"{DgiiStore.Rnc}{electronic}.xml");
        sent.Xml.ShouldBe(EcfSubmissionQueue.Unzip((await app.DocumentAsync(electronic)).SignedXml)); // the signed e-CF, untouched

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/deliveries");
        list.EnumerateArray().Select(d => d.GetProperty("status").GetString()).ShouldBe(["Delivered", "NotElectronic"], ignoreOrder: true);

        // A buyer that stays down for 72 hours: given up, the DGII queue never waited for it.
        app.Buyer.Down = true;
        var late = (await cashier.SellAsync(soda, 59m, "FiscalCredit31", ElectronicBuyer)).GetProperty("eNcf").GetString()!;
        await app.DrainAsync();
        (await app.DocumentAsync(late)).Status.ShouldBe(SubmissionStatus.Accepted);
        await DeliverAsync();
        app.Clock.Advance(TimeSpan.FromHours(73));
        await DeliverAsync();
        (await DeliveriesAsync()).Single(d => d.ENcf == late).Status.ShouldBe(DeliveryStatus.Failed);
    }

    private Task<int> DeliverAsync() => app.InScopeAsync(sp => sp.GetRequiredService<EcfDeliveryQueue>().ProcessAsync(CancellationToken.None));

    private Task<List<EcfDelivery>> DeliveriesAsync() =>
        app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Set<EcfDelivery>().AsNoTracking().ToListAsync());
}

/// <summary>TK-154 and TK-151 at the store, on its own app: the delivery test moves the clock past the admin's session.</summary>
public sealed class ReceivedDocumentsTests(ExchangeTokiApp app) : IClassFixture<ExchangeTokiApp>
{
    [Fact]
    public async Task Received_e_CF_come_down_from_the_cloud_and_the_approval_and_the_consent_go_through_it()
    {
        var admin = await app.SetUpAsync();
        var item = new
        {
            id = 7L, environment = "testecf", issuerRnc = "101672919", issuerName = "Distribuidora del Cibao SRL", eNcf = "E310000000005", type = 31,
            issuedOn = "2026-10-15", total = 1180m, itbis = 180m, receivedUtc = DateTime.UtcNow, xmlGzip = Convert.ToBase64String(EcfSubmissionQueue.Zip("<ECF/>")),
            approval = (string?)null, approvalReason = (string?)null, approvalUtc = (DateTime?)null, approvalDeliveredUtc = (DateTime?)null,
            approvalDgiiUtc = (DateTime?)null, approvalMessage = (string?)null,
        };
        var consent = new { file = "cGZ4", password = "secreta", ownerName = "Ana Pérez", holderName = "Ana Pérez", accepted = true };

        // Not connected to the cloud: nothing to pull, and the receiver says why.
        (await PullAsync()).ShouldBe(0);
        (await (await admin.PostAsJsonAsync("/api/fiscal/receiver/consent", consent)).ErrorCode()).ShouldBe("Fiscal.CloudNotConnected");

        await app.ConnectAsync();
        app.Cloud.Respond = path =>
        {
            var body = app.Cloud.Requests.Last().Body;
            return path switch
            {
                ReceivedDocumentsPuller.PullPath => Json(new { items = body.GetProperty("after").GetInt64() < 7 ? new[] { item } : [] }),
                "/fiscal/v1/received/7/approval" => Json(item with { approval = "Rejected", approvalReason = body.GetProperty("reason").GetString(), approvalUtc = (DateTime?)DateTime.UtcNow }),
                "/fiscal/v1/receiver/certificate" when body.GetProperty("holderName").GetString() == "" =>
                    new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { code = "Fe.ConsentRequired", detail = "Falta el consentimiento." }) },
                "/fiscal/v1/receiver/certificate" or "/fiscal/v1/receiver/status" => Json(new { active = true, rnc = DgiiStore.Rnc, subject = "CN=Ana Pérez" }),
                _ => null,
            };
        };

        (await PullAsync()).ShouldBe(1);
        (await PullAsync()).ShouldBe(0); // the cursor is the last cloud id
        var received = (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/received")).EnumerateArray().ShouldHaveSingleItem();
        (received.GetProperty("eNcf").GetString(), received.GetProperty("total").GetDecimal(), received.GetProperty("issuedOn").GetString())
            .ShouldBe(("E310000000005", 1180m, "2026-10-15"));

        var id = received.GetProperty("id").GetGuid();
        var approval = await admin.PostAsJsonAsync($"/api/fiscal/received/{id}/approval", new { approve = false, reason = "Mercancía dañada" });
        approval.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await approval.Json()).GetProperty("approval").GetString().ShouldBe("Rejected");
        app.Cloud.To("/fiscal/v1/received/7/approval").Single().Body.GetProperty("reason").GetString().ShouldBe("Mercancía dañada");
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/received"))[0].GetProperty("approvalReason").GetString().ShouldBe("Mercancía dañada");

        // The consent goes with the business RNC and the text version; a refusal of the cloud keeps its code.
        (await admin.PostAsJsonAsync("/api/fiscal/receiver/consent", consent)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var sent = app.Cloud.To("/fiscal/v1/receiver/certificate").Last().Body;
        (sent.GetProperty("rnc").GetString(), sent.GetProperty("consentVersion").GetString(), sent.GetProperty("password").GetString())
            .ShouldBe((DgiiStore.Rnc, Toki.Fiscal.Features.ReceiverCloud.ConsentVersion, "secreta"));
        (await (await admin.PostAsJsonAsync("/api/fiscal/receiver/consent", consent with { holderName = "" })).ErrorCode()).ShouldBe("Fe.ConsentRequired");
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/receiver")).GetProperty("active").GetBoolean().ShouldBeTrue();
    }

    private Task<int> PullAsync() => app.InScopeAsync(sp => sp.GetRequiredService<ReceivedDocumentsPuller>().PullAsync(CancellationToken.None));

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
