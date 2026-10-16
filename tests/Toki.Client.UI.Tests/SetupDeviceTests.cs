using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Client.UI.Pages;
using Toki.Client.UI.State;

namespace Toki.Client.UI.Tests;

/// <summary>R9 #9: "Unir a una tienda" sends the owner's pairing code only after the fingerprint was confirmed.</summary>
public sealed class SetupDeviceTests
{
    [Fact]
    public void The_pairing_code_is_not_sent_until_the_fingerprint_is_confirmed()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new FakeApi();
        ctx.Services.AddTokiClient(new Uri("https://caja-principal.test"), api);
        ctx.Services.AddSingleton<ILocalStore>(new InMemoryStore());
        ctx.Services.AddTokiHardware(new TestDrivers());
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        using (var server = new CertificateRequest("CN=Toki Nodo", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
            ctx.Services.GetRequiredService<NodeCertificatePin>().Validate(server, SslPolicyErrors.RemoteCertificateChainErrors); // the server this PC sees
        api.Json("GET", "/health", new { })
           .Respond("POST", "/api/identity/devices/pair", _ => FakeApi.Problem(HttpStatusCode.Unauthorized, "Identity.InvalidPairingCode", "No"));

        var page = ctx.Render<SetupDevice>();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Huella del servidor"));
        page.Find("input[inputmode=numeric]").Input("12345678");
        page.FindAll("button[type=submit]").Single().HasAttribute("disabled").ShouldBeTrue();
        page.Find("form").Submit(); // Enter
        api.Requests.ShouldNotContain(r => r.Route == "/api/identity/devices/pair");

        page.Find("input[type=checkbox]").Change(true);
        page.Find("form").Submit();
        page.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Route == "/api/identity/devices/pair"));
    }
}
