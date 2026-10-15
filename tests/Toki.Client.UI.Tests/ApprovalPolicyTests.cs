using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Client.UI.Api;
using Toki.Client.UI.Components;
using Toki.Client.UI.Pages;
using Toki.Client.UI.Pages.Admin;
using Toki.Client.UI.State;

namespace Toki.Client.UI.Tests;

/// <summary>TK-210: removing a line or voiding the sale in progress asks for a supervisor only when the store says so.</summary>
public sealed class ApprovalPolicyTests
{
    private static IRenderedComponent<Fragment> Register(BunitContext ctx) => ctx.Render<Fragment>(p => p.Add(x => x.Content, b =>
    {
        b.OpenComponent<Sale>(0);
        b.CloseComponent();
        b.OpenComponent<ApprovalDialog>(1);
        b.CloseComponent();
    }));

    private static (BunitContext, FakeApi, Cart) WithOneLine(bool required)
    {
        var (ctx, api) = ScreenContext.Create("sales.create", "register.operate");
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>())
           .Json("GET", "/api/settings/approvals", new[] { new ApprovalRule("sales.remove-line", required), new ApprovalRule("sales.void", true) });
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(RegisterTests.Salami, null);
        return (ctx, api, cart);
    }

    [Fact]
    public void With_the_policy_on_removing_a_line_asks_for_a_supervisor()
    {
        var (ctx, api, cart) = WithOneLine(required: true);
        using var _ = ctx;
        var approval = Guid.NewGuid();
        api.Json("GET", "/api/identity/users/active", new[] { new ActiveUser(Guid.NewGuid(), "Ramón D.", "ramon") })
           .Json("POST", "/api/identity/approvals", new ApprovalGranted(approval, "Ramón D.", DateTime.UtcNow.AddMinutes(2)));
        var screen = Register(ctx);

        screen.Find(".sale").KeyDown(new KeyboardEventArgs { Key = "Delete" });
        screen.WaitForAssertion(() => screen.FindAll("h2").Select(h => h.TextContent).ShouldContain("Autoriza un supervisor"));
        cart.IsEmpty.ShouldBeFalse();
        screen.WaitForAssertion(() => screen.Find("[aria-label=Supervisor] .esc-chip").TextContent.ShouldBe("Ramón D."));
        var dialog = screen.FindAll(".esc-overlay")[^1];
        foreach (var d in "2468") dialog.KeyDown(new KeyboardEventArgs { Key = d.ToString() });
        dialog.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        screen.WaitForAssertion(() => cart.IsEmpty.ShouldBeTrue());
        api.Requests.Single(r => r.Route == "/api/identity/approvals").Body.ShouldContain("sales.remove-line");
    }

    [Fact]
    public void With_the_policy_off_the_line_goes_at_once()
    {
        var (ctx, api, cart) = WithOneLine(required: false);
        using var _ = ctx;
        var screen = Register(ctx);

        screen.Find(".sale").KeyDown(new KeyboardEventArgs { Key = "Delete" });

        screen.WaitForAssertion(() => cart.IsEmpty.ShouldBeTrue());
        api.Requests.ShouldNotContain(r => r.Route == "/api/identity/approvals");
    }

    [Fact]
    public void Configuracion_saves_one_switch_per_action()
    {
        var (ctx, api) = ScreenContext.Create("settings.manage");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business", new { name = "Mi tienda", discountCapPercent = 10m })
           .Json("GET", "/api/cash/registers", Array.Empty<object>())
           .Json("GET", "/api/settings/approvals", new[] { new ApprovalRule("sales.remove-line", false), new ApprovalRule("sales.void", true) })
           .Respond("PUT", "/api/settings/approvals", _ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        var page = ctx.Render<Settings>();

        page.WaitForAssertion(() => page.FindAll("[role=tab]").Single(t => t.TextContent == "Cajas").Click());
        page.WaitForAssertion(() => page.FindAll("input[role=switch]").Count.ShouldBe(2));
        page.Markup.ShouldContain("Quitar líneas o anular la venta en curso");
        page.FindAll("input[role=switch]")[0].Change(true);
        page.FindAll("input[role=switch]")[1].Change(false);
        page.FindAll("form").Single(f => f.TextContent.Contains("Guardar autorizaciones")).Submit();

        page.WaitForAssertion(() => api.Requests.Single(r => r.Method == "PUT").Body.ShouldBe("{\"required\":[\"sales.remove-line\"]}"));
    }
}
