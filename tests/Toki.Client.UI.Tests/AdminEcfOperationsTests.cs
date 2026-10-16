using System.Net;
using Bunit;
using Shouldly;
using Toki.Client.UI.Components;
using Toki.Client.UI.Pages.Admin;

namespace Toki.Client.UI.Tests;

/// <summary>TK-155 to TK-157 on "Comprobantes electrónicos": health with the DGII check, the series B contingency and the certification assistant.</summary>
public sealed class AdminEcfOperationsTests
{
    private static object Contingency(bool active, bool canSign, int pending = 0) => new
    {
        active, startedUtc = active ? DateTime.UtcNow.AddDays(-2) : (DateTime?)null, reason = active ? "Certificado vencido" : null, day = active ? 3 : (int?)null,
        maxDays = 15, lastEndedUtc = (DateTime?)null, regularizeBy = (DateOnly?)null, canSign, cannotSignReason = canSign ? null : "el certificado digital venció",
        ranges = new[] { new { id = Guid.NewGuid(), type = "Consumer32", from = "B0200000001", to = "B0200000050", next = "B0200000004", available = 47L, expiresOn = (DateOnly?)null } },
        toRegularize = Enumerable.Range(1, pending).Select(i => new { id = Guid.NewGuid(), ncf = $"B020000000{i}", type = "Consumer32", issuedUtc = DateTime.UtcNow, total = 59m }).ToArray(),
        regularized = 0,
    };

