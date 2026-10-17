using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Abastalia.Host.Cloud.Assistant;

namespace Abastalia.Cloud.Tests;

/// <summary>Calls navigate with the question's last word as the route (whatever it is), then answers "Listo.".</summary>
public sealed class NavigateModel : IChatModel
{
    public IReadOnlyList<ChatTool> Offered { get; private set; } = [];
    public string? ToolResult { get; private set; }

    public Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct)
    {
        Offered = tools;
        var result = messages.LastOrDefault(m => m.Role == "tool");
        ToolResult = result?.Content;
        var route = messages[^1].Content!.Split(' ')[^1];
        return Task.FromResult(result is null
            ? new ChatMessage("assistant", null, [new ChatToolCall("n1", "navigate", JsonSerializer.Serialize(new { route }))])
            : new ChatMessage("assistant", "Listo."));
    }
}

public sealed class NavigationApp() : CloudApp(new NavigateModel())
{
    public NavigateModel Model => (NavigateModel)Services.GetService(typeof(IChatModel))!;
}

/// <summary>TK-339: Talia suggests a screen of a closed list; the panel shows an "Ir a …" button the user clicks.</summary>
public sealed class TaliaNavigationTests(NavigationApp app) : IClassFixture<NavigationApp>
{
    private static string[] Enum(IReadOnlyList<ChatTool> tools) =>
        [.. tools.Single(t => t.Name == "navigate").Parameters["properties"]!["route"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>())];

    [Fact]
    public async Task The_owner_gets_the_route_back_as_navigation()
    {
        var owner = await app.OwnerAsync();

        var r = await owner.Client.PostAsJsonAsync("/api/assistant/ask", new { question = "Llévame a /app/billing" });

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await r.Json();
        (json.GetProperty("answer").GetString(), json.GetProperty("navigation").GetString()).ShouldBe(("Listo.", "/app/billing"));
        Enum(app.Model.Offered).ShouldBe(PanelRoutes.All.Select(p => p.Route));
        app.Model.ToolResult!.ShouldContain("Ir a Plan y facturación");
    }

    [Theory]
    [InlineData("https://evil.example/app")]
    [InlineData("/app/platform")]
    [InlineData("/app/sales?x=1")]
    [InlineData("javascript:alert(1)")]
    public async Task A_route_outside_the_enum_is_dropped(string route)
    {
        var owner = await app.OwnerAsync();

        var json = await (await owner.Client.PostAsJsonAsync("/api/assistant/ask", new { question = $"Llévame a {route}" })).Json();

        json.GetProperty("navigation").ValueKind.ShouldBe(JsonValueKind.Null);
        JsonDocument.Parse(app.Model.ToolResult!).RootElement.GetProperty("error").GetString()!.ShouldContain("no existe");
    }

    [Fact]
    public async Task A_read_only_member_is_not_offered_the_owners_screens()
    {
        var owner = await app.OwnerAsync();
        var email = $"member{Guid.NewGuid():N}@example.com";
        var link = (await (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role = "ReadOnly" })).Json()).GetProperty("link").GetString()!;
        (await app.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = link[(link.IndexOf('#') + 1)..], password = CloudApp.Password })).EnsureSuccessStatusCode();
        var member = await app.SignInAsync(email);

        var json = await (await member.Client.PostAsJsonAsync("/api/assistant/ask", new { question = "Llévame a /app/members" })).Json();

        json.GetProperty("navigation").ValueKind.ShouldBe(JsonValueKind.Null);
        Enum(app.Model.Offered).ShouldBe(PanelRoutes.All.Where(p => !p.OwnerOnly).Select(p => p.Route));
        Enum(app.Model.Offered).ShouldContain("/app/sales");
    }

    [Fact]
    public async Task Bad_arguments_change_nothing()
    {
        var assistant = new OwnerAssistant(null!, null!); // navigate reads no data
        foreach (var args in new[] { "", "[]", """{"route":5}""", """{"to":"/app"}""" })
            JsonDocument.Parse(await assistant.RunToolAsync(Guid.NewGuid(), new ChatToolCall("1", "navigate", args), CancellationToken.None)).RootElement
                .TryGetProperty("error", out _).ShouldBeTrue(args);
        assistant.Navigation.ShouldBeNull();
    }

    [Fact]
    public void Every_public_route_is_a_page_of_the_site()
    {
        // Panel.tsx routes are checked by the panel's own test (assistantRoutes.test.ts); here the public pages and the labels.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Abastalia.slnx"))) root = root.Parent!;
        var site = File.ReadAllText(Path.Combine(root.FullName, "src", "Cloud", "panel", "src", "pages", "site", "SitePages.tsx"));
        PanelRoutes.All.ShouldAllBe(p => !string.IsNullOrWhiteSpace(p.Label));
        PanelRoutes.All.Select(p => p.Route).ShouldBeUnique();
        foreach (var p in PanelRoutes.All.Where(p => !p.Route.StartsWith("/app", StringComparison.Ordinal)))
            site.ShouldContain($"path: \"{p.Route}\"", customMessage: p.Route);
    }
}
