using System.Net;
using Bunit;
using Shouldly;
using Toki.Client.UI.Pages.Admin;

namespace Toki.Client.UI.Tests;

/// <summary>TK-204/205: "Comprobantes electrónicos" (rejected → replace, never sent → void, unused numbers → ANECF), and TK-176's warnings.</summary>
public sealed class AdminEcfTests
{
    private static readonly Guid Rejected = Guid.NewGuid(), Pending = Guid.NewGuid(), Range = Guid.NewGuid();

    private static object Doc(Guid id, string encf, string status, string? messages = null) => new
    {
        id, eNcf = encf, type = "FiscalCredit31", referenceId = Guid.NewGuid(), total = 1_180m, itbis = 180m, signedUtc = DateTime.UtcNow.AddHours(-2), status,
        submitDeadlineUtc = DateTime.UtcNow.AddHours(70), lastError = (string?)null, environment = "TesteCF", channel = "Ecf", trackId = (string?)null,
        dgiiCode = (int?)null, dgiiMessages = messages, sequenceUsed = (bool?)true, contingency = false, replacesENcf = (string?)null, replacedByENcf = (string?)null,
    };

    private static FakeApi Ecf(FakeApi api) => api
        .Json("GET", "/api/fiscal/queue", new { environment = "TesteCF", pending = 1, sent = 0, overdue = 0, nextDeadlineUtc = DateTime.UtcNow.AddHours(70), rejected = 1, simulated = 0, certificateExpiresUtc = (DateTime?)null })
        .Respond("GET", "/api/fiscal/documents", _ => FakeApi.Response(HttpStatusCode.OK, new[] { Doc(Pending, "E310000000002", "Pending"), Doc(Rejected, "E310000000001", "Rejected", "145: El RNC del comprador no corresponde") }))
        .Json("GET", "/api/fiscal/documents?rejectedOnly=true", new[] { Doc(Rejected, "E310000000001", "Rejected", "145: El RNC del comprador no corresponde") })
        .Json("GET", "/api/fiscal/sequences", new[]
        {
            new { id = Range, type = "CreditNote34", environment = "TesteCF", current = true, from = "E340000000001", to = "E340000000100", next = "E340000000011", available = 90L, percentUsed = 10m, expiresOn = (DateOnly?)null },
        })
        .Json("GET", "/api/fiscal/voids", Array.Empty<object>());

    [Fact]
    public void A_rejected_e_CF_is_replaced_with_a_second_tap_and_the_environment_is_shown()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        Ecf(api).Json("POST", $"/api/fiscal/documents/{Rejected}/replace", new { documentId = Guid.NewGuid(), eNcf = "E310000000003" }, HttpStatusCode.Created);
        var page = ctx.Render<Ecf>();

        page.WaitForAssertion(() => page.Find("[aria-labelledby=rejected-title] tbody").TextContent.ShouldContain("El RNC del comprador no corresponde"));
        page.Find(".esc-page-head__actions").TextContent.ShouldContain("Pre-certificación (TesteCF)");
        var replace = page.Find("[aria-labelledby=rejected-title] tbody button");
        replace.Click();
        replace = page.Find("[aria-labelledby=rejected-title] tbody button");
        replace.TextContent.ShouldBe("¿Reemplazar? Toque otra vez");
        api.Requests.ShouldNotContain(r => r.Method == "POST");
        replace.Click();

        page.WaitForAssertion(() => page.Find(".esc-notice").TextContent.ShouldContain("Se emitió E310000000003 en reemplazo de E310000000001"));
        api.Requests.Count(r => r.Method == "POST" && r.Route == $"/api/fiscal/documents/{Rejected}/replace").ShouldBe(1);
    }

    [Fact]
    public void A_pending_e_CF_is_voided_and_unused_numbers_show_what_will_be_voided_before_asking()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.manage");
        using var _ = ctx;
        Ecf(api).Json("POST", $"/api/fiscal/documents/{Pending}/void", new { id = Guid.NewGuid(), from = "E310000000002", to = "E310000000002", count = 1L }, HttpStatusCode.Created)
            .Json("POST", $"/api/fiscal/sequences/{Range}/void", new { id = Guid.NewGuid(), from = "E340000000011", to = "E340000000050", count = 40L }, HttpStatusCode.Created);
        var page = ctx.Render<Ecf>();

        page.WaitForAssertion(() => page.Find("[aria-labelledby=recent-title] .esc-button--danger").TextContent.ShouldBe("Anular"));
        page.Find("[aria-labelledby=recent-title] .esc-button--danger").Click();
        page.Find("[aria-labelledby=recent-title] .esc-button--danger").Click();
        page.WaitForAssertion(() => page.Find(".esc-notice").TextContent.ShouldContain("E310000000002 quedó anulado"));

        page.Find("p[role=status]").TextContent.ShouldBe("Se anularán 90 números: E340000000011 a E340000000100.");
        page.Find("input[inputmode=numeric]").Change("50");
        page.Find("p[role=status]").TextContent.ShouldBe("Se anularán 40 números: E340000000011 a E340000000050.");
        page.Find("form").Submit();
        api.Requests.ShouldNotContain(r => r.Route.Contains("/sequences/", StringComparison.Ordinal));
        page.Find("form").Submit();
        page.WaitForAssertion(() => page.Find(".esc-notice").TextContent.ShouldContain("Se anularon 40 números"));
        api.Requests.Single(r => r.Route == $"/api/fiscal/sequences/{Range}/void").Body.ShouldContain("\"to\":50");
    }

    [Fact]
    public void The_summary_warns_about_rejected_e_CF_and_a_certificate_that_expires_within_30_days()
    {
        var (ctx, api) = ScreenContext.Create("reports.view", "fiscal.manage");
        using var _ = ctx;
        api.Json("GET", "/api/fiscal/queue", new { pending = 0, overdue = 0, nextDeadlineUtc = (DateTime?)null, rejected = 2, simulated = 0, certificateExpiresUtc = DateTime.UtcNow.AddDays(12.5) });
        var page = ctx.Render<Summary>();

        page.WaitForAssertion(() => page.FindAll(".esc-alert").Count.ShouldBe(2));
        page.Find(".esc-alert--error .esc-alert__title").TextContent.ShouldBe("La DGII rechazó 2 comprobantes");
        page.Find(".esc-alert--error a").GetAttribute("href").ShouldBe("admin/ecf");
        page.Find(".esc-alert--warning .esc-alert__title").TextContent.ShouldBe("El certificado digital vence en 12 días");
        page.Find(".esc-alert--warning a").GetAttribute("href").ShouldBe("admin/settings?section=fiscal");
    }
}
