using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
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
    [InlineData("Restricted", null, "Ended", "Modo restringido")] // TK-279: with the date it stops selling when the server says it
    [InlineData("Blocked", null, "Blocked", "Licencia vencida: no se puede vender")]
    [InlineData("Active", null, null, null)]
    public void The_note_is_calm_until_the_last_days(string status, int? daysLeft, string? warning, string? expected)
    {
        var note = Names.LicenseNote(new Api.LicenseFeatures(status, null, null, [], 2, null, daysLeft, warning));
        if (expected is null) note.ShouldBeNull();
        else note.ShouldStartWith(expected);
    }

    [Fact]
    public void TK279_Restricted_says_when_the_sale_stops()
    {
        var note = Names.LicenseNote(new Api.LicenseFeatures("Restricted", null, null, [], 1, null, null, "Ended", new DateTime(2026, 12, 1, 16, 0, 0, DateTimeKind.Utc)));
        note.ShouldStartWith("Modo restringido: la venta se detiene el 01/12/2026");
    }

    [Fact]
    public async Task TK279_a_blocked_register_shows_the_full_screen_message_instead_of_the_sale()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create", "settings.manage");
        using var _ = ctx;
        api.Json("GET", "/api/settings/features", new
        {
            status = "Blocked", plan = "Estándar", expiresUtc = DateTime.UtcNow.AddDays(-40), enabled = Array.Empty<string>(), registers = 1, warning = "Blocked",
            clockBehind = true,
        });
        var frame = ctx.Render<Abastalia.Client.UI.Components.Frame>(p => p.Add(x => x.Body,
            (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "PANTALLA DE VENTA"))));

        frame.WaitForAssertion(() => frame.Find("main [role=alert] h1").TextContent.ShouldBe("Su licencia está vencida"));
        frame.Markup.ShouldNotContain("PANTALLA DE VENTA");
        frame.Find("main").TextContent.ShouldContain("Plan y facturación");
        frame.FindAll("main button").ShouldContain(b => b.TextContent == "Activar licencia");
        frame.Find(".esc-status-bar").TextContent.ShouldContain("La hora de la PC está atrasada");
    }

    [Fact]
    public async Task TK287_the_blocked_screen_signs_out_and_asks_first_about_a_sale_on_the_screen()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        api.Json("GET", "/api/settings/features", new
            {
                status = "Blocked", plan = "Estándar", expiresUtc = DateTime.UtcNow.AddDays(-40), enabled = Array.Empty<string>(), registers = 1, warning = "Blocked",
            })
           .Respond("DELETE", "/api/identity/sessions/current", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var cart = ctx.Services.GetRequiredService<Abastalia.Client.UI.State.Cart>();
        cart.Add(new Abastalia.Client.UI.Api.ProductByCode(Guid.NewGuid(), null, "Refresco", "Unit", "Exempt", 59m, null, 1m, null, null, [], null), null);
        var frame = ctx.Render<Abastalia.Client.UI.Components.Frame>(p => p.Add(x => x.Body,
            (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "PANTALLA DE VENTA"))));

        frame.WaitForAssertion(() => frame.Find("main [role=alert] h1").TextContent.ShouldBe("Su licencia está vencida"));
        frame.FindAll("main button").ShouldNotContain(b => b.TextContent == "Activar licencia"); // a cashier: the owner signs in to do it
        frame.FindAll("main button").Single(b => b.TextContent.Contains("Cerrar sesión")).Click();
        frame.WaitForAssertion(() => frame.Find("[role=dialog]").TextContent.ShouldContain("Hay una venta sin cobrar."));
        api.Requests.ShouldNotContain(r => r.Method == "DELETE");

        frame.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Anularla y salir")).Click();
        frame.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Method == "DELETE" && r.Route == "/api/identity/sessions/current"));
        ctx.Services.GetRequiredService<Abastalia.Client.UI.State.Session>().IsOpen.ShouldBeFalse();
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.ShouldEndWith("/sign-in");
        frame.Markup.ShouldNotContain("Su licencia está vencida"); // the sign-in screen, not the block, for the next user
    }

    [Theory]
    [InlineData("Trial", "Terminó la prueba gratis", "Compre su plan")]
    [InlineData("Suspended", "Abastalia suspendió el servicio de su negocio", "para resolverlo")]
    [InlineData("Expired", "Su licencia está vencida", "Pague su plan")]
    public async Task R12_the_block_screen_says_why(string reason, string title, string wayOut)
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        api.Json("GET", "/api/settings/features", new
        {
            status = "Blocked", plan = (string?)null, expiresUtc = (DateTime?)null, enabled = Array.Empty<string>(), registers = 1, warning = "Blocked", blockReason = reason,
        });
        var frame = ctx.Render<Abastalia.Client.UI.Components.Frame>(p => p.Add(x => x.Body,
            (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "PANTALLA DE VENTA"))));

        frame.WaitForAssertion(() => frame.Find("main [role=alert] h1").TextContent.ShouldBe(title));
        frame.Find("main [role=alert]").TextContent.ShouldContain(wayOut);
        Names.LicenseNote(new Api.LicenseFeatures("Blocked", null, null, [], 1, Warning: "Blocked", BlockReason: reason))!.ShouldStartWith(title == "Su licencia está vencida" ? "Licencia vencida" : title[..12]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R12_an_uncertain_checkout_is_resolved_from_the_block_screen(bool saved)
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Abastalia.Client.UI.State.Cart>();
        cart.Add(new Abastalia.Client.UI.Api.ProductByCode(Guid.NewGuid(), null, "Refresco", "Unit", "Exempt", 59m, null, 1m, null, null, [], null), null);
        cart.CheckoutUncertain = true;
        api.Json("GET", "/api/settings/features", new
            {
                status = "Blocked", plan = "Estándar", expiresUtc = DateTime.UtcNow.AddDays(-40), enabled = Array.Empty<string>(), registers = 1, warning = "Blocked",
            })
           .Respond("DELETE", "/api/identity/sessions/current", _ => new HttpResponseMessage(HttpStatusCode.NoContent))
           .Respond("GET", $"/api/sales/{cart.SaleId}", _ => saved
               ? FakeApi.Response(HttpStatusCode.OK, new { id = cart.SaleId, number = 7L, eNcf = "E320000000007", total = 59m })
               : new HttpResponseMessage(HttpStatusCode.NotFound))
           .Json("GET", $"/api/sales/{cart.SaleId}/receipt", new { change = 1m });
        var frame = ctx.Render<Abastalia.Client.UI.Components.Frame>(p => p.Add(x => x.Body,
            (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "PANTALLA DE VENTA"))));

        frame.WaitForAssertion(() => frame.FindAll("main button").Single(b => b.TextContent.Contains("Cerrar sesión")).Click());
        frame.WaitForAssertion(() => frame.Find("[role=dialog]").TextContent.ShouldContain("El último cobro no recibió respuesta."));
        frame.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Anularla y salir")).HasAttribute("disabled").ShouldBeTrue(); // the trap before R12
        frame.FindAll("[role=dialog] button").Single(b => b.TextContent == "Comprobar el último cobro").Click();
        if (!saved)
        {
            frame.WaitForAssertion(() => frame.Find("[role=dialog]").TextContent.ShouldContain("Ese cobro no se guardó"));
            frame.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Anularla y salir")).Click();
        }
        frame.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Method == "DELETE" && r.Route == "/api/identity/sessions/current"));
        cart.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task A_pasted_license_code_is_installed_and_sample_data_removed_with_one_click()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("settings.manage", "products.manage");
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
