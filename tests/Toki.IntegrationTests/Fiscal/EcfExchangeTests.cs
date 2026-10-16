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
        if (path is not ("/fe/recepcion/api/ecf" or "/fe/aprobacioncomercial/api/ecf")) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (request.Headers.Authorization?.ToString() != "Bearer buyer-token") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (path == "/fe/aprobacioncomercial/api/ecf") return new HttpResponseMessage(HttpStatusCode.OK); // TK-246: an ACECF from the store

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
        // R9 #19: a 31 accepted long before the deliveries existed (the update that brought TK-153) is not handed out.
        var old = (await cashier.SellAsync(soda, 59m, "FiscalCredit31", ElectronicBuyer)).GetProperty("eNcf").GetString()!;
        await app.DrainAsync();
        (await app.DocumentAsync(late)).Status.ShouldBe(SubmissionStatus.Accepted);
        await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.Where(d => d.ENcf == old)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ResultUtc, app.Clock.GetUtcNow().UtcDateTime.AddDays(-30))));
        await DeliverAsync();
        app.Clock.Advance(TimeSpan.FromHours(73));
        await DeliverAsync();
        (await DeliveriesAsync()).Single(d => d.ENcf == late).Status.ShouldBe(DeliveryStatus.Failed);
        (await DeliveriesAsync()).ShouldNotContain(d => d.ENcf == old);
    }

    private Task<int> DeliverAsync() => app.InScopeAsync(sp => sp.GetRequiredService<EcfDeliveryQueue>().ProcessAsync(CancellationToken.None));

    private Task<List<EcfDelivery>> DeliveriesAsync() =>
        app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Set<EcfDelivery>().AsNoTracking().ToListAsync());
}


/// <summary>TK-154 and TK-246 at the store, on its own app: the delivery test moves the clock past the admin's session. Received e-CF come down from
/// the cloud; the receiver is activated with the store's signed proof (or, CloudCustody, the .p12); the commercial approval is signed here and sent from
/// here to the supplier and the DGII.</summary>
public sealed class ReceivedDocumentsTests(ExchangeTokiApp app) : IClassFixture<ExchangeTokiApp>
{
    private const string Supplier = "101672919";