    [Fact]
    public void The_health_card_lists_what_needs_the_owner_and_asks_the_DGII_on_demand()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/health", new
        {
            environment = "TesteCF", oldestPendingUtc = DateTime.UtcNow.AddHours(-30), lastDgiiContactUtc = DateTime.UtcNow.AddHours(-31), certificateExpiresUtc = DateTime.UtcNow.AddYears(1),
            canSign = true, contingencyActive = false,
            items = new[] { new { level = "warning", code = "queue", title = "Hay comprobantes sin enviar a la DGII desde hace 30 horas", detail = "La DGII da 72 horas." } },
        }).Json("POST", "/api/fiscal/dgii-status", new { state = "Down", message = "No se pudo conectar con la DGII.", checkedUtc = DateTime.UtcNow });
        var card = ctx.Render<EcfHealthPanel>();

        card.WaitForAssertion(() => card.Find(".esc-alert--warning .esc-alert__title").TextContent.ShouldContain("desde hace 30 horas"));
        card.Find("details summary").TextContent.ShouldBe("Qué hacer si no se puede facturar con e-CF");
        card.Find("button").Click();
        card.WaitForAssertion(() => card.Find("p[role=status]").TextContent.ShouldContain("La DGII no responde"));
    }

    [Fact]
    public void The_contingency_starts_with_a_reason_and_a_second_tap_and_B_receipts_are_regularized_after()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/contingency", Contingency(active: false, canSign: false))
            .Json("POST", "/api/fiscal/contingency/start", Contingency(active: true, canSign: false));
        var panel = ctx.Render<EcfContingencyPanel>();

        panel.WaitForAssertion(() => panel.Find("[aria-labelledby=contingency-title]").TextContent.ShouldContain("el certificado digital venció"));
        var start = panel.FindAll("button").Single(b => b.TextContent == "Activar contingencia");
        start.Click();
        panel.Find("p[role=status]").TextContent.ShouldContain("Escriba el motivo");
        panel.Find("input:not([inputmode]):not([type])").Change("Certificado vencido");
        panel.FindAll("button").Single(b => b.TextContent == "Activar contingencia").Click();
        api.Requests.ShouldNotContain(r => r.Method == "POST");
        api.Json("GET", "/api/fiscal/contingency", Contingency(active: true, canSign: false));
        panel.FindAll("button").Single(b => b.TextContent.StartsWith("¿Activar", StringComparison.Ordinal)).Click();
        panel.WaitForAssertion(() => panel.Find(".esc-pill--error").TextContent.ShouldBe("Activa · día 3 de 15"));
        api.Requests.Single(r => r.Route == "/api/fiscal/contingency/start").Body.ShouldContain("Certificado vencido");

        // Ended, with B receipts left: Regularizar asks a second tap.
        api.Json("GET", "/api/fiscal/contingency", Contingency(active: false, canSign: true, pending: 2))
            .Json("POST", "/api/fiscal/contingency/regularize", new { regularized = 2 });
        var after = ctx.Render<EcfContingencyPanel>();
        after.WaitForAssertion(() => after.Find("[aria-labelledby=contingency-title]").TextContent.ShouldContain("2 comprobantes serie B por regularizar"));
        after.FindAll("button").Single(b => b.TextContent == "Regularizar").Click();
        after.FindAll("button").Single(b => b.TextContent.StartsWith("¿Emitir", StringComparison.Ordinal)).Click();
        after.WaitForAssertion(() => after.Find("p[role=status]").TextContent.ShouldContain("Se emitieron 2 e-CF de reemplazo"));
    }

    [Fact]
    public void The_certification_assistant_ticks_steps_and_sends_the_test_set()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        var state = new
        {
            environment = "CerteCF", steps = new[] { new { number = 0, done = true, doneUtc = DateTime.UtcNow } },
            cases = new[] { new { id = Guid.NewGuid(), row = 2, eNcf = "E310000000001", type = "FiscalCredit31", channel = "Ecf", status = "Pending", trackId = (string?)null, messages = (string?)null } },
            simulation = new { accepted = 3, rejected = 0, waiting = 1 },
        };
        api.Json("GET", "/api/fiscal/certification", state)
            .Respond("PUT", "/api/fiscal/certification/steps/3", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .Json("POST", "/api/fiscal/certification/send", new[] { new { id = Guid.NewGuid(), row = 2, eNcf = "E310000000001", type = "FiscalCredit31", channel = "Ecf", status = "Accepted", trackId = "t-1", messages = (string?)null } });
        var panel = ctx.Render<EcfCertificationPanel>();

        panel.WaitForAssertion(() => panel.Find(".esc-card__aside").TextContent.ShouldBe("1 de 14 pasos"));
        panel.Find("[aria-labelledby=cert-title]").TextContent.ShouldContain("Aceptados: 3");
        panel.FindAll("input[type=checkbox]")[3].Change(true);
        panel.WaitForAssertion(() => api.Requests.Single(r => r.Method == "PUT").Body.ShouldContain("\"done\":true"));
        panel.FindAll("button").Single(b => b.TextContent == "Enviar y consultar").Click();
        panel.WaitForAssertion(() => panel.Find("p[role=status]").TextContent.ShouldContain("Listo: 1 aceptados, 0 rechazados"));
    }

    [Fact]
    public void Accompaniment_is_asked_with_the_contact_and_then_shows_as_sent_with_its_status()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        var request = new { id = Guid.NewGuid(), status = "New", contactName = "Ana Pérez", phone = "809-555-1234", preferredTimes = "Mañanas",
            session1Utc = (DateTime?)null, session2Utc = (DateTime?)null, createdUtc = DateTime.UtcNow };
        api.Json("GET", "/api/fiscal/accompaniment", new { connected = true, request = (object?)null, suggestedName = "Yudelka M.", suggestedPhone = "809-555-1234" })
            .Json("POST", "/api/fiscal/accompaniment", request);
        var panel = ctx.Render<EcfAccompanimentPanel>();

        panel.WaitForAssertion(() => panel.FindAll("input")[1].GetAttribute("value").ShouldBe("809-555-1234"));
        panel.FindAll("input")[0].Change("");
        panel.Find("form").Submit();
        panel.Find(".esc-field__error").TextContent.ShouldContain("Escriba con quién hablamos");
        api.Requests.ShouldNotContain(r => r.Method == "POST");

        panel.FindAll("input")[0].Change("Ana Pérez");
        panel.FindAll("input")[2].Change("Mañanas");
        panel.Find("form").Submit();
        panel.WaitForAssertion(() => panel.Find(".esc-pill").TextContent.ShouldBe("Nueva"));
        panel.Find("dl").TextContent.ShouldContain("Ana Pérez · 809-555-1234");
        panel.FindAll("p[role=status]").ShouldContain(p => p.TextContent.Contains("Solicitud enviada"));
        api.Requests.Single(r => r.Method == "POST").Body.ShouldContain("\"preferredTimes\":\"Ma"); // ñ goes escaped
    }

    [Fact]
    public void Accompaniment_without_the_cloud_says_to_connect_and_offers_no_form()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/accompaniment", new { connected = false, request = (object?)null, suggestedName = (string?)null, suggestedPhone = (string?)null });
        var panel = ctx.Render<EcfAccompanimentPanel>();

        panel.WaitForAssertion(() => panel.Find(".esc-notice").TextContent.ShouldContain("Conecte la tienda a la nube"));
        panel.Find(".esc-notice a").GetAttribute("href").ShouldBe("admin/settings?section=cloud");
        panel.FindAll("form").ShouldBeEmpty();
    }

    [Fact]
    public void The_summary_lists_the_contingency_from_the_health_check()
    {
        var (ctx, api) = ScreenContext.Create("reports.view", "fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/health", new
        {
            environment = "eCF", oldestPendingUtc = (DateTime?)null, lastDgiiContactUtc = (DateTime?)null, certificateExpiresUtc = (DateTime?)null, canSign = false,
            contingencyActive = true,
            items = new[]
            {
                new { level = "error", code = "contingency", title = "Contingencia activa: día 3 de 15", detail = "Se facturan comprobantes serie B en papel." },
                new { level = "warning", code = "sequences", title = "Quedan 3 números", detail = "x" },
            },
        });
        var page = ctx.Render<Summary>();

        page.WaitForAssertion(() => page.Find(".esc-alert--error .esc-alert__title").TextContent.ShouldBe("Contingencia activa: día 3 de 15"));
        page.FindAll(".esc-alert").Count.ShouldBe(1);
        page.Find(".esc-alert--error a").GetAttribute("href").ShouldBe("admin/ecf");
    }
}
