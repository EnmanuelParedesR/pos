using System.Net;
using Bunit;
using Shouldly;
using Abastalia.Client.UI.Pages.Admin;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-196/220: the trial's warnings, "Comprar licencia" and the sample data on the license screen.</summary>
public sealed class TrialLicenseTests
{
    private static object Features(string status, int? daysLeft, string? warning) => new
    {
        status, plan = (string?)null, expiresUtc = (DateTime?)null, enabled = Array.Empty<string>(), registers = status == "Restricted" ? 1 : 2,
        trialEndsUtc = daysLeft is null ? (DateTime?)null : DateTime.UtcNow.AddDays(daysLeft.Value), trialDaysLeft = daysLeft, warning,
    };

    [Theory]
    [InlineData("Trial", null, null, "Prueba gratis: los 14 días empiezan con la primera venta")]
    [InlineData("Trial", 9, null, "Prueba gratis · quedan 9 días")]
    [InlineData("Trial", 2, "Last", "La prueba termina en 2 días")]
    [InlineData("Restricted", null, "Ended", "Modo restringido: la venta sigue")]
    [InlineData("Active", null, null, null)]
    public void The_note_is_calm_until_the_last_days(string status, int? daysLeft, string? warning, string? expected)
    {
        var note = Names.LicenseNote(new Api.LicenseFeatures(status, null, null, [], 2, null, daysLeft, warning));
        if (expected is null) note.ShouldBeNull();
        else note.ShouldStartWith(expected);
    }

    [Fact]
    public void A_pasted_license_code_is_installed_and_sample_data_removed_with_one_click()
    {
        var (ctx, api) = ScreenContext.Create("settings.manage", "products.manage");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business", new { name = "Mi tienda", discountCapPercent = 10m })
           .Json("GET", "/api/settings/features", Features("Restricted", null, "Ended"))
           .Json("GET", "/api/inventory/sample-data", new { products = 9, categories = 3 })
           .Respond("PUT", "/api/settings/license", _ =>
           {
               api.Json("GET", "/api/settings/features", Features("Active", null, null));
               return new HttpResponseMessage(HttpStatusCode.NoContent);
           })
           .Respond("DELETE", "/api/inventory/sample-data", _ =>
           {
               api.Json("GET", "/api/inventory/sample-data", new { products = 0, categories = 0 });
               return FakeApi.Response(HttpStatusCode.OK, new { products = 0, categories = 0 });
           });
        var page = ctx.Render<Settings>();

        page.WaitForAssertion(() => page.FindAll("[role=tab]").Single(t => t.TextContent == "Licencia").Click());
        page.WaitForAssertion(() => page.Markup.ShouldContain("Modo restringido"));
        page.Markup.ShouldContain("Hay 9 productos de ejemplo.");

        page.Find("textarea[aria-label='Código de licencia']").Input("eyJhbGciOiJFUzI1NiJ9.e30.firma");
        page.FindAll("form").Single(f => f.TextContent.Contains("Activar licencia")).Submit();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Licencia activada"));
        api.Requests.Single(r => r.Route == "/api/settings/license").Body.ShouldContain("eyJhbGciOiJFUzI1NiJ9.e30.firma");
        page.Markup.ShouldNotContain("Modo restringido");

        page.FindAll("button").Single(b => b.TextContent == "Quitar datos de ejemplo").Click();
        page.WaitForAssertion(() => page.Markup.ShouldContain("No hay productos de ejemplo."));
    }
}
