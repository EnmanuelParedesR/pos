using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Host.Cloud.Assistant;
using Abastalia.Host.Cloud.Panel;

namespace Abastalia.Cloud.Tests;

/// <summary>Calls panel_help with the topic it was given, then answers with the tool's result.</summary>
public sealed class HelpModel(string topic) : IChatModel
{
    public List<ChatMessage> Last { get; private set; } = [];
    public IReadOnlyList<ChatTool> Offered { get; private set; } = [];

    public Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct)
    {
        (Last, Offered) = ([.. messages], tools);
        var result = messages.LastOrDefault(m => m.Role == "tool");
        return Task.FromResult(result is null
            ? new ChatMessage("assistant", null, [new ChatToolCall("h1", "panel_help", JsonSerializer.Serialize(new { topic }))], Tokens: 100)
            : new ChatMessage("assistant", result.Content, Tokens: 100));
    }
}

/// <summary>TK-301: Talia knows the panel, the plans, the license states, the store's Configuración and the certification steps.</summary>
public sealed class PanelHelpTests(CloudApp app) : IClassFixture<CloudApp>
{
    [Theory]
    [InlineData("¿Dónde activo los planes?", "planes", "/app/billing", "Elegir plan")]
    [InlineData("¿Cómo agrego otra caja?", "planes", "Caja adicional", "Adapte su plan")]
    [InlineData("No puedo pagar en línea", "planes", "/precios#contacto", "escríbanos")]
    [InlineData("La caja dice que la licencia está bloqueada, ¿qué hago?", "licencia", "Activar licencia", "Ningún dato se pierde")]
    [InlineData("¿Dónde pongo el logo de la factura?", "tienda", "Negocio", "logo")]
    [InlineData("¿Cómo cambio el ambiente de la DGII?", "tienda", "CerteCF", "Comprobantes")]
    [InlineData("¿Cómo activo la verificación en dos pasos?", "panel", "/app/security", "dos pasos")]
    [InlineData("¿Dónde veo cuántas preguntas me quedan?", "panel", "/app/stores", "Consumo del mes")]
    [InlineData("¿Cuáles son los pasos de la certificación?", "certificacion", "Paso 14", "Solicitud FI-GDF-016")]
    public async Task A_how_to_question_gets_the_right_section_with_its_link(string question, string topic, string expected, string alsoExpected)
    {
        var owner = await app.OwnerAsync();
        var model = new HelpModel(topic);
        await using var scope = app.Services.CreateAsyncScope();
        var assistant = new OwnerAssistant(model, scope.ServiceProvider.GetRequiredService<PanelQueries>());

        var answer = await assistant.AskAsync(owner.TenantId, question, [], CancellationToken.None);

        model.Offered.ShouldContain(t => t.Name == "panel_help");
        model.Last[0].Content!.ShouldContain("panel_help");
        var result = JsonDocument.Parse(answer).RootElement;
        result.GetProperty("topic").GetString().ShouldBe(topic);
        answer.ShouldContain(expected);
        answer.ShouldContain(alsoExpected);
        assistant.TokensUsed.ShouldBe(200); // TK-248: counted like any other tool round
    }

    [Fact]
    public async Task Without_a_known_topic_it_returns_every_section()
    {
        var assistant = new OwnerAssistant(null!, null!); // panel_help reads no data
        var all = JsonDocument.Parse(await assistant.RunToolAsync(Guid.NewGuid(), new ChatToolCall("1", "panel_help", """{"topic":"otra"}"""), CancellationToken.None)).RootElement;
        all.EnumerateObject().Select(p => p.Name).ShouldBe(PanelHelp.Topics.Keys, ignoreOrder: true);
        (await assistant.RunToolAsync(Guid.NewGuid(), new ChatToolCall("2", "panel_help", ""), CancellationToken.None)).ShouldBe(all.GetRawText());
    }

    [Fact]
    public void The_help_has_no_prices_and_every_link_is_a_page_of_the_panel_or_the_site()
    {
        var entries = PanelHelp.Topics.Values.SelectMany(e => e).ToList();
        entries.ShouldAllBe(e => !e.Text.Contains("US$") && !e.Text.Contains("RD$") && !Regex.IsMatch(e.Text, @"\$\s?\d"));

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Abastalia.slnx"))) root = root.Parent!;
        var panel = File.ReadAllText(Path.Combine(root.FullName, "src", "Cloud", "panel", "src", "Panel.tsx"));
        var site = File.ReadAllText(Path.Combine(root.FullName, "src", "Cloud", "panel", "src", "pages", "site", "SitePages.tsx"));
        foreach (var link in entries.Select(e => e.Link).OfType<string>().Distinct())
        {
            var path = link.Split('#')[0];
            if (path == "/app") continue; // the index route
            if (path.StartsWith("/app/", StringComparison.Ordinal)) panel.ShouldContain($"path=\"{path["/app/".Length..]}\"", customMessage: link);
            else site.ShouldContain($"path: \"{path}\"", customMessage: link);
        }
    }
}
