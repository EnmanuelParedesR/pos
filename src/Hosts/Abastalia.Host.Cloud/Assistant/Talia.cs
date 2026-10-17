using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Abastalia.BuildingBlocks.Persistence;

namespace Abastalia.Host.Cloud.Assistant;

/// <summary>What the model reported spending on one reply. <paramref name="Cached"/> is part of <paramref name="Prompt"/> (billed at a tenth);
/// <paramref name="Reasoning"/> is part of <paramref name="Completion"/>.</summary>
public sealed record ChatUsage(int Prompt, int Completion, int Cached = 0, int Reasoning = 0)
{
    public int Total => Prompt + Completion;

    public static ChatUsage operator +(ChatUsage a, ChatUsage b) =>
        new(a.Prompt + b.Prompt, a.Completion + b.Completion, a.Cached + b.Cached, a.Reasoning + b.Reasoning);

    public static readonly ChatUsage None = new(0, 0);
}

/// <summary>How one bot calls the model (TK-348). Empty values keep the deployment's defaults; <see cref="Deployment"/> sends the call to another
/// deployment of the same Azure OpenAI resource. GPT-5 models take <see cref="ReasoningEffort"/> (minimal, low, medium) and <see cref="Verbosity"/>
/// (low, medium); gpt-4.1 rejects both, so leave them empty there.</summary>
public sealed record ChatTuning
{
    public string? Deployment { get; set; }
    public string? ReasoningEffort { get; set; }
    public string? Verbosity { get; set; }
    public int? MaxCompletionTokens { get; set; }

    /// <summary>Set by the loop on its last round: answer with what you have, no more tools.</summary>
    internal bool NoMoreTools { get; init; }

    internal ChatTuning Final() => this with { NoMoreTools = true };
}

/// <summary>Talia's settings (section <c>Talia</c>). The defaults are the ones the evals chose (docs/talia.md, TK-348).</summary>
public sealed class TaliaOptions
{
    public const string Section = "Talia";

    /// <summary>The owner's assistant in the panel: data tools and the knowledge base.</summary>
    /// <remarks>Output (answer + reasoning) is capped per round, so a runaway answer costs at most that (~450 per round measured with gpt-5-nano at low).</remarks>
    public ChatTuning Panel { get; set; } = new() { ReasoningEffort = "low", Verbosity = "low", MaxCompletionTokens = 4000 };

    /// <summary>The site's assistant (abastalia.com): the public knowledge base only.</summary>
    public ChatTuning Public { get; set; } = new() { ReasoningEffort = "low", Verbosity = "low", MaxCompletionTokens = 2500 };

    /// <summary>Tokens the site's assistant may spend per store day, for everyone together; then it says it is resting. 2M ≈ US$0.80 with gpt-5-nano.</summary>
    public int PublicDailyTokens { get; set; } = 2_000_000;

    /// <summary>Of those, what one IP may spend per day (~20 to 40 questions), so one visitor cannot rest Talia for everyone (review of TK-315).</summary>
    public int PublicDailyTokensPerIp { get; set; } = 100_000;

    /// <summary>Questions per minute per IP to the site's assistant.</summary>
    public int PublicPerMinutePerIp { get; set; } = 6;
}

/// <param name="Tools">Each tool the model called, in order.</param>
public sealed record TaliaReply(string Answer, ChatUsage Usage, int Rounds, IReadOnlyList<string> Tools, long Milliseconds);

