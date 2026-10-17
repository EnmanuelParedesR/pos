using Bunit;
using Shouldly;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Help;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-226: the "Novedades" card shows once per user and version, and closing it is saved on the server.</summary>
public sealed class ReleaseNotesTests
{
    [Fact]
    public async Task Shows_the_versions_highlights_until_closed_and_saves_it()
    {
        var (ctx, api) = await ScreenContext.CreateAsync();
        using var _ = ctx;
        api.Json("GET", "/api/settings/release-notes/seen", new { version = (string?)null })
           .Respond("PUT", "/api/settings/release-notes/seen", _ => new(System.Net.HttpStatusCode.NoContent));
        var card = ctx.Render<ReleaseNotesCard>(p => p.Add(c => c.Version, "1.0.0"));

        card.WaitForAssertion(() => card.Find("#release-notes-title").TextContent.ShouldBe("Novedades de la versión 1.0.0"));
        card.FindAll("li").Count.ShouldBe(ReleaseNotes.For("1.0.0")!.Highlights.Count);
        card.Find("a").GetAttribute("href").ShouldBe("admin/whats-new");

        card.Find("button").Click();
        card.WaitForAssertion(() => card.FindAll("section").ShouldBeEmpty());
        api.Requests.ShouldContain(r => r.Method == "PUT" && r.Route == "/api/settings/release-notes/seen" && r.Body.Contains("\"1.0.0\""));
    }

    [Fact]
    public async Task Stays_hidden_once_seen_or_without_notes_for_the_version()
    {
        var (ctx, api) = await ScreenContext.CreateAsync();
        using var _ = ctx;
        api.Json("GET", "/api/settings/release-notes/seen", new { version = "1.0.0" });
        var seen = ctx.Render<ReleaseNotesCard>(p => p.Add(c => c.Version, "1.0.0"));
        seen.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Route == "/api/settings/release-notes/seen"));
        seen.FindAll("section").ShouldBeEmpty();

        ctx.Render<ReleaseNotesCard>(p => p.Add(c => c.Version, "0.0.1")).FindAll("section").ShouldBeEmpty();
        ReleaseNotes.All.ShouldAllBe(n => n.Highlights.Count >= 3 && n.Highlights.Count <= 5);
    }
}
