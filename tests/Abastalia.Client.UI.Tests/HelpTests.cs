using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Help;
using Abastalia.Client.UI.Pages.Admin;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-242: F1 opens the help of the screen on view with its shortcuts; the "Primer día" guide is reached from Resumen.</summary>
public sealed class HelpTests
{
    [Fact]
    public void F1_at_the_register_shows_the_sale_help_with_its_shortcuts_and_Esc_closes_it()
    {
        var (ctx, _) = ScreenContext.Create("sales.create");
        using var __ = ctx;
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("sale");
        var frame = ctx.Render<Frame>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "venta"))));

        frame.Find("[data-keys-root]").KeyDown(new KeyboardEventArgs { Key = "F1" });

        frame.WaitForAssertion(() => frame.Find("[role=dialog] h2").TextContent.ShouldBe("Ayuda: Vender"));
        var keys = frame.FindAll(".esc-help__keys dt").Select(k => k.TextContent).ToList();
        keys.ShouldContain("F12");
        frame.Find(".esc-help__keys").TextContent.ShouldContain("Cobrar");
        frame.Markup.ShouldNotContain("Guía del primer día"); // the guide is for the back office

        frame.Find(".esc-overlay").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        frame.FindAll("[role=dialog]").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("admin/purchasing/suppliers?id=1", "Compras y gastos")]
    [InlineData("admin", "Resumen")]
    [InlineData("administrar", "Ayuda")] // a prefix only counts at a whole segment
    [InlineData("close-register", "Cerrar caja")]
    public void Each_address_gets_the_help_of_its_section(string route, string title) =>
        HelpContent.For(route).Title.ShouldBe(title);

    [Fact]
    public void The_first_day_guide_lists_the_steps_in_order_and_Resumen_links_to_it()
    {
        var (ctx, _) = ScreenContext.Create("reports.view");
        using var __ = ctx;

        var guide = ctx.Render<FirstDay>();
        var steps = guide.FindAll("ol li b").Select(b => b.TextContent).ToList();
        steps.First().ShouldBe("Datos del negocio.");
        steps.Last().ShouldBe("Primera venta.");

        ctx.Render<Summary>().Find("a[href='admin/first-day']").TextContent.ShouldBe("Guía del primer día");
    }
}