    [Fact]
    public async Task Received_e_CF_come_down_the_receiver_is_activated_with_a_signed_proof_and_the_approval_is_signed_and_sent_from_the_store()
    {
        var admin = await app.SetUpAsync();
        var item = new
        {
            id = 7L, environment = "testecf", issuerRnc = Supplier, issuerName = "Distribuidora del Cibao SRL", eNcf = "E310000000005", type = 31,
            issuedOn = "2026-10-15", total = 1180m, itbis = 180m, receivedUtc = DateTime.UtcNow, xmlGzip = Convert.ToBase64String(EcfSubmissionQueue.Zip("<ECF/>")),
            approval = (string?)null, approvalReason = (string?)null, approvalUtc = (DateTime?)null, approvalDeliveredUtc = (DateTime?)null,
            approvalDgiiUtc = (DateTime?)null, approvalMessage = (string?)null,
        };
        var consent = new { mode = "StoreSigned", file = (string?)null, password = (string?)null, ownerName = "Ana Pérez", holderName = "Ana Pérez", accepted = true };

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
                "/fiscal/v1/receiver/challenge" => Json(new { challenge = "reto-de-la-nube" }),
                "/fiscal/v1/receiver/activate" or "/fiscal/v1/receiver/certificate" or "/fiscal/v1/receiver/status" =>
                    Json(new { active = true, rnc = DgiiStore.Rnc, subject = "CN=Firmante de Prueba", signingMode = path.EndsWith("certificate", StringComparison.Ordinal) ? "CloudCustody" : "StoreSigned" }),
                _ => null,
            };
        };

        (await PullAsync()).ShouldBe(1);
        (await PullAsync()).ShouldBe(0); // the cursor is the last cloud id
        var received = (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/received")).EnumerateArray().ShouldHaveSingleItem();
        (received.GetProperty("eNcf").GetString(), received.GetProperty("total").GetDecimal(), received.GetProperty("issuedOn").GetString())
            .ShouldBe(("E310000000005", 1180m, "2026-10-15"));

        // StoreSigned (default): the cloud's challenge and the RNC, signed here; no certificate and no password go.
        (await admin.PostAsJsonAsync("/api/fiscal/receiver/consent", consent)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var activation = app.Cloud.To("/fiscal/v1/receiver/activate").Single().Body;
        activation.GetProperty("consentVersion").GetString().ShouldBe(Toki.Fiscal.Features.ReceiverCloud.StoreSignedConsentVersion);
        activation.TryGetProperty("password", out _).ShouldBeFalse();
        var proof = XDocument.Parse(activation.GetProperty("proof").GetString()!);
        (proof.Root!.Name.LocalName, proof.Root.Element("RNC")!.Value, proof.Root.Element("Reto")!.Value).ShouldBe(("ActivacionReceptor", DgiiStore.Rnc, "reto-de-la-nube"));
        SignatureIsValid(proof.ToString(SaveOptions.DisableFormatting)).ShouldBeTrue();
        // CloudCustody: the .p12 goes once with its password and the TK-151 consent text.
        (await admin.PostAsJsonAsync("/api/fiscal/receiver/consent", consent with { mode = "CloudCustody", file = "cGZ4", password = "secreta" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var custody = app.Cloud.To("/fiscal/v1/receiver/certificate").Single().Body;
        (custody.GetProperty("rnc").GetString(), custody.GetProperty("consentVersion").GetString(), custody.GetProperty("password").GetString())
            .ShouldBe((DgiiStore.Rnc, Toki.Fiscal.Features.ReceiverCloud.ConsentVersion, "secreta"));
        (await app.InScopeAsync(sp => sp.GetRequiredService<Toki.BuildingBlocks.Persistence.InfraDbContext>().AuditRecords.CountAsync(a => a.Action == "fiscal.receiver-consent")))
            .ShouldBe(2);

        // The approval: signed here, to the supplier's approval URL (its seed signed here too) and the DGII's copy; nothing goes through the cloud.
        app.Dgii.Directory[Supplier] = FakeBuyerReceiver.Url;
        var id = received.GetProperty("id").GetGuid();
        (await (await admin.PostAsJsonAsync($"/api/fiscal/received/{id}/approval", new { approve = false })).ErrorCode()).ShouldBe("Fiscal.ApprovalReasonRequired");
        var approval = await admin.PostAsJsonAsync($"/api/fiscal/received/{id}/approval", new { approve = false, reason = "Mercancía dañada" });
        approval.StatusCode.ShouldBe(HttpStatusCode.OK, await approval.Content.ReadAsStringAsync());
        var answer = await approval.Json();
        (answer.GetProperty("approval").GetString(), answer.GetProperty("approvalReason").GetString()).ShouldBe(("Rejected", "Mercancía dañada"));
        answer.GetProperty("approvalDeliveredUtc").ValueKind.ShouldBe(JsonValueKind.String);
        answer.GetProperty("approvalDgiiUtc").ValueKind.ShouldBe(JsonValueKind.String);

        var toSupplier = app.Buyer.Requests.Single(r => r.Path == "/fe/aprobacioncomercial/api/ecf");
        toSupplier.Authorization.ShouldBe("Bearer buyer-token");
        toSupplier.FileName.ShouldBe($"{DgiiStore.Rnc}E310000000005.xml");
        var toDgii = app.Dgii.To("/aprobacioncomercial/api/aprobacioncomercial").ShouldHaveSingleItem();
        toDgii.Xml.ShouldBe(toSupplier.Xml);
        var acecf = XDocument.Parse(toSupplier.Xml!);
        EcfXml.Validate(acecf, EcfXml.AcecfSchema).ShouldBeEmpty();
        SignatureIsValid(toSupplier.Xml!).ShouldBeTrue();
        acecf.Descendants("Estado").Single().Value.ShouldBe("2");
        acecf.Descendants("RNCComprador").Single().Value.ShouldBe(DgiiStore.Rnc);
        app.Cloud.Requests.ShouldNotContain(r => r.Path.Contains("approval", StringComparison.Ordinal));
        // The cloud learns of it through the outbox.
        (await app.InScopeAsync(sp => sp.GetRequiredService<Toki.BuildingBlocks.Persistence.InfraDbContext>().Outbox
            .CountAsync(m => m.Type == typeof(Toki.Fiscal.Contracts.CommercialApprovalRecordedV1).FullName))).ShouldBe(1);

        // The decision cannot change; pressing again sends nothing that already went.
        (await (await admin.PostAsJsonAsync($"/api/fiscal/received/{id}/approval", new { approve = true })).ErrorCode()).ShouldBe("Fiscal.ApprovalAlreadySigned");
        (await admin.PostAsJsonAsync($"/api/fiscal/received/{id}/approval", new { approve = false, reason = "otra vez" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Buyer.Requests.Count(r => r.Path == "/fe/aprobacioncomercial/api/ecf").ShouldBe(1);
        app.Dgii.To("/aprobacioncomercial/api/aprobacioncomercial").Count().ShouldBe(1);
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/received"))[0].GetProperty("approvalReason").GetString().ShouldBe("Mercancía dañada");
    }

    private Task<int> PullAsync() => app.InScopeAsync(sp => sp.GetRequiredService<ReceivedDocumentsPuller>().PullAsync(CancellationToken.None));

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    internal static bool SignatureIsValid(string xml)
    {
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);
        var signed = new System.Security.Cryptography.Xml.SignedXml(doc);
        signed.LoadXml((System.Xml.XmlElement)doc.GetElementsByTagName("Signature", System.Security.Cryptography.Xml.SignedXml.XmlDsigNamespaceUrl)[0]!);
        var certificate = signed.KeyInfo.OfType<System.Security.Cryptography.Xml.KeyInfoX509Data>().SelectMany(d => d.Certificates!.OfType<X509Certificate2>()).First();
        return signed.CheckSignature(certificate, verifySignatureOnly: true);
    }
}

/// <summary>TK-246: the store signs for its cloud receiver on request (StoreSigned): only acknowledgements of this business, each one audited.</summary>
public sealed class RemoteSigningTests(ExchangeTokiApp app) : IClassFixture<ExchangeTokiApp>
{
    [Fact]
    public async Task The_store_signs_only_this_business_acknowledgements_on_the_clouds_request_refuses_anything_else_and_audits_each_one()
    {
        await app.SetUpAsync();
        (await PollAsync()).ShouldBe(RemoteSigning.Idle); // not connected: nothing to ask
        await app.ConnectAsync();

        string Arecf(string buyer, int estado) => new XDocument(new XElement("ARECF", new XElement("DetalleAcusedeRecibo", new XElement("Version", "1.0"),
            new XElement("RNCEmisor", "101672919"), new XElement("RNCComprador", buyer), new XElement("eNCF", "E310000000011"), new XElement("Estado", estado),
            estado == 0 ? null : new XElement("CodigoMotivoNoRecibido", 4), new XElement("FechaHoraAcuseRecibo", "15-10-2026 11:00:00")))).ToString(SaveOptions.DisableFormatting);
        var requests = new Queue<(Guid Id, string Xml)>([
            (Guid.NewGuid(), Arecf(DgiiStore.Rnc, 0)),
            (Guid.NewGuid(), Arecf("130000001", 1)), // a refusal names the RNC the e-CF was for
            (Guid.NewGuid(), Arecf("130000001", 0)), // a receipt for another business
            (Guid.NewGuid(), "<SemillaModel><valor>semilla-de-la-dgii</valor><fecha>2026-10-15T11:00:00</fecha></SemillaModel>"),
            (Guid.NewGuid(), "<ACECF><DetalleAprobacionComercial/></ACECF>"),
            (Guid.NewGuid(), "<ECF/>"),
            (Guid.NewGuid(), "no es xml"),
        ]);
        var asked = requests.ToList();
        app.Cloud.Respond = path => path switch
        {
            RemoteSigning.NextPath => requests.TryDequeue(out var r) ? Json(new { id = r.Id, xml = r.Xml }) : new HttpResponseMessage(HttpStatusCode.NoContent),
            _ when path.StartsWith("/fiscal/v1/receiver/sign-requests/", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => null,
        };

        for (var i = 0; i < asked.Count; i++) (await PollAsync()).ShouldBe(TimeSpan.Zero);
        (await PollAsync()).ShouldBe(TimeSpan.Zero); // nothing came (204): poll again at once

        var answers = asked.Select(a => app.Cloud.To($"/fiscal/v1/receiver/sign-requests/{a.Id}").Single().Body).ToList();
        string? Signed(int i) => answers[i].GetProperty("signedXml").GetString();
        ReceivedDocumentsTests.SignatureIsValid(Signed(0)!).ShouldBeTrue();
        XDocument.Parse(Signed(0)!).Root!.Name.LocalName.ShouldBe("ARECF");
        Signed(1).ShouldNotBeNull();
        foreach (var refused in new[] { 2, 3, 4, 5, 6 })
        {
            Signed(refused).ShouldBeNull();
            answers[refused].GetProperty("refused").GetString().ShouldNotBeNullOrEmpty();
        }
        answers[3].GetProperty("refused").GetString()!.ShouldContain("SemillaModel");
        app.Cloud.To(RemoteSigning.NextPath).ShouldAllBe(r => r.Authorization!.StartsWith("Node ", StringComparison.Ordinal) && app.Cloud.SignedByNode(r));

        var audit = await app.InScopeAsync(sp => sp.GetRequiredService<Toki.BuildingBlocks.Persistence.InfraDbContext>().AuditRecords
            .Where(a => a.ResourceType == "RemoteSignature").Select(a => a.Action).ToListAsync());
        audit.Count(a => a == "fiscal.remote-sign").ShouldBe(2);
        audit.Count(a => a == "fiscal.remote-sign-refused").ShouldBe(5);
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    private Task<TimeSpan> PollAsync() => app.InScopeAsync(sp => sp.GetRequiredService<RemoteSigning>().PollOnceAsync(CancellationToken.None));

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}