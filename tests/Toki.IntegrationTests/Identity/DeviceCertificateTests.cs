using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Client.UI;
using Toki.Client.UI.State;
using Toki.Identity;
using Toki.IntegrationTests.Infra;
using C = Toki.Client.UI.Api;

namespace Toki.IntegrationTests.Identity;

/// <summary>The real API on Kestrel over https, as in a store: the server's certificate comes from the store CA.</summary>
public sealed class TlsTokiApp : TokiApp
{
    public TlsTokiApp() => UseKestrel();

    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Toki:HttpsWithNodeCertificate"] = "true",
        ["Kestrel:Endpoints:Https:Url"] = "https://127.0.0.1:0",
    };

    public Uri Address
    {
        get
        {
            StartServer();
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            return new Uri(addresses.Single(a => a.StartsWith("https://", StringComparison.Ordinal)).Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
        }
    }
}

/// <summary>TK-091 (ADR-0009) over real TLS: pairing issues a client certificate from the store CA, the register pins the CA, and the server
/// knows each register by its certificate (no X-Device secret).</summary>
public sealed class DeviceCertificateTests
{
    /// <summary>A register: the app's own services (TLS checks, pairing, storage), on a store it keeps between runs.</summary>
    private sealed class Register : IAsyncDisposable
    {
        public Store Data { get; } = new();
        private ServiceProvider _provider;

        public Register(Uri server) => _provider = Start(server, Data);

        private static ServiceProvider Start(Uri server, Store data) =>
            new ServiceCollection().AddSingleton<ILocalStore>(data).AddTokiClient(server).BuildServiceProvider();

        public C.TokiApi Api => _provider.GetRequiredService<C.TokiApi>();
        public Session Session => _provider.GetRequiredService<Session>();
        public NodeCertificatePin Pin => _provider.GetRequiredService<NodeCertificatePin>();

        /// <summary>Closes the app and opens it again: new connections, everything read back from its store.</summary>
        public async Task RestartAsync(Uri server)
        {
            await _provider.DisposeAsync();
            _provider = Start(server, Data);
            await Session.LoadAsync();
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();

        public sealed class Store : ILocalStore
        {
            private readonly Dictionary<string, string> _data = [];
            public Task<string?> ReadAsync(string key) => Task.FromResult(_data.GetValueOrDefault(key));
            public Task SaveAsync(string key, string? value)
            {
                if (value is null) _data.Remove(key);
                else _data[key] = value;
                return Task.CompletedTask;
            }
        }
    }

    private static async Task<Register> InstallAsync(TlsTokiApp app)
    {
        var register = new Register(app.Address);
        (await register.Api.GetAsync<C.Empty>("/health")).Ok.ShouldBeTrue();
        var shown = register.Pin.Seen;
        var setup = await register.Api.PostAsync<C.SetupResult>("/api/identity/setup",
            new C.SetupRequest("Dueña", "admin", TokiApp.AdminPin, "PC principal", register.Session.CertificateRequest()));
        setup.Problem.ShouldBeNull();
        (await register.Session.PairAsync(shown, setup.Value!.DeviceToken, setup.Value.Certificate, setup.Value.StoreCa)).ShouldBeTrue();
        return register;
    }

    private static async Task SignInAsync(Register register)
    {
        var opened = await register.Api.PostAsync<C.OpenedSession>("/api/identity/sessions", new { Code = "admin", Pin = TokiApp.AdminPin });
        opened.Problem.ShouldBeNull();
        register.Session.Open(opened.Value!, new C.Me(opened.Value!.UserId, "Dueña", Guid.Empty, []));
        var me = await register.Api.GetAsync<C.Me>("/api/identity/me");
        me.Problem.ShouldBeNull(); // the session works on this register's certificate
        register.Session.Open(opened.Value!, me.Value!);
    }

    [Fact]
    public async Task Pairing_issues_a_certificate_the_server_knows_the_register_by_and_revoking_it_denies_it()
    {
        await using var app = new TlsTokiApp();
        await using var main = await InstallAsync(app);
        main.Session.DeviceToken.ShouldBeNull(); // no secret over https
        main.Pin.ClientCertificate.ShouldNotBeNull().Subject.ShouldContain("Toki Caja");
        main.Pin.Ca.ShouldNotBeNull();
        main.Pin.Pinned.ShouldBeNull(); // the CA, not the server's own certificate
        await SignInAsync(main);
        (await main.Api.GetAsync<List<C.ActiveUser>>("/api/identity/users/active")).Value.ShouldNotBeNull().ShouldNotBeEmpty();

        // A second register pairs with a code, the way "Unir a una tienda" does.
        var code = await main.Api.PostAsync<C.PairingCode>("/api/identity/devices/pairing-codes", new { DeviceName = "Caja 2" });
        await using var second = new Register(app.Address);
        (await second.Api.GetAsync<C.Empty>("/health")).Ok.ShouldBeTrue();
        var shown = second.Pin.Seen;
        var paired = await second.Api.PostAsync<C.Paired>("/api/identity/devices/pair", new { code.Value!.Code, CertificateRequest = second.Session.CertificateRequest() });
        paired.Value.ShouldNotBeNull().Token.ShouldBeNull();
        (await second.Session.PairAsync(shown, paired.Value.Token, paired.Value.Certificate, paired.Value.StoreCa)).ShouldBeTrue();
        (await second.Api.GetAsync<List<C.ActiveUser>>("/api/identity/users/active")).Ok.ShouldBeTrue();

        (await main.Api.PostAsync<C.Empty>($"/api/identity/devices/{paired.Value.DeviceId}/revoke")).Ok.ShouldBeTrue();
        var refused = await second.Api.GetAsync<List<C.ActiveUser>>("/api/identity/users/active");
        refused.Problem.ShouldNotBeNull().Code.ShouldBe("Identity.DeviceNotPaired");
    }

