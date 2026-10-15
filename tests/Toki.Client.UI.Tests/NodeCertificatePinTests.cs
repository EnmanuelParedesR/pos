using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Client.UI.Api;
using Toki.Client.UI.State;

namespace Toki.Client.UI.Tests;

/// <summary>TK-122 (ADR-0009): the register pins the store server's self-signed certificate when it pairs and refuses any other after.</summary>
public sealed class NodeCertificatePinTests
{
    private static X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Windows' TLS needs a key it can load, not the ephemeral one CreateSelfSigned makes.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    [Fact]
    public void Unpaired_it_remembers_what_it_saw_and_pinned_it_accepts_only_that_certificate()
    {
        using var node = Certificate("Toki Nodo");
        using var impostor = Certificate("Toki Nodo");
        var pin = new NodeCertificatePin();

        pin.Validate(node, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue(); // self-signed: trust on first use
        pin.Seen.ShouldBe(node.GetCertHashString(HashAlgorithmName.SHA256));
        pin.Validate(null, SslPolicyErrors.RemoteCertificateNotAvailable).ShouldBeFalse();

        pin.Pin(pin.Seen);
        pin.Validate(impostor, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeFalse();
        pin.Validate(impostor, SslPolicyErrors.None).ShouldBeFalse(); // even a publicly trusted one: the pin decides
        pin.Validate(node, SslPolicyErrors.RemoteCertificateChainErrors).ShouldBeTrue();
        NodeCertificatePin.Fingerprint("ab12cd34ef567890ffff").ShouldBe("AB12 CD34 EF56 7890");
    }

    [Fact]
    public async Task Pairing_pins_the_server_over_real_TLS_and_another_server_is_refused_before_anything_is_sent()
    {
        using var node = Certificate("Toki Nodo");
        using var impostor = Certificate("Toki Nodo");
        await using var server = new TlsServer(node);
        var store = new InMemoryStore();
        var services = new ServiceCollection().AddSingleton<ILocalStore>(store).AddTokiClient(new Uri($"https://localhost:{server.Port}"));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var api = scope.ServiceProvider.GetRequiredService<TokiApi>();
            (await api.GetAsync<Empty>("/health")).Ok.ShouldBeTrue();
            await scope.ServiceProvider.GetRequiredService<Session>().PairAsync("device-secret");
        }
        (await store.ReadAsync("node-certificate")).ShouldBe(node.GetCertHashString(HashAlgorithmName.SHA256));
        var requests = server.Requests;

        server.Certificate = impostor; // someone else answers on the store LAN
        await using (var scope = provider.CreateAsyncScope())
        {
            var r = await scope.ServiceProvider.GetRequiredService<TokiApi>().GetAsync<Empty>("/api/identity/me");
            r.Problem!.Code.ShouldBe(Problem.UnknownServer);
            r.Problem.IsUncertain.ShouldBeFalse();
        }
        server.Requests.ShouldBe(requests); // the device secret never left the PC

        server.Certificate = node;
        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<TokiApi>().GetAsync<Empty>("/health")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task The_pin_is_loaded_with_the_device_and_forgotten_with_it()
    {
        var store = new InMemoryStore();
        await store.SaveAsync("node-certificate", "AB12");
        await store.SaveAsync("device", "secret");
        var pin = new NodeCertificatePin();
        var session = new Session(store, pin);

        await session.LoadAsync();
        pin.Pinned.ShouldBe("AB12");

        await session.ForgetDeviceAsync();
        pin.Pinned.ShouldBeNull();
        (await store.ReadAsync("node-certificate")).ShouldBeNull();
    }

    /// <summary>A bare HTTPS server: one request per connection, answers 200, counts what reached it.</summary>
    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        public TlsServer(X509Certificate2 certificate)
        {
            Certificate = certificate;
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Requests => Volatile.Read(ref _requests);
        public X509Certificate2 Certificate { get; set; }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                try
                {
                    await using var tls = new SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(Certificate);
                    var buffer = new byte[8192];
                    var head = new StringBuilder();
                    while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var n = await tls.ReadAsync(buffer, _stop.Token);
                        if (n == 0) break;
                        head.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    Interlocked.Increment(ref _requests);
                    await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                }
                catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException) { } // the client refused us
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _loop; }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
            _stop.Dispose();
        }
    }
}
