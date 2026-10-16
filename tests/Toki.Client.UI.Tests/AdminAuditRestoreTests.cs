using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Client.UI.Pages.Admin;
using Toki.Client.UI.State;

namespace Toki.Client.UI.Tests;

/// <summary>TK-213: the audit log screen and restoring a backup from Configuración.</summary>
public sealed class AdminAuditRestoreTests
{
    private static readonly Guid Ana = Guid.NewGuid();

    [Fact]
    public void The_audit_log_filters_shows_spanish_actions_and_expands_the_details()
    {
        var (ctx, api) = ScreenContext.Create("audit.view");
        using var _ = ctx;
        api.Json("GET", "/api/audit/users", new[] { new { id = Ana, name = "Ana" } })
           .Json("GET", "/api/audit", new object[]
           {
               new { sequence = 7L, dateUtc = DateTime.UtcNow, userId = Ana, user = "Ana", supervisor = "Pedro", action = "sales.void", resourceType = "Sale",
                   resourceId = Guid.NewGuid(), data = "{\"reason\":\"Cliente se arrepinti\\u00F3\"}" },
               new { sequence = 6L, dateUtc = DateTime.UtcNow, userId = (Guid?)null, user = (string?)null, supervisor = (string?)null, action = "something.new",
                   resourceType = "Thing", resourceId = (Guid?)null, data = "{}" },
           });
        var page = ctx.Render<Audit>();

        page.WaitForAssertion(() => page.FindAll("tbody tr").Count.ShouldBe(2));
        var rows = page.FindAll("tbody tr");
        rows[0].TextContent.ShouldContain("Venta anulada");
        rows[0].TextContent.ShouldContain("Ana · autorizó Pedro");
        rows[1].TextContent.ShouldContain("something.new"); // an unknown code shows as it is
        rows[1].TextContent.ShouldContain("Toki (automático)");

        rows[0].Click();
        page.WaitForAssertion(() => page.Find(".audit-data").TextContent.ShouldContain("Cliente se arrepintió"));

        page.Find("select[aria-label='Usuario']").Change(Ana.ToString());
        page.Find("select[aria-label='Acción']").Change("sales.void");
        page.WaitForAssertion(() => api.Requests.Last(r => r.Route.StartsWith("/api/audit?")).Route.ShouldContain($"&userId={Ana}&action=sales.void&page=1"));
    }

    [Fact]
    public void Restoring_asks_for_the_passphrase_closed_registers_and_a_second_confirmation()
    {
        var (ctx, api) = ScreenContext.Create("backups.manage");
        using var _ = ctx;
        const string file = "toki-20261014-233000.toki-backup";
        api.Json("GET", "/api/backups", new[] { new { fileName = file, bytes = 1_048_576L, dateUtc = DateTime.UtcNow } })
           .Respond("POST", $"/api/backups/{file}/restore", _ => FakeApi.Problem(HttpStatusCode.BadRequest, "Backup.WrongPassphrase", "La frase no abre…"));
        var page = ctx.Render<Settings>();

        page.WaitForAssertion(() => page.Find($"button[aria-label='Restaurar {file}']").Click());
        page.Find("[role=dialog]").TextContent.ShouldContain("Todos los datos de la tienda se reemplazan");
        Continue().HasAttribute("disabled").ShouldBeTrue();
        page.Find("input[aria-label='Frase de los respaldos']").Input("frase equivocada");
        Continue().HasAttribute("disabled").ShouldBeTrue(); // the registers are not confirmed closed yet
        page.Find("input[aria-label='Las cajas están cerradas']").Change(true);
        Continue().Click();
        api.Requests.ShouldNotContain(r => r.Route.EndsWith("/restore")); // nothing until the second confirmation

        Button("Sí, reemplazar los datos").Click();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Esa frase no abre este respaldo."));

        // R9 #3: older than the last e-CF signed: restored only after ticking that those documents are lost here.
        api.Respond("POST", $"/api/backups/{file}/restore", body => body.Contains("\"confirmOlder\":true")
            ? FakeApi.Response(HttpStatusCode.OK, new { backupBefore = "toki-20261015-101010-antes-de-restaurar.toki-backup" })
            : FakeApi.Problem(HttpStatusCode.Conflict, "Backup.OlderThanLastDocument", "Este respaldo es del 14/10/2026 23:30 y el último comprobante…"));
        Continue().Click();
        Button("Sí, reemplazar los datos").Click();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Este respaldo es del 14/10/2026 23:30"));
        Button("Sí, reemplazar los datos").HasAttribute("disabled").ShouldBeTrue();
        page.Find("input[aria-label='Restaurar aunque se pierdan comprobantes']").Change(true);
        Button("Sí, reemplazar los datos").Click();
        page.WaitForAssertion(() => page.Markup.ShouldContain("toki-20261015-101010-antes-de-restaurar.toki-backup"));
        api.Requests.Last(r => r.Route.EndsWith("/restore")).Body.ShouldContain("\"passphrase\":\"frase equivocada\"");

        Button("Volver a entrar").Click();
        ctx.Services.GetRequiredService<Session>().IsOpen.ShouldBeFalse();
        ctx.Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/sign-in");

        AngleSharp.Dom.IElement Button(string text) => page.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains(text));
        AngleSharp.Dom.IElement Continue() => Button("Continuar");
    }
}
