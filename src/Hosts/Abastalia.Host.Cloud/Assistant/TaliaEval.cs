using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abastalia.BuildingBlocks.Persistence;

namespace Abastalia.Host.Cloud.Assistant;

/// <summary>TK-337: one scripted conversation of Talia's evaluation (format in docs/talia-acciones.md §6).</summary>
/// <param name="ToolData">What each read tool returns in this case (any JSON). A read tool without data returns <c>[]</c>;
/// certification_faq and panel_help return their real content.</param>
/// <param name="Script">The model's replies for CI (the fake model plays them in order); talia-eval ignores it and can record new ones.</param>
public sealed record EvalCase(string Name, string Question, EvalExpect Expect, IReadOnlyList<AssistantTurn>? History = null,
    EvalContext? Context = null, Dictionary<string, JsonNode?>? ToolData = null, IReadOnlyList<EvalReply>? Script = null);

/// <param name="Role">owner or readonly; with Plan, informative until TK-338 filters the tools by permission.</param>
public sealed record EvalContext(string? Role = null, string? Plan = null, DateOnly? Today = null, Dictionary<Guid, string>? Stores = null);

/// <param name="Calls">Each must match one call the model made: same tool and every listed argument
/// (text equal ignoring case and accents; "*" any value; "~x" contains x; null absent or empty). Other arguments are not checked.</param>
/// <param name="Forbidden">Tools the model must not call.</param>
/// <param name="NoTools">The model must answer without calling any tool.</param>
/// <param name="AnswerContains">Every fragment must be in the answer (ignoring case and accents).</param>
/// <param name="AnswerAnyOf">At least one must be in the answer (refusals worded in different ways).</param>
public sealed record EvalExpect(IReadOnlyList<EvalCall>? Calls = null, IReadOnlyList<string>? Forbidden = null, bool NoTools = false,
    IReadOnlyList<string>? AnswerContains = null, IReadOnlyList<string>? AnswerAnyOf = null, IReadOnlyList<string>? AnswerNotContains = null);

public sealed record EvalCall(string Tool, JsonObject? Args = null);

public sealed record EvalReply(string? Content = null, IReadOnlyList<EvalCall>? ToolCalls = null);

/// <param name="Replies">What the model answered, in the script's format (talia-eval --record).</param>
public sealed record EvalResult(EvalCase Case, string Answer, IReadOnlyList<ChatToolCall> Calls, IReadOnlyList<string> Failures,
    IReadOnlyList<EvalReply> Replies, int Tokens)
{
    public bool Passed => Failures.Count == 0;
}

/// <summary>TK-337: runs the cases through the assistant's real rounds (<see cref="OwnerAssistant.ConverseAsync"/>) with the case's tool data.
/// CI plays each case's script (deterministic); <c>talia-eval</c> runs them against the real Azure OpenAI deployment.</summary>
public static class TaliaEval
{
    public const string Command = "talia-eval";
    public const string DefaultFolder = "tests/Abastalia.Cloud.Tests/TaliaEvals";
    public static readonly DateOnly DefaultToday = new(2026, 10, 15);
    public static readonly Guid DefaultStoreId = Guid.Parse("0199f000-0000-7000-8000-000000000001");

