using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Pages;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.UI.Tests;

/// <summary>R9 #9: "Unir a una tienda" sends the owner's pairing code only after the fingerprint was confirmed.</summary>
public sealed class SetupDeviceTests
{
    [Fact]
    public void The_pairing_code_is_not_sent_until_the_fingerprint_is_confirmed()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new FakeApi();
        ctx.Services.AddAbastaliaClient(new Uri("https://caja-principal.test"), api);
        ctx.Services.AddSingleton<ILocalStore>(new InMemoryStore());
        ctx.Services.AddAbastaliaHardware(new TestDrivers());
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        using (var server = new CertificateRequest("CN=Abastalia Nodo", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
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

    /// <summary>TK-251: the installed app points at this same PC; a register that joins a store is pointed at the main PC, and keeps it.</summary>
    [Fact]
    public async Task Joining_points_the_register_at_the_main_PC_and_it_stays_there_on_the_next_run()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new FakeApi();
        ctx.Services.AddAbastaliaClient(new Uri("https://localhost:5443"), api);
        var store = new InMemoryStore();
        ctx.Services.AddSingleton<ILocalStore>(store);
        ctx.Services.AddAbastaliaHardware(new TestDrivers());
        api.Respond("GET", "/health", _ => throw new HttpRequestException("no server on this PC"));

        var page = ctx.Render<SetupDevice>(); // nothing answers here, so it stays on "Unir a una tienda"
        page.WaitForAssertion(() => page.Find("#server"));
        Button(page).Click(); // empty
        page.WaitForAssertion(() => page.Markup.ShouldContain("Escriba el nombre o la IP"));

        api.Json("GET", "/health", new { });
        page.Find("#server").Change("192.168.1.10");
        Button(page).Click();
        var address = new Uri("https://192.168.1.10:5443/");
        page.WaitForAssertion(() => ctx.Services.GetRequiredService<ServerAddress>().Current.ShouldBe(address));
        page.Markup.ShouldNotContain("No se encontró");
        (await store.ReadAsync("server")).ShouldBe(address.ToString());

        // The next run (and after an update, which replaces appsettings) starts at the main PC.
        var next = new ServerAddress(new Uri("https://localhost:5443"));
        await new Session(store, new NodeCertificatePin(), next).LoadAsync();
        next.Current.ShouldBe(address);

        static AngleSharp.Dom.IElement Button(IRenderedComponent<SetupDevice> page) => page.FindAll("button").Single(b => b.TextContent.Contains("Buscar"));
    }
}
