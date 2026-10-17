using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Tests;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>TK-144: the DGII client alone against <see cref="FakeDgii"/>: authentication, token cache, URLs per environment and answer mapping.</summary>
public sealed class DgiiClientTests : IDisposable
{
    private static readonly X509Certificate2 Certificate = new CertificateRequest("CN=Firmante de Prueba, SERIALNUMBER=00112345678", RSA.Create(2048),
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.Zero));
    private readonly FakeDgii _dgii = new();
    private readonly DgiiClient _client;

    public DgiiClientTests()
    {
        _dgii.Now = _clock.GetUtcNow;
        _client = Client(FiscalEnvironment.TesteCF);
    }

    private DgiiClient Client(FiscalEnvironment env) => new(new Factory(_dgii), new CertificateEcfSigner(Certificate), new DgiiUrls(env), _clock);

    public void Dispose()
    {
        _client.Dispose();
        _dgii.Dispose();
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static string Signed(string xml) => new CertificateEcfSigner(Certificate).Sign(XDocument.Parse(xml)).SignedXml;

    [Fact]
    public async Task The_seed_is_signed_and_the_token_is_reused_until_5_minutes_before_it_expires()
    {
        await _client.SendEcfAsync("131245678", "E310000000001", Signed("<ECF><Encabezado><IdDoc><eNCF>E310000000001</eNCF></IdDoc></Encabezado></ECF>"), default);
        await _client.GetResultAsync("track-1", default);

        var seed = _dgii.To("/autenticacion/api/autenticacion/validarsemilla").Single();
        (seed.Host, seed.Path, seed.Field).ShouldBe(("ecf.dgii.gov.do", "/testecf/autenticacion/api/autenticacion/validarsemilla", "xml"));
        var signed = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
        signed.LoadXml(seed.Xml!);
        var signature = new SignedXml(signed);
        signature.LoadXml((XmlElement)signed.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        signature.CheckSignature(Certificate, verifySignatureOnly: true).ShouldBeTrue();
        EcfXml.Validate(XDocument.Parse(seed.Xml!), EcfXml.SeedSchema).ShouldBeEmpty();
        _dgii.Tokens.ShouldBe(["token-1"]);
        _dgii.To("/consultaresultado/api/consultas/estado").Single().Authorization.ShouldBe("Bearer token-1");

        _clock.Advance(TimeSpan.FromMinutes(54));
        await _client.GetResultAsync("track-1", default);
        _dgii.Tokens.Count.ShouldBe(1);
        _clock.Advance(TimeSpan.FromMinutes(1)); // 55 minutes: 5 before "expira"
        await _client.GetResultAsync("track-1", default);
        _dgii.Tokens.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_401_gets_a_new_token_once_and_a_DGII_outage_throws_for_the_queue_to_retry()
    {
        await _client.GetResultAsync("x", default);
        _dgii.Tokens.Clear(); // the DGII forgot the token (restart, revocation)
        await _client.GetResultAsync("x", default);
        _dgii.Tokens.ShouldBe(["token-1"]); // numbered by count: a fresh one was issued

        _dgii.Down = true;
        await Should.ThrowAsync<HttpRequestException>(() => _client.GetResultAsync("x", default));
    }

    [Fact]
    public async Task Answers_map_to_the_DGII_codes_and_each_service_has_its_host_and_file_name()
    {
        var xml = Signed("<ECF><Encabezado><IdDoc><eNCF>E310000000001</eNCF></IdDoc></Encabezado></ECF>");
        var sent = await _client.SendEcfAsync("131245678", "E310000000001", xml, default);
        sent.ShouldBe(new DgiiAnswer(DgiiState.InProcess, "track-1"));
        _dgii.To("/recepcion/api/facturaselectronicas").Single().FileName.ShouldBe("131245678E310000000001.xml");

        (await _client.GetResultAsync("track-1", default)).State.ShouldBe(DgiiState.InProcess);
        _dgii.Result = _ => (2, false, "XML no cumple el esquema");
        (await _client.GetResultAsync("track-1", default)).ShouldBe(new DgiiAnswer(DgiiState.Rejected, "track-1", false, "145: XML no cumple el esquema"));
        (await _client.GetResultAsync("nope", default)).State.ShouldBe(DgiiState.NotFound);
        (await _client.FindAsync("131245678", "E310000000001", "abc", EcfChannel.Ecf, default))!.TrackId.ShouldBe("track-1");
        (await _client.FindAsync("131245678", "E310000000009", "abc", EcfChannel.Ecf, default)).ShouldBeNull();

        _dgii.Result = _ => (4, true, "Descuadre");
        var rfce = await _client.SendRfceAsync("131245678", "E320000000001", Signed("<RFCE><Encabezado><IdDoc><eNCF>E320000000001</eNCF></IdDoc></Encabezado></RFCE>"), default);
        (rfce.State, rfce.SequenceUsed).ShouldBe((DgiiState.AcceptedConditional, true));
        _dgii.To("/recepcionfc/api/recepcion/ecf").Single().Host.ShouldBe("fc.dgii.gov.do");
        (await _client.FindAsync("131245678", "E320000000001", "a+b/c=", EcfChannel.Rfce, default))!.State.ShouldBe(DgiiState.AcceptedConditional);
        _dgii.To("/consultarfce/api/Consultas/Consulta").Single().Query.ShouldContain("Cod_Seguridad_eCF=a%2Bb%2Fc%3D");

        // CerteCF publishes neither TrackIds by e-NCF nor voids.
        using var certification = Client(FiscalEnvironment.CerteCF);
        (await certification.FindAsync("131245678", "E310000000001", "abc", EcfChannel.Ecf, default)).ShouldBeNull();
        (await certification.VoidAsync("131245678", "<ANECF/>", default)).State.ShouldBe(DgiiState.Rejected);
        new DgiiUrls(FiscalEnvironment.eCF).Ecf.ShouldBe("https://ecf.dgii.gov.do/ecf");
    }

    [Fact]
    public async Task An_answer_that_is_not_the_DGII_s_is_retried_never_a_rejection()
    {
        var xml = Signed("<ECF><Encabezado><IdDoc><eNCF>E310000000001</eNCF></IdDoc></Encabezado></ECF>");
        HttpResponseMessage? next = null;
        _dgii.Override = _ => next;

        // R12 #4: the firewall's page, a proxy's 404 or 413, an empty 408: HttpRequestException, so the queue tries again.
        foreach (var answer in new Func<HttpResponseMessage>[]
        {
            () => new(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent(FakeDgiiPublic.Page("waf-denied"), System.Text.Encoding.UTF8, "text/html") },
            () => new(System.Net.HttpStatusCode.NotFound),
            () => new(System.Net.HttpStatusCode.RequestTimeout),
            () => new(System.Net.HttpStatusCode.RequestEntityTooLarge) { Content = new StringContent("<html>Too large</html>") },
        })
        {
            next = answer();
            await Should.ThrowAsync<HttpRequestException>(() => _client.SendEcfAsync("131245678", "E310000000001", xml, default));
            next = answer();
            await Should.ThrowAsync<HttpRequestException>(() => _client.SendRfceAsync("131245678", "E320000000001", xml, default));
            next = answer();
            await Should.ThrowAsync<HttpRequestException>(() => _client.VoidAsync("131245678", "<ANECF/>", default));
        }

        // The DGII's own refusal stays a rejection.
        next = new(System.Net.HttpStatusCode.BadRequest) { Content = System.Net.Http.Json.JsonContent.Create(new { trackId = (string?)null, error = "XML inválido", mensaje = "Firma" }) };
        (await _client.SendEcfAsync("131245678", "E310000000001", xml, default)).ShouldBe(new DgiiAnswer(DgiiState.Rejected, Messages: "XML inválido: Firma"));
        // R13: for an RFCE too (it has no codigo nor estado: read as "not found" it would stay Sent, polled for good).
        next = new(System.Net.HttpStatusCode.BadRequest) { Content = System.Net.Http.Json.JsonContent.Create(new { error = "XML inválido", mensaje = "Firma" }) };
        (await _client.SendRfceAsync("131245678", "E320000000001", xml, default)).ShouldBe(new DgiiAnswer(DgiiState.Rejected, Messages: "XML inválido: Firma"));
    }
}