    [Fact]
    public async Task Without_a_certificate_only_health_and_pairing_answer_and_an_unknown_certificate_is_refused_at_the_handshake()
    {
        await using var app = new TlsTokiApp();
        var address = app.Address;
        using var ca = X509Certificate2.CreateFromPem(app.Services.GetRequiredService<StoreCertificateAuthority>().CertificatePem);
        RemoteCertificateValidationCallback ours = (_, server, _, _) => server is not null && NodeCertificatePin.IssuedBy(server, ca);
        using var anonymous = new HttpClient(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = ours } }) { BaseAddress = address };
        (await anonymous.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var denied = await anonymous.GetAsync("/api/identity/users/active");
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await denied.ErrorCode()).ShouldBe("Identity.DeviceNotPaired");

        // A client certificate the store CA did not issue: the server ends the handshake (TLS 1.3 may let the client notice only on reading).
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Toki Caja", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], critical: false));
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var stranger = X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
        using var impostor = new HttpClient(new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = ours,
                LocalCertificateSelectionCallback = (_, _, _, _, _) => stranger,
            },
        }) { BaseAddress = address };
        await Should.ThrowAsync<HttpRequestException>(() => impostor.GetAsync("/api/identity/users/active"));
    }

    [Fact]
    public async Task R11_C5_a_register_renews_its_one_year_certificate_over_mTLS_and_the_old_one_lasts_until_it_expires()
    {
        await using var app = new TlsTokiApp();
        await using var main = await InstallAsync(app);
        var old = main.Pin.ClientCertificate!;
        (old.NotAfter - old.NotBefore).TotalDays.ShouldBeLessThan(400);
        async Task<string?> Renew(string csr) =>
            (await main.Api.PostAsync<C.RenewedCertificate>("/api/identity/devices/current/certificate", new { CertificateRequest = csr })).Value?.Certificate;

        await main.Session.RenewCertificateIfDueAsync(Renew, DateTime.UtcNow);
        main.Pin.ClientCertificate.ShouldBeSameAs(old); // not due yet

        await main.Session.RenewCertificateIfDueAsync(Renew, old.NotAfter.ToUniversalTime().AddDays(-10));
        var renewed = main.Pin.ClientCertificate!;
        renewed.Thumbprint.ShouldNotBe(old.Thumbprint);
        Convert.ToHexString(renewed.GetPublicKey()).ShouldNotBe(Convert.ToHexString(old.GetPublicKey())); // a new key too
        await main.RestartAsync(app.Address); // kept: read back from its store
        main.Pin.ClientCertificate!.Thumbprint.ShouldBe(renewed.Thumbprint);
        await SignInAsync(main);

        // The answer may not have reached the register: the certificate it renewed still works until it expires.
        using var ca = X509Certificate2.CreateFromPem(app.Services.GetRequiredService<StoreCertificateAuthority>().CertificatePem);
        using var withOld = new HttpClient(new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, server, _, _) => server is not null && NodeCertificatePin.IssuedBy(server, ca),
                LocalCertificateSelectionCallback = (_, _, _, _, _) => old,
            },
        }) { BaseAddress = app.Address };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var again = await withOld.PostAsJsonAsync("/api/identity/devices/current/certificate",
            new { CertificateRequest = new CertificateRequest("CN=Toki Caja", key, HashAlgorithmName.SHA256).CreateSigningRequestPem() });
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_renewed_server_certificate_keeps_paired_registers_connected()
    {
        await using var app = new TlsTokiApp();
        await using var main = await InstallAsync(app);
        await SignInAsync(main);
        var before = main.Pin.Seen;

        app.Services.GetRequiredService<StoreCertificateAuthority>().IssueServerCertificate();
        await main.RestartAsync(app.Address); // new connections: a new handshake with the new certificate

        (await main.Api.GetAsync<List<C.ActiveUser>>("/api/identity/users/active")).Ok.ShouldBeTrue();
        main.Pin.Seen.ShouldNotBeNull().ShouldNotBe(before);
        main.Session.UnknownServer.ShouldBeFalse();
    }
}