/// <summary>Tools and plumbing both Talias share (TK-347, TK-348).</summary>
public static class TaliaTools
{
    public static readonly ChatTool SearchKnowledge = new("search_knowledge",
        "Busca en la base de conocimiento de Abastalia: qué es y para qué negocios sirve, funciones, planes y precios, prueba gratis, licencia, " +
        "factura electrónica (e-CF) y la DGII, certificación, equipos (balanza, impresora, escáner), instalación, sin internet, respaldos, " +
        "contacto y problemas comunes. Devuelve secciones con su texto y sus enlaces. Busca con las palabras clave de la pregunta; " +
        "si la pregunta tiene dos temas, búscalos por separado.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Palabras clave, por ejemplo \"precio plan pro\" o \"balanza compatible\"." } },
            ["required"] = new JsonArray("query"),
        });

    /// <summary>Runs <see cref="SearchKnowledge"/>: the sections found, or a note that tells the model to say it does not know.</summary>
    public static string Search(TaliaKnowledge knowledge, JsonElement args, bool includePanel)
    {
        var query = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString()! : "";
        var hits = knowledge.Search(query.Length > 300 ? query[..300] : query, includePanel);
        // "¿Qué es Abastalia?" is all stop words: the overview answers it.
        if (hits.Count == 0 && query.Contains("abastalia", StringComparison.OrdinalIgnoreCase)) hits = knowledge.Search("que es abastalia sistema punto de venta", includePanel);
        return hits.Count == 0
            ? Serialize(new { results = Array.Empty<object>(), note = "No hay nada en la base sobre eso. Prueba otras palabras una vez; si tampoco, di que no lo sabes y ofrece /precios#contacto." })
            : Serialize(new { results = hits.Select(h => new { h.Title, h.Section, h.Text, h.Links }) }); // no article id: the model took it for a path
    }

    /// <summary>The first messages of a question. TK-348: the rules first and the same for everyone, so Azure caches them (a cached token costs a
    /// tenth); the rest of <paramref name="systemPrompt"/> (the day, the stores) after, then the history and the question.</summary>
    public static List<ChatMessage> Conversation(string rules, string systemPrompt, IReadOnlyList<AssistantTurn> history, int maxHistory, string question)
    {
        List<ChatMessage> messages = systemPrompt.StartsWith(rules, StringComparison.Ordinal)
            ? [new("system", rules), new("system", systemPrompt[rules.Length..].Trim())]
            : [new("system", systemPrompt)];
        messages.AddRange(History(history, maxHistory, 2000));
        messages.Add(new("user", question));
        return messages;
    }

    /// <summary>The last <paramref name="max"/> user and assistant turns, each cut at <paramref name="maxLength"/> letters; anything else is dropped.</summary>
    public static IEnumerable<ChatMessage> History(IReadOnlyList<AssistantTurn> history, int max, int maxLength) =>
        history.Where(h => h.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(h.Content)).TakeLast(max)
            .Select(h => new ChatMessage(h.Role!, h.Content!.Length > maxLength ? h.Content[..maxLength] : h.Content));

    // TK-301: accents as is, not ó: the result goes to the model (never into HTML) and each escape costs tokens.
    private static readonly JsonSerializerOptions ToolJson = new(Json.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ToolJson);
}

/// <summary>The loop both Talias share (TK-348): ask, run the tools the model asks for, repeat; the last round has no tools, so it always answers.</summary>
public static partial class TaliaLoop
{
    public const string NoAnswer = "No tengo una respuesta para eso.";

    /// <summary>The last round still asked for tools (the model ignored tool_choice none) and said nothing (TK-337's round-limit case).</summary>
    public const string GaveUp = "No pude completar la respuesta. Intente con una pregunta más concreta.";

    public static async Task<TaliaReply> RunAsync(IChatModel model, ChatTuning tuning, List<ChatMessage> messages, IReadOnlyList<ChatTool> tools,
        Func<ChatToolCall, Task<string>> runTool, int maxRounds, CancellationToken ct, Action<ChatUsage>? spent = null)
    {
        var watch = Stopwatch.StartNew();
        var usage = ChatUsage.None;
        var called = new List<string>();
        for (var round = 1; ; round++)
        {
            var reply = await model.CompleteAsync(messages, tools, round == maxRounds ? tuning.Final() : tuning, ct);
            var cost = reply.Usage ?? new ChatUsage(reply.Tokens, 0);
            usage += cost;
            spent?.Invoke(cost);
            if (reply.ToolCalls is not { Count: > 0 } || round == maxRounds)
            {
                var answer = Clean(reply.Content);
                if (answer.Length == 0) answer = reply.ToolCalls is { Count: > 0 } ? GaveUp : NoAnswer;
                return new(answer, usage, round, called, watch.ElapsedMilliseconds);
            }
            messages.Add(reply);
            foreach (var call in reply.ToolCalls)
            {
                called.Add(call.Name);
                messages.Add(new("tool", await runTool(call), ToolCallId: call.Id));
            }
        }
    }

    /// <summary>The chat shows plain text (React escapes it; only known paths become links). Markdown the model writes anyway would show as
    /// asterisks and hashes, so it is taken out here: bold and code marks, heading hashes, "* " bullets and [text](url) links.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = MarkdownLink().Replace(text, m => m.Groups[1].Value == m.Groups[2].Value ? m.Groups[2].Value : $"{m.Groups[1].Value} ({m.Groups[2].Value})");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Heading().Replace(s, "");
        s = StarBullet().Replace(s, "$1- ");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex(@"\[([^\]\n]+)\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(?m)^[ \t]*#{1,6}[ \t]+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"(?m)^([ \t]*)[*•][ \t]+")]
    private static partial Regex StarBullet();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
