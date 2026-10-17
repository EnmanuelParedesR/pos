using System.Net;
using Bunit;
using Shouldly;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages.Admin;

namespace Abastalia.Client.UI.Tests;

/// <summary>M16 (TK-212..215): the role editor shows the segmented permissions in the owner's words, and deleting a role in use shows why not.</summary>
public sealed class RolesSettingsTests
{
    private static readonly Guid Cashier = Guid.NewGuid();
    private static readonly Guid Unused = Guid.NewGuid();

    private static FakeApi Roles(FakeApi api) => api.Json("GET", "/api/identity/roles", new
    {
        roles = new[]
        {
            new { id = Cashier, name = "Cajero", permissions = new[] { "sales.create", "register.close", "credit.collect" } },
            new { id = Unused, name = "Viejo", permissions = new[] { "sales.create" } },
        },
        availablePermissions = new[]
        {
            "register.open", "register.close", "register.cash-in", "register.approve-close", "sales.create", "credit.collect",
            "purchasing.manage", "payables.pay", "reports.view", "shifts.view", "fiscal.reports", "settings.manage", "registers.manage", "backups.manage", "audit.view",
        },
    });

    [Fact]
    public async Task The_segmented_permissions_show_in_their_groups_with_the_owners_words()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("users.manage");
        using var _ = ctx;
        Roles(api);
        var page = ctx.Render<RolesSettings>();

        page.WaitForAssertion(() => page.Find("button[aria-label='Editar Cajero']").Click());
        var legends = page.FindAll("legend").Select(l => l.TextContent).ToList();
        legends.ShouldBe(["Caja", "Ventas", "Clientes y fiado", "Compras", "Reportes", "Administración"]);
        string Label(string text) => page.FindAll("label.esc-check").Single(l => l.TextContent.Contains(text)).TextContent;
        Label("Abrir turno").ShouldNotBeNull();
        Label("Cerrar su propio turno").ShouldNotBeNull();
        Label("Cobrar abonos de fiado").ShouldNotBeNull();
        Label("Pagar a suplidores").ShouldNotBeNull();
        Label("Turnos y cuadres").ShouldNotBeNull();
        Label("reportes de la DGII").ShouldNotBeNull();
        Label("respaldos").ShouldNotBeNull();
        page.FindAll("label.esc-check").ShouldNotContain(l => l.TextContent.Contains('.')); // no permission shown by its code
    }

    [Fact]
    public async Task A_role_in_use_is_not_deleted_and_the_server_says_why()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("users.manage");
        using var _ = ctx;
        Roles(api)
            .Respond("DELETE", $"/api/identity/roles/{Cashier}", _ => FakeApi.Problem(HttpStatusCode.Conflict, "Identity.RoleInUse", "Ese rol tiene usuarios. Cámbieles el rol antes de borrarlo."))
            .Respond("DELETE", $"/api/identity/roles/{Unused}", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = ctx.Render<RolesSettings>();

        page.WaitForAssertion(() => page.Find("button[aria-label='Editar Cajero']").Click());
        page.FindAll("[role=dialog] .esc-dialog__footer button").Single(b => b.TextContent == "Borrar rol").Click();
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("Ese rol tiene usuarios"));

        page.FindAll("[role=dialog] .esc-dialog__footer button").Single(b => b.TextContent.Contains("Cancelar")).Click();
        page.Find("button[aria-label='Editar Viejo']").Click();
        page.FindAll("[role=dialog] .esc-dialog__footer button").Single(b => b.TextContent == "Borrar rol").Click();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Rol Viejo borrado."));
        api.Requests.Count(r => r.Method == "DELETE").ShouldBe(2);
    }

    [Fact]
    public async Task Turnos_y_cuadres_and_the_DGII_reports_follow_their_own_permissions()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("fiscal.reports");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business", new { name = "Mi tienda", discountCapPercent = 10m });
        var page = ctx.Render<Reports>();

        page.Markup.ShouldContain("Formato 607");
        page.Markup.ShouldNotContain("Turnos y cuadres");
        page.Markup.ShouldNotContain("Ventas por día");
        page.FindAll("nav a").ShouldNotContain(a => a.TextContent.Contains("Turnos y cuadres"));
    }
}
