using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-238: DGII environment on screen, backup schedule, health screen and the first-run business data.</summary>
public sealed class StoreOperationsTests
{
    private static object Mode(params (string Env, string[] Blockers)[] targets) => new
    {
        environment = "Simulated", saved = false, autoSubmit = false, changedUtc = (DateTime?)null,
        targets = targets.Select(t => new { environment = t.Env, blockers = t.Blockers }),
    };

    [Fact]
    public void The_environment_shows_what_is_missing_and_changes_only_after_an_explicit_confirmation()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/mode", Mode(("Simulated", []), ("TesteCF", []), ("CerteCF", []), ("eCF", ["Importe el certificado digital."])))
           .Respond("PUT", "/api/fiscal/mode", _ => FakeApi.Response(HttpStatusCode.NoContent, new { }));
        var panel = ctx.Render<EcfModePanel>();

        panel.WaitForAssertion(() => panel.FindAll("button[role=radio]").Count.ShouldBe(4));
        panel.FindAll("button[role=radio]").Single(b => b.TextContent == "Producción").Click();
        panel.Markup.ShouldContain("Importe el certificado digital.");
        panel.FindAll("button").ShouldNotContain(b => b.TextContent == "Cambiar ambiente");

        panel.FindAll("button[role=radio]").Single(b => b.TextContent.Contains("TesteCF")).Click();
        panel.FindAll("button").Single(b => b.TextContent == "Cambiar ambiente").HasAttribute("disabled").ShouldBeTrue();
        panel.Find("input[type=checkbox]").Change(true);
        panel.FindAll("button").Single(b => b.TextContent == "Cambiar ambiente").Click();
        panel.WaitForAssertion(() => api.Requests.Single(r => r.Method == "PUT").Body.ShouldContain("\"TesteCF\""));
    }

    [Fact]
    public void The_backup_card_warns_without_a_passphrase_and_saves_the_schedule()
    {
        var (ctx, api) = ScreenContext.Create("backups.manage");
        using var _ = ctx;
        api.Json("GET", "/api/backups/settings", new
            {
                copyTo = (string?)null, time = "23:30", keep = 14, automatic = true, saved = false, folder = @"C:\ProgramData\Abastalia\backups",
                passphraseProblem = "Configure la frase de los respaldos antes de respaldar.", last = (object?)null, copyProblem = (string?)null,
            })
           .Respond("PUT", "/api/backups/settings", _ => FakeApi.Response(HttpStatusCode.NoContent, new { }));
        var card = ctx.Render<BackupSettingsCard>();

        card.WaitForAssertion(() => card.Markup.ShouldContain("Los respaldos no se están haciendo"));
        card.Markup.ShouldContain("Sin copia");
        card.Markup.ShouldContain("Apagada"); // TK-238: the cloud copy, off by default
        card.Find("input[maxlength='260']").Change(@"E:\Respaldos");
        card.FindAll("input[type=checkbox]").Single(c => c.ParentElement!.TextContent.Contains("a la nube")).Change(true);
        card.Find("form").Submit();
        card.WaitForAssertion(() => api.Requests.Single(r => r.Method == "PUT").Body.ShouldContain("\"time\":\"23:30\"", Case.Insensitive));
        api.Requests.Single(r => r.Method == "PUT").Body.ShouldContain(@"E:\\Respaldos");
        api.Requests.Single(r => r.Method == "PUT").Body.ShouldContain("\"cloud\":true", Case.Insensitive);
    }

    [Fact]
    public void The_health_screen_puts_what_needs_attention_with_what_to_do()
    {
        var (ctx, api) = ScreenContext.Create("settings.manage");
        using var _ = ctx;
        api.Json("GET", "/api/settings/health", new[]
        {
            new { name = "backup", status = "Degraded", title = "Respaldos", detail = (string?)"Configure la frase.", action = (string?)"Abra Configuración → Respaldos." },
            new { name = "clock", status = "Healthy", title = "Hora de la PC", detail = (string?)null, action = (string?)null },
        });
        var panel = ctx.Render<HealthPanel>();

        panel.WaitForAssertion(() => panel.Markup.ShouldContain("Qué hacer:"));
        panel.Markup.ShouldContain("Revisar");
        panel.Markup.ShouldContain("Bien");
    }

    [Fact]
    public void The_first_installation_refuses_an_RNC_of_the_wrong_length_before_sending_anything()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new FakeApi();
        ctx.Services.AddAbastaliaClient(new Uri("https://caja-principal.test"), api);
        ctx.Services.AddSingleton<ILocalStore>(new InMemoryStore());
        ctx.Services.AddAbastaliaHardware(new TestDrivers());
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        using (var server = new CertificateRequest("CN=Abastalia Nodo", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)))
            ctx.Services.GetRequiredService<NodeCertificatePin>().Validate(server, SslPolicyErrors.RemoteCertificateChainErrors);
        api.Json("GET", "/health", new { });

        var page = ctx.Render<SetupDevice>();
        page.FindAll("button[role=tab]").Single(b => b.TextContent == "Primera instalación").Click();
        page.Markup.ShouldContain("Su negocio");
        page.Find("input[autocomplete=name]").Change("Dueña");
        page.FindAll("input[maxlength='20']")[0].Change("admin");
        page.Find("input[type=password]").Change("1234");
        page.Find("input[maxlength='13']").Change("12345");
        for (var i = 0; i < page.FindAll("input[type=checkbox]").Count; i++) page.FindAll("input[type=checkbox]")[i].Change(true); // the fingerprint confirmation
        page.Find("form").Submit();

        page.WaitForAssertion(() => page.Markup.ShouldContain("El RNC tiene 9 números y la cédula 11."));
        api.Requests.ShouldNotContain(r => r.Route == "/api/identity/setup");
    }
}