    private static readonly JsonSerializerOptions CaseJson = new(Json.Options)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>Every <c>*.json</c> of the folder holds an array of cases.</summary>
    public static IReadOnlyList<EvalCase> Load(string folder) =>
        [.. Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal)
            .SelectMany(f => JsonSerializer.Deserialize<List<EvalCase>>(File.ReadAllText(f), CaseJson)
                ?? throw new InvalidDataException($"{f}: no cases."))];

    public static async Task<EvalResult> RunAsync(IChatModel model, EvalCase c, CancellationToken ct)
    {
        var recorder = new Recorder(model);
        var assistant = new OwnerAssistant(recorder, null!); // the tools read the case's data, never a replica
        var calls = new List<ChatToolCall>();
        var failures = new List<string>();
        var prompt = OwnerAssistant.SystemPrompt(c.Context?.Today ?? DefaultToday,
            c.Context?.Stores ?? new() { [DefaultStoreId] = "Carnicería La Central" });
        string answer;
        try
        {
            answer = await assistant.ConverseAsync(prompt, c.Question, c.History ?? [], call =>
            {
                calls.Add(call);
                return ToolResultAsync(assistant, c, call, ct);
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            answer = "";
            failures.Add($"error: {ex.GetType().Name}: {ex.Message}");
        }

        var e = c.Expect;
        foreach (var expected in e.Calls ?? [])
            if (!calls.Any(a => a.Name == expected.Tool && ArgsMismatch(expected.Args, a.Arguments) is null))
                failures.Add($"expected {expected.Tool} {expected.Args?.ToJsonString() ?? "{}"}; got " +
                    (calls.Count == 0 ? "no tool" : string.Join(", ", calls.Select(a => $"{a.Name} {a.Arguments}"))));
        foreach (var name in (e.Forbidden ?? []).Where(n => calls.Any(a => a.Name == n)))
            failures.Add($"called the forbidden tool {name}");
        if (e.NoTools && calls.Count > 0) failures.Add($"expected no tool; got {string.Join(", ", calls.Select(a => a.Name))}");
        foreach (var fragment in (e.AnswerContains ?? []).Where(f => !Contains(answer, f)))
            failures.Add($"answer lacks \"{fragment}\"");
        if (e.AnswerAnyOf is { Count: > 0 } any && !any.Any(f => Contains(answer, f)))
            failures.Add($"answer has none of {string.Join(" | ", any.Select(f => $"\"{f}\""))}");
        foreach (var fragment in (e.AnswerNotContains ?? []).Where(f => Contains(answer, f)))
            failures.Add($"answer has \"{fragment}\"");

        return new(c, answer, calls, failures, recorder.Replies, assistant.TokensUsed);
    }

    private static async Task<string> ToolResultAsync(OwnerAssistant assistant, EvalCase c, ChatToolCall call, CancellationToken ct)
    {
        if (c.ToolData?.TryGetValue(call.Name, out var data) == true) return data?.ToJsonString(CaseJson) ?? "null";
        if (call.Name is "certification_faq" or "panel_help" or "navigate") return await assistant.RunToolAsync(Guid.Empty, call, ct); // static content; navigate checks its route list
        return OwnerAssistant.Tools.Any(t => t.Name == call.Name) ? "[]" : OwnerAssistant.Failure($"No existe la herramienta {call.Name}.");
    }

    /// <returns>Why the arguments do not match, or null when they do.</returns>
    public static string? ArgsMismatch(JsonObject? expected, string actualJson)
    {
        if (expected is null) return null;
        JsonObject actual;
        try { actual = JsonNode.Parse(string.IsNullOrWhiteSpace(actualJson) ? "{}" : actualJson) as JsonObject ?? []; }
        catch (JsonException) { return "arguments are not JSON"; }
        foreach (var (key, want) in expected)
        {
            var got = actual.TryGetPropertyValue(key, out var g) && g is not null && g.ToString() is { Length: > 0 } text ? text : null;
            var w = want?.ToString();
            var ok = w switch
            {
                null => got is null,
                "*" => got is not null,
                ['~', .. var part] => got is not null && Contains(got, part),
                _ => got is not null && Compare.Compare(got, w, Loose) == 0,
            };
            if (!ok) return $"{key}: wanted {w ?? "nothing"}, got {got ?? "nothing"}";
        }
        return null;
    }

    private static bool Contains(string text, string fragment) => Compare.IndexOf(text, fragment, Loose) >= 0;

    /// <summary><c>talia-eval [folder] [--only text] [--record folder]</c>: prints each case, the pass % and the tokens. Exit 0 when it ran.</summary>
    public static async Task<int> CliAsync(IChatModel? model, string[] args, TextWriter output, CancellationToken ct)
    {
        if (model is null)
        {
            await output.WriteLineAsync("AzureOpenAI no está configurado: falta AzureOpenAI:Endpoint o AzureOpenAI:Deployment (docs/talia-acciones.md §6).");
            return 2;
        }
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var options = new[] { "--only", "--record" };
        var folder = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !options.Contains(args[i - 1]))).FirstOrDefault() ?? DefaultFolder;
        var only = Option("--only");
        var record = Option("--record");
        if (!Directory.Exists(folder))
        {
            await output.WriteLineAsync($"No existe la carpeta de casos {Path.GetFullPath(folder)}.");
            return 2;
        }

        var cases = Load(folder).Where(c => only is null || c.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (record is not null) Directory.CreateDirectory(record);
        var passed = 0;
        var tokens = 0;
        foreach (var c in cases)
        {
            var r = await RunAsync(model, c, ct);
            if (r.Passed) passed++;
            tokens += r.Tokens;
            await output.WriteLineAsync($"{(r.Passed ? "PASS" : "FAIL")} {c.Name} ({r.Replies.Count} vueltas, {r.Tokens:N0} tokens)");
            foreach (var f in r.Failures) await output.WriteLineAsync($"     - {f}");
            if (!r.Passed)
            {
                foreach (var call in r.Calls) await output.WriteLineAsync($"     llamó {call.Name} {call.Arguments}");
                await output.WriteLineAsync($"     respuesta: {r.Answer.ReplaceLineEndings(" ")}");
            }
            if (record is not null)
                await File.WriteAllTextAsync(Path.Combine(record, $"{c.Name}.json"), JsonSerializer.Serialize(new { c.Name, Script = r.Replies }, CaseJson), ct);
        }
        var percent = cases.Count == 0 ? 0 : 100.0 * passed / cases.Count;
        await output.WriteLineAsync($"{passed}/{cases.Count} casos bien ({percent:0.0} %), {tokens:N0} tokens.");
        return 0;
    }

    /// <summary>Keeps the model's replies in the script's format.</summary>
    private sealed class Recorder(IChatModel inner) : IChatModel
    {
        public List<EvalReply> Replies { get; } = [];

        public async Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct)
        {
            var reply = await inner.CompleteAsync(messages, tools, ct);
            Replies.Add(new(reply.Content, reply.ToolCalls?.Select(c => new EvalCall(c.Name, ParseObject(c.Arguments))).ToList()));
            return reply;
        }

        private static JsonObject? ParseObject(string json)
        {
            try { return JsonNode.Parse(json) as JsonObject; }
            catch (JsonException) { return null; }
        }
    }
}
