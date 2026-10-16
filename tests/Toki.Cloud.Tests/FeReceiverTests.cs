using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
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

/// <summary>The cloud receiver with the test authority as INDOTEL's, both signing modes allowed (TK-246) and short waits for the store's signature.</summary>
public sealed class FeCloudApp : CloudApp
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Fe:TrustedRoots:0", TestPki.RootBase64); // R9 #1: the test authority stands in for INDOTEL's
        builder.UseSetting("Fe:CheckRevocation", "false"); // the test authority publishes no CRL
        builder.UseSetting("Fe:AllowCloudCustody", "true");
        builder.UseSetting("Fe:LateAcksInBackground", "false");
        builder.UseSetting("Fe:SignTimeoutSeconds", "3");
        builder.UseSetting("Fe:SignPollSeconds", "1");
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

/// <summary>TK-246: the store's main PC as the cloud sees it in StoreSigned mode: a long poll for sign requests, answered with the store's own signer
/// (or with what a test makes it answer), until disposed.</summary>
public sealed class FakeStoreSigner : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    /// <summary>What to post back for a request (the XML to sign); null: never answer. Default: signed with the store's certificate.</summary>
    public Func<string, string?> Answer { get; set; }
    public List<string> Requests { get; } = [];

    public FakeStoreSigner(HttpClient node, X509Certificate2 certificate)
    {
        Answer = xml => new CertificateEcfSigner(certificate).Sign(XDocument.Parse(xml)).SignedXml;
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var r = await node.PostAsJsonAsync("/fiscal/v1/receiver/sign-requests/next", new { }, _stop.Token);
                    if (r.StatusCode != HttpStatusCode.OK)
                    {
                        if (r.StatusCode != HttpStatusCode.NoContent) await Task.Delay(50, _stop.Token);
                        continue;
                    }
                    var request = await r.Content.ReadFromJsonAsync<JsonElement>(_stop.Token);
                    var xml = request.GetProperty("xml").GetString()!;
                    lock (Requests) Requests.Add(xml);
                    if (Answer(xml) is { } signed)
                        (await node.PostAsJsonAsync($"/fiscal/v1/receiver/sign-requests/{request.GetProperty("id").GetGuid()}", new { signedXml = signed }, _stop.Token)).Dispose();
                }
                catch (OperationCanceledException) { }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}

/// <summary>TK-151 to TK-154 and TK-246 in the cloud: the two signing modes (StoreSigned, CloudCustody), the restricted signer, the receiver services
/// with their authentication, the store's pull and the approvals synced from the store. The supplier's e-CF are built and signed exactly as a Toki
/// store does.</summary>
public sealed class FeReceiverTests(FeCloudApp app) : IClassFixture<FeCloudApp>
{
    private const string SupplierRnc = "101672919";
    /// <summary>The supplier's certificate (its own RNC), issued by the trusted test authority. R11 B-High2: a cédula signing for it is unverified.</summary>
    private static readonly X509Certificate2 SupplierCertificate = TestPki.Issue($"CN=Distribuidora, SERIALNUMBER={SupplierRnc}");
    /// <summary>A person's certificate (cédula) signing for the supplier's company: maybe its delegate, which nobody here can verify.</summary>
    private static readonly X509Certificate2 DelegateCertificate = TestPki.Issue("CN=Firmante Proveedor, SERIALNUMBER=00198765432");

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

