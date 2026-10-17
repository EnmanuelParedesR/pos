using System.Net;
using System.Reflection;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Talia;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-342: Talia in Administración. She answers in plain text, links only the store's screens and leaves "Abrir …" for the person to click.</summary>
public sealed class TaliaWindowTests
{
    private static async Task<(BunitContext Ctx, FakeApi Api, IRenderedComponent<Admin> Shell)> OpenAsync(params string[] permissions)
    {
        var (ctx, api) = await ScreenContext.CreateAsync(permissions);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("admin");
        var shell = ctx.Render<Admin>(p => p.Add(x => x.Title, "Resumen"));
        return (ctx, api, shell);
    }

    private static void Ask(IRenderedComponent<Admin> shell, string question)
    {
        shell.Find("#talia-question").Input(question);
        shell.Find(".talia-drawer__composer").Submit();
    }

    [Fact]
    public void Every_screen_Talia_may_open_is_a_page_of_the_store_app()
    {
        var pages = typeof(Admin).Assembly.GetTypes().SelectMany(t => t.GetCustomAttributes<RouteAttribute>()).Select(r => r.Template).ToHashSet();

        StoreScreens.All.Count.ShouldBeGreaterThan(15);
        StoreScreens.All.Select(s => s.Route).ShouldBeUnique();
        foreach (var s in StoreScreens.All)
        {
            pages.ShouldContain(s.Route, s.Route);
            s.Label.ShouldNotBeNullOrWhiteSpace();
        }
        // Every section of the back office's menu can be suggested.
        foreach (var section in new[] { "/admin/products", "/admin/inventory", "/admin/purchasing", "/admin/shifts", "/admin/customers", "/admin/orders",
                     "/admin/invoices", "/admin/reports", "/admin/audit", "/admin/ecf", "/admin/settings" })
            StoreScreens.Label(section).ShouldNotBeNull(section);
    }

    [Fact]
    public async Task The_answer_leaves_an_Abrir_button_and_only_the_click_opens_the_screen()
    {
        var (ctx, api, shell) = await OpenAsync("reports.view", "products.manage");
        using var _ = ctx;
        api.Json("POST", "/api/assistant/ask", new { answer = "Se cambia en Administración → Productos.", screen = "/admin/products" });
        var nav = ctx.Services.GetRequiredService<NavigationManager>();

        shell.Find("button[title='Pregúntele a Talia']").Click();
        shell.Find("[role=dialog] h2").TextContent.ShouldBe("Talia");
        Ask(shell, "¿Dónde cambio un precio?");

        shell.WaitForAssertion(() => shell.Find(".talia-drawer__go").TextContent.ShouldContain("Abrir Productos"));
        nav.Uri.ShouldEndWith("/admin"); // she suggests; she never moves the screen
        var sent = JsonDocument.Parse(api.Requests.Single(r => r.Route == "/api/assistant/ask").Body).RootElement;
        sent.GetProperty("question").GetString().ShouldBe("¿Dónde cambio un precio?");
        sent.GetProperty("history").GetArrayLength().ShouldBe(0);

        shell.Find(".talia-drawer__go").Click();
        nav.Uri.ShouldEndWith("/admin/products");
        shell.FindAll(".talia-drawer").ShouldBeEmpty();

        // Open again: the chat is still there, and the next question carries it.
        shell.Find("button[title='Pregúntele a Talia']").Click();
        shell.FindAll(".esc-bubble--user").Single().TextContent.ShouldBe("¿Dónde cambio un precio?");
        Ask(shell, "¿Y el de la pechuga?");
        shell.WaitForAssertion(() => shell.FindAll(".esc-bubble--assistant").Count.ShouldBe(2));
        JsonDocument.Parse(api.Requests.Last(r => r.Route == "/api/assistant/ask").Body).RootElement.GetProperty("history").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Plain_text_with_links_only_to_the_stores_screens_and_no_button_for_an_unknown_one()
    {
        var (ctx, api, shell) = await OpenAsync("reports.view");
        using var _ = ctx;
        api.Json("POST", "/api/assistant/ask", new { answer = "Vea /admin/reports, el plan en /app/billing y <b>esto</b> /admin/platform.", screen = "/admin/platform" });

        shell.Find("button[title='Pregúntele a Talia']").Click();
        Ask(shell, "¿Dónde veo las ventas?");

        shell.WaitForAssertion(() => shell.FindAll(".esc-bubble--assistant").Count.ShouldBe(1));
        var bubble = shell.Find(".esc-bubble--assistant");
        bubble.QuerySelectorAll("a").Select(a => (a.GetAttribute("href"), a.TextContent)).ShouldBe([("admin/reports", "Reportes")]); // the screen's name, not its path
        bubble.QuerySelectorAll("b").ShouldBeEmpty(); // escaped, never HTML
        bubble.TextContent.ShouldContain("<b>esto</b>");
        shell.FindAll(".talia-drawer__go").ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_internet_she_says_so_in_amber_and_keeps_the_question()
    {
        var (ctx, api, shell) = await OpenAsync("reports.view");
        using var _ = ctx;
        api.Respond("POST", "/api/assistant/ask", _ => FakeApi.Problem(HttpStatusCode.Conflict, "Talia.NeedsInternet",
            "Talia necesita internet. Revise la conexión e intente de nuevo; el resto de la app sigue funcionando."));

        shell.Find("button[title='Pregúntele a Talia']").Click();
        Ask(shell, "¿Cómo conecto la balanza?");

        shell.WaitForAssertion(() => shell.Find(".esc-notice--warning").TextContent.ShouldContain("Talia necesita internet"));
        shell.Find("#talia-question").GetAttribute("value").ShouldBe("¿Cómo conecto la balanza?");
        shell.FindAll(".esc-bubble--user").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cashier_does_not_get_Talia_and_another_user_starts_with_an_empty_chat()
    {
        var (ctx, _, shell) = await OpenAsync("sales.create", "customers.manage");
        using var __ = ctx;
        shell.FindAll("button[title='Pregúntele a Talia']").ShouldBeEmpty();

        var office = ctx.Services.GetRequiredService<State.BackOffice>();
        office.Talia(Guid.NewGuid()).Add((new Api.TaliaTurn("user", "¿Cuánto vendí?"), null));
        office.TaliaOpen = true;
        office.Talia(Guid.NewGuid()).ShouldBeEmpty();
        office.TaliaOpen.ShouldBeFalse();
    }
}
