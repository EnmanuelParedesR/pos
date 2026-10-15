using System.Net;
using Bunit;
using Shouldly;
using Toki.Client.UI.Pages.Admin;

namespace Toki.Client.UI.Tests;

/// <summary>TK-138: the owner connects the store to the cloud with the panel's code.</summary>
public sealed class CloudSettingsTests
{
    private static object Status(bool connected) =>
        new { connected, tenantId = connected ? Guid.NewGuid() : (Guid?)null, storeId = (Guid?)null, cloudUrl = "https://toki.test", lastUploadUtc = (DateTime?)null, pendingEvents = 3, lastError = (string?)null };

    [Fact]
    public void The_code_connects_the_store_and_the_status_shows_connected()
    {
        var (ctx, api) = ScreenContext.Create("settings.manage");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business", new { name = "Mi tienda", rnc = (string?)null, address = (string?)null, phone = (string?)null, receiptFooter = (string?)null, discountCapPercent = 10m })
           .Json("GET", "/api/sync/status", Status(false))
           .Respond("POST", "/api/sync/connect", _ =>
           {
               api.Json("GET", "/api/sync/status", Status(true));
               return FakeApi.Response(HttpStatusCode.OK, new { });
           });
        var page = ctx.Render<Settings>();

        page.WaitForAssertion(() => page.FindAll("[role=tab]").Single(t => t.TextContent == "Nube").Click());
        page.WaitForAssertion(() => page.Find("input[aria-label='Código de conexión']").Input("12345678"));
        page.Find("form").Submit();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Conectada"));
        page.Markup.ShouldContain("Pendientes por subir: <b>3</b>");
        api.Requests.Single(r => r.Route == "/api/sync/connect").Body.ShouldContain("\"code\":\"12345678\"");
    }

    [Fact]
    public void An_invalid_code_is_explained_in_Spanish()
    {
        var (ctx, api) = ScreenContext.Create("settings.manage");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business", new { name = "Mi tienda", discountCapPercent = 10m })
           .Json("GET", "/api/sync/status", Status(false))
           .Respond("POST", "/api/sync/connect", _ => FakeApi.Problem(HttpStatusCode.BadRequest, "Sync.InvalidCode", "El código no es válido o ya venció. Pida uno nuevo en el panel de Toki."));
        var page = ctx.Render<Settings>();

        page.WaitForAssertion(() => page.FindAll("[role=tab]").Single(t => t.TextContent == "Nube").Click());
        page.WaitForAssertion(() => page.Find("input[aria-label='Código de conexión']").Input("11112222"));
        page.Find("form").Submit();

        page.WaitForAssertion(() => page.Markup.ShouldContain("El código no es válido o ya venció"));
    }
}