        // TK-246: the screens offer StoreSigned first.
        var status = await (await node.Client.PostAsync("/fiscal/v1/receiver/status", null)).Json();
        (status.GetProperty("active").GetBoolean(), status.GetProperty("defaultSigningMode").GetString(), status.GetProperty("cloudCustodyAvailable").GetBoolean())
            .ShouldBe((false, FeSigningModes.StoreSigned, true));
    }

    [Fact]
    public async Task TK246_store_signed_the_store_signs_each_acknowledgement_over_the_node_channel_and_the_cloud_keeps_no_certificate()
    {
        var (node, owner, rnc, ours) = await StoreSignedReceiverAsync();
        var row = await app.InDbAsync(db => db.FeCertificates.AsNoTracking().SingleAsync(c => c.TenantId == owner.TenantId && c.RevokedUtc == null));
        (row.SigningMode, row.KeyName, row.Thumbprint).ShouldBe((FeSigningModes.StoreSigned, "", ours.Thumbprint));
        typeof(FeCertificate).GetProperties().ShouldNotContain(p => p.PropertyType == typeof(byte[])); // public facts only: no certificate, no key
        var status = await (await node.Client.PostAsync("/fiscal/v1/receiver/status", null)).Json();
        status.GetProperty("signingMode").GetString().ShouldBe(FeSigningModes.StoreSigned);

        await using var store = new FakeStoreSigner(node.Client, ours);
        await OnlineAsync(node);
        var supplier = await SupplierClientAsync(rnc);
        var ack = await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000701"));
        (ack.Estado, ack.SignedBy).ShouldBe(("0", ours.Thumbprint));
        (await AckAsync(supplier, rnc, SupplierEcf(NewRnc(), "E310000000702"))).Reason.ShouldBe("4"); // a refusal is signed by the store too
        store.Requests.Count.ShouldBe(2);
        store.Requests.ShouldAllBe(x => XDocument.Parse(x).Root!.Name == "ARECF");

        var signatures = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe")).GetProperty("signatures").EnumerateArray().ToList();
        signatures.Count(s => s.GetProperty("root").GetString() == "ARECF").ShouldBe(2);
        var item = (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        item.GetProperty("acknowledgedUtc").ValueKind.ShouldBe(JsonValueKind.String);

        // Another PC of the tenant does not sign for the receiver, and cannot answer for the one that does.
        var other = await app.StoreWithNodeAsync(owner, "Sucursal");
        (await (await other.Client.PostAsJsonAsync("/fiscal/v1/receiver/sign-requests/next", new { })).ErrorCode()).ShouldBe("Fe.NotSigningNode");
        (await (await other.Client.PostAsJsonAsync($"/fiscal/v1/receiver/sign-requests/{Guid.NewGuid()}", new { signedXml = "<x/>" })).ErrorCode())
            .ShouldBe("Fe.SignRequestGone");
    }

    [Fact]
    public async Task R11_a_cedula_for_a_company_RNC_activates_the_receiver_only_after_support_approves_it_and_from_one_PC()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var rnc = NewRnc();
        var person = TestPki.Issue("CN=Ana Pérez, SERIALNUMBER=00112345678");
        (await (await ActivateAsync(node, rnc, person)).ErrorCode()).ShouldBe("Fe.DelegationPending");
        (await (await ActivateAsync(node, rnc, person)).ErrorCode()).ShouldBe("Fe.DelegationPending"); // one request, still waiting

        // Toki support checks the delegation outside Toki and decides in the console (staff with 2FA only, audited).
        var staff = await app.StaffAsync();
        var id = (await staff.Client.GetFromJsonAsync<JsonElement>("/api/platform/fe/delegations")).EnumerateArray()
            .Single(d => d.GetProperty("rnc").GetString() == rnc).GetProperty("id").GetGuid();
        (await owner.Client.PostAsJsonAsync($"/api/platform/fe/delegations/{id}", new { approve = true })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await staff.Client.PostAsJsonAsync($"/api/platform/fe/delegations/{id}", new { approve = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await staff.Client.PostAsJsonAsync($"/api/platform/fe/delegations/{id}", new { approve = false })).StatusCode.ShouldBe(HttpStatusCode.NotFound); // it stands
        (await app.InDbAsync(db => db.PlatformAudit.AnyAsync(a => a.Path == $"/api/platform/fe/delegations/{id}" && a.Method == "POST"))).ShouldBeTrue();

        // Approved: the store activates again, and its acknowledgements signed with that very certificate count.
        (await ActivateAsync(node, rnc, person)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var store = new FakeStoreSigner(node.Client, person))
        {
            await OnlineAsync(node);
            var ack = await AckAsync(await SupplierClientAsync(rnc), rnc, SupplierEcf(rnc, "E310000000401"));
            (ack.Estado, ack.SignedBy).ShouldBe(("0", person.Thumbprint));
        }

        // R11 B-Med4: another PC of the business can neither take the receiver over nor withdraw it; an Owner can, in the panel.
        var other = await app.StoreWithNodeAsync(owner, "Sucursal");
        var company = TestPki.Issue($"CN=Empresa, SERIALNUMBER={rnc}");
        (await (await ActivateAsync(other, rnc, company)).ErrorCode()).ShouldBe("Fe.OtherNodeActive");
        (await (await other.Client.PostAsync("/fiscal/v1/receiver/revoke", null)).ErrorCode()).ShouldBe("Fe.OtherNodeActive");
        (await owner.Client.PostAsync("/api/panel/fe/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ActivateAsync(other, rnc, company)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A refused one stays refused.
        var stranger = TestPki.Issue("CN=Otra persona, SERIALNUMBER=00100000003");
        (await (await ActivateAsync(other, rnc, stranger)).ErrorCode()).ShouldBe("Fe.DelegationPending");
        var refusedId = (await staff.Client.GetFromJsonAsync<JsonElement>("/api/platform/fe/delegations")).EnumerateArray()
            .Single(d => d.GetProperty("rnc").GetString() == rnc && d.GetProperty("approved").ValueKind == JsonValueKind.Null).GetProperty("id").GetGuid();
        (await staff.Client.PostAsJsonAsync($"/api/platform/fe/delegations/{refusedId}", new { approve = false })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await ActivateAsync(other, rnc, stranger)).ErrorCode()).ShouldBe("Fe.DelegationRefused");
    }

    [Fact]
    public async Task TK246_store_off_answers_503_with_Retry_After_keeps_the_e_CF_pending_and_acknowledges_it_when_the_store_is_back()
    {
        var (node, owner, rnc, ours) = await StoreSignedReceiverAsync();
        var supplier = await SupplierClientAsync(rnc);
        var retried = SupplierEcf(rnc, "E310000000801");
        var late = SupplierEcf(rnc, "E310000000802");

        foreach (var ecf in new[] { retried, late })
        {
            var r = await supplier.PostAsync($"/testecf/{rnc}/fe/recepcion/api/ecf", Xml(ecf));
            r.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            r.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(FeReceiverEndpoints.RetryAfterSeconds));
        }
        // Kept, pending its acknowledgement; the store pulls it already; the panel shows the PC without connection.
        var pending = await app.InDbAsync(db => db.FeReceived.Where(d => d.TenantId == owner.TenantId).ToListAsync());
        pending.Count.ShouldBe(2);
        pending.ShouldAllBe(d => d.AcknowledgedUtc == null);
        (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items").GetArrayLength().ShouldBe(2);
        var offline = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe")).GetProperty("offline").EnumerateArray().ShouldHaveSingleItem();
        offline.GetProperty("node").GetString().ShouldBe("PC principal");
        offline.GetProperty("offlineSinceUtc").ValueKind.ShouldBe(JsonValueKind.String);

        await using var store = new FakeStoreSigner(node.Client, ours);
        await OnlineAsync(node);
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe")).GetProperty("offline")[0].GetProperty("offlineSinceUtc").ValueKind
            .ShouldBe(JsonValueKind.Null); // closed at the store's poll

        // The supplier's retry of the same bytes is received now (not a duplicate); the other one is acknowledged by the late pass and kept.
        (await AckAsync(supplier, rnc, retried)).Estado.ShouldBe("0");
        await using (var scope = app.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<FeLateAcknowledger>().RunAsync(CancellationToken.None)).ShouldBe(1);
        var saved = await app.InDbAsync(db => db.FeReceived.Where(d => d.TenantId == owner.TenantId).ToListAsync());
        saved.Count.ShouldBe(2);
        saved.ShouldAllBe(d => d.AcknowledgedUtc != null);
        var kept = await app.InDbAsync(db => db.FeSignatures.Where(s => s.TenantId == owner.TenantId && s.ENcf == "E310000000802").SingleAsync());
        var lateAck = FeXml.Parse(FeXml.Unzip(kept.XmlGzip!))!;
        (FeXml.Text(lateAck, "/ARECF/DetalleAcusedeRecibo/Estado"), FeXml.SignatureIsValid(lateAck)).ShouldBe(("0", true));
    }

    [Fact]
    public async Task TK246_a_store_that_does_not_answer_in_time_or_answers_another_signature_gives_no_acknowledgement()
    {
        var (node, owner, rnc, ours) = await StoreSignedReceiverAsync();
        await using var store = new FakeStoreSigner(node.Client, ours) { Answer = _ => null };
        await OnlineAsync(node);
        var supplier = await SupplierClientAsync(rnc);

        var started = DateTime.UtcNow;
        (await supplier.PostAsync($"/testecf/{rnc}/fe/recepcion/api/ecf", Xml(SupplierEcf(rnc, "E310000000901")))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (DateTime.UtcNow - started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2.5)); // waited for the store (Fe:SignTimeoutSeconds)

        // Signed by a trusted certificate of another RNC, or a different document than the one asked for: not an acknowledgement.
        store.Answer = xml => new CertificateEcfSigner(TestPki.Issue("CN=Otra, SERIALNUMBER=130000001")).Sign(XDocument.Parse(xml)).SignedXml;
        (await supplier.PostAsync($"/testecf/{rnc}/fe/recepcion/api/ecf", Xml(SupplierEcf(rnc, "E310000000902")))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        store.Answer = xml => new CertificateEcfSigner(ours).Sign(XDocument.Parse(xml.Replace("<Estado>1<", "<Estado>0<", StringComparison.Ordinal)
            .Replace("<CodigoMotivoNoRecibido>4</CodigoMotivoNoRecibido>", "", StringComparison.Ordinal))).SignedXml;
        (await supplier.PostAsync($"/testecf/{rnc}/fe/recepcion/api/ecf", Xml(SupplierEcf(NewRnc(), "E310000000903")))).StatusCode
            .ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await app.InDbAsync(db => db.FeSignatures.CountAsync(s => s.TenantId == owner.TenantId))).ShouldBe(0);
    }

    [Fact]
    public async Task TK246_activation_without_a_certificate_needs_a_signed_proof_of_the_RNC_and_switching_modes_deletes_the_vault_key()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var rnc = NewRnc();
        var own = TestPki.Issue($"CN=Empresa, SERIALNUMBER={rnc}", out var pfx);

        (await (await ActivateAsync(node, rnc, Certificate($"CN=Falsa, SERIALNUMBER={rnc}", out _))).ErrorCode()).ShouldBe("Fe.CertificateUntrusted");
        (await (await ActivateAsync(node, rnc, TestPki.Issue("CN=Otra, SERIALNUMBER=130000001"))).ErrorCode()).ShouldBe("Fe.CertificateRncMismatch");
        (await (await ActivateAsync(node, rnc, own, challenge: "inventado")).ErrorCode()).ShouldBe("Fe.ProofInvalid");
        (await (await ActivateAsync(node, rnc, own, proofRnc: NewRnc())).ErrorCode()).ShouldBe("Fe.ProofInvalid");
        // A challenge is this tenant's only.
        var otherTenant = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var foreign = (await (await otherTenant.Client.PostAsync("/fiscal/v1/receiver/challenge", null)).Json()).GetProperty("challenge").GetString();
        (await (await ActivateAsync(node, rnc, own, challenge: foreign)).ErrorCode()).ShouldBe("Fe.ProofInvalid");

        // CloudCustody first, then switching to StoreSigned: the vault key goes.
        (await node.Client.PostAsJsonAsync("/fiscal/v1/receiver/certificate", new
        {
            file = Convert.ToBase64String(pfx), password = "prueba", rnc, ownerName = "Ana", holderName = "Ana", accepted = true, consentVersion = FeConsent.Version,
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var keyName = await app.InDbAsync(db => db.FeCertificates.Where(c => c.TenantId == owner.TenantId && c.RevokedUtc == null).Select(c => c.KeyName).SingleAsync());
        (await app.Services.GetRequiredService<IFeCertificateVault>().KeyAsync(keyName, CancellationToken.None)).ShouldNotBeNull();
        var switched = await ActivateAsync(node, rnc, own);
        switched.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await switched.Json()).GetProperty("signingMode").GetString().ShouldBe(FeSigningModes.StoreSigned);
        await Should.ThrowAsync<InvalidOperationException>(() => app.Services.GetRequiredService<IFeCertificateVault>().KeyAsync(keyName, CancellationToken.None));

        // While the platform keeps CloudCustody off, nobody turns it on.
        using var off = app.WithWebHostBuilder(b => b.UseSetting("Fe:AllowCloudCustody", "false"));
        var offNode = await StoreWithNodeAsync(off);
        var refused = await offNode.PostAsJsonAsync("/fiscal/v1/receiver/certificate", new
        {
            file = Convert.ToBase64String(pfx), password = "prueba", rnc = NewRnc(), ownerName = "Ana", holderName = "Ana", accepted = true, consentVersion = FeConsent.Version,
        });
        (await refused.ErrorCode()).ShouldBe("Fe.CloudCustodyUnavailable");
        (await (await offNode.PostAsync("/fiscal/v1/receiver/status", null)).Json()).GetProperty("cloudCustodyAvailable").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_supplier_e_CF_is_acknowledged_with_a_signed_ARECF_kept_and_pulled_by_the_store()
    {
        var (node, owner, rnc, ours) = await ReceiverAsync();
        var status = await (await node.Client.PostAsync("/fiscal/v1/receiver/status", null)).Json();
        status.GetProperty("active").GetBoolean().ShouldBeTrue();
        status.GetProperty("signingMode").GetString().ShouldBe(FeSigningModes.CloudCustody);
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

        // Every signature is in the panel: the seed is the supplier's (signed at the supplier), so five acknowledgements.
        var panel = await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe");
        panel.GetProperty("signatures").EnumerateArray().Count(s => s.GetProperty("root").GetString() == "ARECF").ShouldBe(5);
        panel.GetProperty("status").GetProperty("rnc").GetString().ShouldBe(rnc);
    }

    [Fact]
    public async Task The_receiver_signer_refuses_anything_but_acknowledgements_and_approvals_even_a_seed()
    {
        var (_, owner, rnc, _) = await ReceiverAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var certificate = await db.FeCertificates.SingleAsync(c => c.TenantId == owner.TenantId && c.RevokedUtc == null);
        var signer = scope.ServiceProvider.GetRequiredService<FeSigner>();

        // TK-246 (council): a signed SemillaModel is a DGII token for the RNC; the cloud never signs one.
        foreach (var root in new[] { "ECF", "RFCE", "ANECF", "SemillaModel" })
            await Should.ThrowAsync<InvalidOperationException>(() => signer.SignAsync(certificate, new XDocument(new XElement(root)), null, false, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(() =>
            signer.SignAsync(certificate, new XDocument(new XElement(XName.Get("ARECF", "urn:otro"))), null, false, CancellationToken.None));
        (await signer.SignAsync(certificate, new XDocument(new XElement("ACECF", new XElement("x", "y"))), null, false, CancellationToken.None)).ShouldNotBeNull();

        // The same RNC cannot be claimed by a second tenant.
        var intruder = await app.StoreWithNodeAsync(await app.OwnerAsync());
        TestPki.Issue($"CN=Otro, SERIALNUMBER={rnc}", out var pfx);
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
    public async Task TK246_the_commercial_approval_is_decided_at_the_store_and_reaches_the_cloud_as_an_event()
    {
        var (node, owner, rnc, certificate) = await ReceiverAsync();
        (await AckAsync(await SupplierClientAsync(rnc), rnc, SupplierEcf(rnc, "E310000000021"))).Estado.ShouldBe("0");
        var id = (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items")[0].GetProperty("id").GetInt64();
        // The cloud no longer signs or sends approvals.
        (await node.Client.PostAsJsonAsync($"/fiscal/v1/received/{id}/approval", new { approve = true })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.Client.PostAsJsonAsync($"/api/panel/fe/received/{id}/approval", new { approve = true })).StatusCode
            .ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);

        var at = CloudApp.Today.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc);
        var acecf = StoreAcecf(certificate, "E310000000021");
        // R11 B-Med4: another store of the business cannot record it, nor the receiver's PC without the ACECF signed by its certificate.
        var other = await app.StoreWithNodeAsync(owner, "Sucursal");
        (await other.SendAsync(Events.Envelope(new CommercialApprovalRecordedV1(Guid.NewGuid(), id, "Approved", null, at, at, at, null, acecf), at.AddMinutes(-2))))
            .EnsureSuccessStatusCode();
        (await node.SendAsync(Events.Envelope(new CommercialApprovalRecordedV1(Guid.NewGuid(), id, "Approved", null, at, at, at, null, FeXml.Zip("<ACECF/>")), at.AddMinutes(-1))))
            .EnsureSuccessStatusCode();
        await app.ApplyAsync();
        (await app.InDbAsync(db => db.FeReceived.SingleAsync(d => d.Id == id))).Approval.ShouldBeNull();
        (await (await other.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items").GetArrayLength().ShouldBe(0);

        (await node.SendAsync(
            Events.Envelope(new CommercialApprovalRecordedV1(Guid.NewGuid(), id, "Rejected", "Llegó dañado", at, null, at, "No se pudo entregar al suplidor", acecf), at),
            Events.Envelope(new CommercialApprovalRecordedV1(Guid.NewGuid(), id, "Rejected", "Llegó dañado", at, at.AddMinutes(5), null, null, acecf), at.AddMinutes(5)),
            Events.Envelope(new CommercialApprovalRecordedV1(Guid.NewGuid(), id, "Approved", null, at, at, at, null, acecf), at.AddMinutes(6))))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.ApplyAsync();

        var shown = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe/received")).EnumerateArray().Single(d => d.GetProperty("id").GetInt64() == id);
        (shown.GetProperty("approval").GetString(), shown.GetProperty("approvalReason").GetString()).ShouldBe(("Rejected", "Llegó dañado")); // the first decision stays
        shown.GetProperty("approvalDeliveredUtc").ValueKind.ShouldBe(JsonValueKind.String);
        shown.GetProperty("approvalDgiiUtc").ValueKind.ShouldBe(JsonValueKind.String);
        shown.GetProperty("approvalMessage").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task TK250_an_owner_decides_the_approval_in_the_panel_one_decision_wins_and_only_the_receivers_PC_pulls_it_until_the_store_records_it()
    {
        var (node, owner, rnc, certificate) = await ReceiverAsync();
        var supplier = await SupplierClientAsync(rnc);
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000031"))).Estado.ShouldBe("0");
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000032"))).Estado.ShouldBe("0");
        async Task<JsonElement> PullAsync(StoreNode pc) => await (await pc.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json();
        var ids = (await PullAsync(node)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();
        var (a, b) = (ids[0], ids[1]);
        Task<HttpResponseMessage> Decide(Owner who, long id, bool approve, string? reason = null) =>
            who.Client.PostAsJsonAsync($"/api/panel/fe/received/{id}/decision", new { approve, reason });

        // Owners only; a rejection needs its reason; another business's document does not exist.
        var email = $"contador{Guid.NewGuid():N}@example.com";
        var invite = await (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role = "ReadOnly" })).Json();
        var link = invite.GetProperty("link").GetString()!;
        (await app.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = link[(link.IndexOf('#') + 1)..], password = CloudApp.Password }))
            .EnsureSuccessStatusCode();
        (await Decide(await app.SignInAsync(email), a, true)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await Decide(owner, a, false, "  ")).ErrorCode()).ShouldBe("Fe.ReasonRequired");
        (await (await Decide(await app.OwnerAsync(), a, true)).ErrorCode()).ShouldBe("Fe.NotFound");

        // Two decisions at once: one wins, the other is refused; the same one again changes nothing.
        var both = await Task.WhenAll(Decide(owner, a, true), Decide(owner, a, false, "Montos incorrectos"));
        both.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var lost = both.Single(r => r.StatusCode != HttpStatusCode.OK);
        (await lost.ErrorCode()).ShouldBe("Fe.AlreadyDecided");
        var won = (await both.Single(r => r.StatusCode == HttpStatusCode.OK).Json()).GetProperty("panelDecision").GetString()!;
        var again = await Decide(owner, a, won == "Approved", won == "Approved" ? null : "Montos incorrectos");
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Json()).GetProperty("approval").ValueKind.ShouldBe(JsonValueKind.Null); // pending: signed at the store

        // Only the PC that activated the receiver (it has the certificate) pulls it, with who decided.
        var pending = (await PullAsync(node)).GetProperty("decisions").EnumerateArray().ShouldHaveSingleItem();
        (pending.GetProperty("id").GetInt64(), pending.GetProperty("decision").GetString(), pending.GetProperty("decidedBy").GetString()).ShouldBe((a, won, owner.Email));
        (await PullAsync(await app.StoreWithNodeAsync(owner, "Sucursal"))).GetProperty("decisions").GetArrayLength().ShouldBe(0);

        // The store decided b first: the panel cannot decide otherwise. And a decision made at the store before it pulled the panel's wins.
        var at = CloudApp.Today.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc);
        var (acecfA, acecfB) = (StoreAcecf(certificate, "E310000000031"), StoreAcecf(certificate, "E310000000032"));
        var storeSays = won == "Approved" ? "Rejected" : "Approved";
        (await node.SendAsync(
            Events.Envelope(new CommercialApprovalRecordedV1(Guid.NewGuid(), b, "Rejected", "Llegó dañado", at, at, at, null, acecfB), at),
            Events.Envelope(new CommercialApprovalRecordedV1(Guid.NewGuid(), a, storeSays, storeSays == "Rejected" ? "En la tienda" : null, at, at, at, null, acecfA), at)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.ApplyAsync();
        (await (await Decide(owner, b, true)).ErrorCode()).ShouldBe("Fe.AlreadyDecided");
        (await Decide(owner, b, false, "Llegó dañado")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PullAsync(node)).GetProperty("decisions").GetArrayLength().ShouldBe(0);
        var shown = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe/received")).EnumerateArray().Single(d => d.GetProperty("id").GetInt64() == a);
        (shown.GetProperty("approval").GetString(), shown.GetProperty("panelDecision").GetString()).ShouldBe((storeSays, won));
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
        var (node, owner, rnc, _) = await ReceiverAsync();
        var supplier = await SupplierClientAsync(rnc);

        // Self-signed with the right cédula, from a trusted authority with another company's RNC, or with no RNC or cédula at all: signature reason 2.
        var selfSigned = Certificate("CN=Falso, SERIALNUMBER=00198765432", out _);
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000101", signer: selfSigned))).Reason.ShouldBe("2");
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000102", signer: TestPki.Issue("CN=Otra empresa, SERIALNUMBER=130000001")))).Reason.ShouldBe("2");
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000103", signer: TestPki.Issue("CN=Sin RNC")))).Reason.ShouldBe("2");
        // The supplier's own RNC on the certificate is fine.
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000104", signer: TestPki.Issue($"CN=Distribuidora, SERIALNUMBER={SupplierRnc}")))).Estado.ShouldBe("0");
        // R11 B-High2: a cédula for the supplier's company is received (the supplier did deliver it) but "firmante sin verificar": the panel shows
        // it flagged, the store never gets it (no purchase, no 606) and it cannot be decided.
        (await AckAsync(supplier, rnc, SupplierEcf(rnc, "E310000000105", signer: DelegateCertificate))).Estado.ShouldBe("0");
        var pulled = (await (await node.Client.PostAsJsonAsync("/fiscal/v1/received/pull", new { after = 0 })).Json()).GetProperty("items");
        pulled.EnumerateArray().Select(i => i.GetProperty("eNcf").GetString()).ShouldBe(["E310000000104"]);
        var unverified = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/fe/received")).EnumerateArray()
            .Single(d => d.GetProperty("eNcf").GetString() == "E310000000105");
        unverified.GetProperty("signerUnverified").GetBoolean().ShouldBeTrue();
        (await (await owner.Client.PostAsJsonAsync($"/api/panel/fe/received/{unverified.GetProperty("id").GetInt64()}/decision", new { approve = true })).ErrorCode())
            .ShouldBe("Fe.SignerUnverified");

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
        (await supplier.PostAsync($"/testecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml(Acecf(DelegateCertificate)))).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // R11
        (await supplier.PostAsync($"/testecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml(Acecf(SupplierCertificate)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        // R11 B-Med3: the same buyer's approval again is answered and not stored twice.
        (await supplier.PostAsync($"/testecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml(Acecf(SupplierCertificate)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.InDbAsync(db => db.FeApprovalsReceived.CountAsync(a => a.ENcf == "E310000000078" && a.BuyerRnc == SupplierRnc && a.TenantId == owner.TenantId))).ShouldBe(1);
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

        using var perRnc = app.WithWebHostBuilder(b => b.UseSetting("Fe:RequestsPerMinutePerIp", "100").UseSetting("Fe:RequestsPerMinutePerRnc", "2")
            .UseSetting("Fe:SeedRequestsPerMinutePerRnc", "2"));
        client = perRnc.CreateClient();
        var rnc = NewRnc();
        (await client.PostAsync($"/testecf/{rnc}/fe/recepcion/api/ecf", Xml("<ECF/>"))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.PostAsync($"/ecf/{rnc}/fe/aprobacioncomercial/api/ecf", Xml("<ECF/>"))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.PostAsync($"/TesteCF/{rnc}/fe/recepcion/api/ecf", Xml("<ECF/>"))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await client.PostAsync($"/testecf/{NewRnc()}/fe/recepcion/api/ecf", Xml("<ECF/>"))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        // R11 B-Med7: anonymous seeds have their own bucket per RNC: a flood of them never uses up that RNC's reception, nor the other way round.
        var flooded = NewRnc();
        foreach (var _ in Enumerable.Range(0, 2))
            (await client.GetAsync($"/testecf/{flooded}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync($"/ecf/{flooded}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await client.PostAsync($"/ecf/{flooded}/fe/autenticacion/api/validacioncertificado", Xml("<x/>"))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await client.PostAsync($"/testecf/{flooded}/fe/recepcion/api/ecf", Xml("<ECF/>"))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync($"/testecf/{rnc}/fe/autenticacion/api/semilla")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
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

    // --- helpers ---

    /// <summary>CloudCustody (TK-151): the certificate's copy in the (local, test) vault.</summary>
    private async Task<(StoreNode Node, Owner Owner, string Rnc, X509Certificate2 Certificate)> ReceiverAsync()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var rnc = NewRnc();
        var certificate = TestPki.Issue($"CN=Empresa, SERIALNUMBER={rnc}", out var pfx);
        var r = await node.Client.PostAsJsonAsync("/fiscal/v1/receiver/certificate",
            new { file = Convert.ToBase64String(pfx), password = "prueba", rnc, ownerName = "Ana Pérez", holderName = "Ana Pérez", accepted = true, consentVersion = FeConsent.Version });
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (node, owner, rnc, certificate);
    }

    /// <summary>StoreSigned (TK-246): the store proves the RNC with its certificate; the certificate stays with the test.</summary>
    private async Task<(StoreNode Node, Owner Owner, string Rnc, X509Certificate2 Certificate)> StoreSignedReceiverAsync()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var rnc = NewRnc();
        var certificate = TestPki.Issue($"CN=Empresa, SERIALNUMBER={rnc}");
        var r = await ActivateAsync(node, rnc, certificate);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return (node, owner, rnc, certificate);
    }

    private static async Task<HttpResponseMessage> ActivateAsync(StoreNode node, string rnc, X509Certificate2 certificate, string? challenge = null, string? proofRnc = null)
    {
        challenge ??= (await (await node.Client.PostAsync("/fiscal/v1/receiver/challenge", null)).Json()).GetProperty("challenge").GetString();
        var proof = new CertificateEcfSigner(certificate).Sign(new XDocument(new XElement("ActivacionReceptor", new XElement("RNC", proofRnc ?? rnc),
            new XElement("Reto", challenge)))).SignedXml;
        return await node.Client.PostAsJsonAsync("/fiscal/v1/receiver/activate",
            new { proof, rnc, ownerName = "Ana Pérez", holderName = "Ana Pérez", accepted = true, consentVersion = FeConsent.StoreSignedVersion });
    }

    /// <summary>Until the cloud has seen the store's poll (a store never seen is offline: nothing waits for it).</summary>
    private async Task OnlineAsync(StoreNode node)
    {
        var broker = app.Services.GetRequiredService<FeSignBroker>();
        for (var i = 0; i < 100 && !broker.IsOnline(node.NodeId); i++) await Task.Delay(50);
        broker.IsOnline(node.NodeId).ShouldBeTrue();
    }

    private static async Task<HttpClient> StoreWithNodeAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var email = $"owner{Guid.NewGuid():N}@example.com";
        (await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = CloudApp.Password })).EnsureSuccessStatusCode();
        await CloudApp.ConfirmEmailAsync(factory, email);
        var login = await (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = CloudApp.Password })).Json();
        var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        var store = (await (await owner.PostAsJsonAsync("/api/stores", new { name = "Tienda" })).Json()).GetProperty("id").GetGuid();
        var code = (await (await owner.PostAsync($"/api/stores/{store}/connection-codes", null)).Json()).GetProperty("code").GetString();
        return (await CloudApp.RegisterNodeAsync(factory, factory.Services.GetRequiredService<TimeProvider>(), code)).Client;
    }

    /// <summary>A supplier authenticated at the tenant's receiver: seed → signed with its certificate → token.</summary>
    /// <summary>The store's ACECF of a received e-NCF, signed with the receiver's certificate (R11 B-Med4: the approval event counts only with it).</summary>
    private static byte[] StoreAcecf(X509Certificate2 certificate, string encf) => FeXml.Zip(new CertificateEcfSigner(certificate)
        .Sign(new XDocument(new XElement("ACECF", new XElement("DetalleAprobacionComercial", new XElement("eNCF", encf))))).SignedXml);

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
