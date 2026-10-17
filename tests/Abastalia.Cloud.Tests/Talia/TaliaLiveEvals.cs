using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.CashRegister.Contracts;
using Abastalia.Host.Cloud.Assistant;
using Abastalia.Host.Cloud.Panel;
using Abastalia.Identity.Contracts;
using Abastalia.Sales.Contracts;

namespace Abastalia.Cloud.Tests.Talia;

/// <summary>TK-348: Talia against real models, on a store with known numbers. Off unless these are set (they never are in CI):
/// <list type="bullet">
/// <item><c>ABASTALIA_TALIA_EVAL_ENDPOINT</c> and <c>ABASTALIA_TALIA_EVAL_KEY</c>: an Azure OpenAI resource that takes keys (not production's).</item>
/// <item><c>ABASTALIA_TALIA_EVAL_MODELS</c>: deployment[:reasoning[:verbosity]] separated by commas, e.g. <c>gpt-5-nano:low:low,gpt-5-mini:minimal</c>.</item>
/// <item><c>ABASTALIA_TALIA_EVAL_JUDGE</c> (optional): a deployment that grades each answer 1–5 against what the tools returned.</item>
/// <item><c>ABASTALIA_TALIA_EVAL_REPEAT</c> (optional, 1): runs per case, to see how much a model varies.</item>
/// <item><c>ABASTALIA_TALIA_EVAL_ONLY</c> (optional): a case id prefix, e.g. <c>public-</c>.</item>
/// </list>
/// Writes <c>TestResults/talia/report-*.md</c> (deploy/azure/talia-evals.ps1 sets all this up). How to read it: docs/talia.md.</summary>
public sealed class TaliaLiveEvals
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    [Fact]
    public async Task Talia_answers_the_eval_cases_with_each_model()
    {
        var endpoint = Env("ABASTALIA_TALIA_EVAL_ENDPOINT");
        var key = Env("ABASTALIA_TALIA_EVAL_KEY");
        var models = Env("ABASTALIA_TALIA_EVAL_MODELS");
        if (endpoint is null || key is null || models is null) Assert.Skip("Set ABASTALIA_TALIA_EVAL_ENDPOINT, _KEY and _MODELS to run Talia against real models.");

        var options = new AzureOpenAIOptions { Endpoint = endpoint, ApiKey = key, Deployment = "unused", ApiVersion = Env("ABASTALIA_TALIA_EVAL_API_VERSION") ?? "2025-04-01-preview" };
        var model = new LiveRecordingModel(new AzureOpenAIChatModel(new HttpClient { Timeout = TimeSpan.FromSeconds(120) }, options));
        var judge = Env("ABASTALIA_TALIA_EVAL_JUDGE");
        var repeat = int.TryParse(Env("ABASTALIA_TALIA_EVAL_REPEAT"), out var r) ? Math.Max(r, 1) : 1;
        var only = Env("ABASTALIA_TALIA_EVAL_ONLY");
        var cases = LiveCase.Load().Where(c => only is null || c.Id.StartsWith(only, StringComparison.Ordinal)).ToList();

        await using var app = new LiveApp(model);
        var store = await LiveStore.SeedAsync(app);

        var runs = new List<Run>();
        foreach (var spec in models.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = spec.Split(':');
            var tuning = new ChatTuning
            {
                Deployment = parts[0],
                ReasoningEffort = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null,
                Verbosity = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null,
                // The production cap per round (TaliaOptions.Public, the tighter of the two), so an answer cut by it shows here first.
                MaxCompletionTokens = new TaliaOptions().Public.MaxCompletionTokens,
            };
            // Up to four questions at a time per model: well under the deployment's per-minute limit.
            using var gate = new SemaphoreSlim(4);
            var tasks = cases.SelectMany(c => Enumerable.Range(0, repeat).Select(async i =>
            {
                await gate.WaitAsync();
                try { return await RunAsync(app, store, model, tuning, spec, c, judge); }
                finally { gate.Release(); }
            }));
            runs.AddRange(await Task.WhenAll(tasks));
        }

        var report = Report(runs, cases, repeat, judge);
        var folder = Path.Combine(Repo.Root, "TestResults", "talia");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(file, report);
        await File.WriteAllTextAsync(Path.ChangeExtension(file, ".json"), JsonSerializer.Serialize(runs, Indented));
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        TestContext.Current.TestOutputHelper?.WriteLine($"Report: {file}");
        runs.ShouldNotBeEmpty();
    }

    private static async Task<Run> RunAsync(LiveApp app, LiveStore store, LiveRecordingModel model, ChatTuning tuning, string spec, LiveCase c, string? judge)
    {
        var trace = new LiveTrace();
        var watch = Stopwatch.StartNew();
        TaliaReply reply;
        try
        {
            using var scope = LiveRecordingModel.Begin(trace);
            if (c.Bot == "public")
                reply = await new PublicAssistant(model, tuning).AskAsync(c.Q, c.History ?? [], CloudApp.Today, CancellationToken.None);
            else
            {
                await using var services = app.Services.CreateAsyncScope();
                var owner = new OwnerAssistant(model, services.ServiceProvider.GetRequiredService<PanelQueries>(), showCosts: true, tuning: tuning);
                await owner.AskAsync(store.TenantId, c.Q, c.History ?? [], CancellationToken.None);
                reply = owner.LastReply!;
            }
        }
        catch (Exception ex)
        {
            return new Run(spec, c.Id, c.Bot, "", ["error: " + ex.Message], [], ChatUsage.None, watch.ElapsedMilliseconds, false, null, null);
        }

        var failures = c.Check(reply.Answer, reply.Tools, trace.RawAnswers);
        int? score = null;
        string? why = null;
        if (judge is not null) (score, why) = await JudgeAsync(model, judge, c, reply.Answer, trace.ToolResults);
        return new Run(spec, c.Id, c.Bot, reply.Answer, failures, reply.Tools, reply.Usage, reply.Milliseconds, failures.Count == 0, score, why);
    }

    private static async Task<(int?, string?)> JudgeAsync(LiveRecordingModel model, string judge, LiveCase c, string answer, List<string> toolResults)
    {
        var evidence = toolResults.Count == 0 ? "(ninguna herramienta)" : string.Join("\n---\n", toolResults.Select(t => t.Length > 6000 ? t[..6000] : t));
        var prompt = $$"""
            Evalúa la respuesta de Talia, la asistente de Abastalia (punto de venta con factura electrónica en República Dominicana).
            Pregunta: {{c.Q}}
            Lo que se espera: {{c.Rubric ?? "Una respuesta correcta, breve y útil, solo con lo que dicen las herramientas."}}
            Lo que devolvieron las herramientas (la fuente de los datos y de lo que dice de Abastalia):
            {{evidence}}
            Talia además sabe, sin herramientas, y nada de esto es inventado: hoy es jueves 15 de octubre de 2026; el negocio del panel es
            "Colmado La Esquina"; y puede proponer estas páginas: /precios, /precios#contacto, /descargar, /guia-e-cf, /e-cf, /certificacion,
            /comparar, /glosario, /app y las pantallas /app/... que nombran las herramientas.
            Respuesta de Talia:
            {{answer}}

            Califica de 1 a 5: 5 = correcta, completa, breve y sin nada inventado; 3 = útil pero le falta algo o sobra; 1 = falsa, inventada o no ayuda.
            Cualquier cifra, función o promesa que no esté en las herramientas es inventada (máximo 2). Rechazar bien lo que no debe hacer vale 5.
            Contesta solo JSON: {"score": n, "why": "una frase"}
            """;
        try
        {
            var reply = await model.Inner.CompleteAsync([new("user", prompt)], [], new ChatTuning { Deployment = judge, ReasoningEffort = "low" }, CancellationToken.None);
            var json = Regex.Match(reply.Content ?? "", @"\{[\s\S]*\}").Value;
            using var doc = JsonDocument.Parse(json);
            return (doc.RootElement.GetProperty("score").GetInt32(), doc.RootElement.TryGetProperty("why", out var w) ? w.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or HttpRequestException or InvalidOperationException)
        {
            return (null, "judge failed: " + ex.Message);
        }
    }

    /// <summary>US$ per million tokens (input, cached input, output), Azure Global Standard, eastus2 retail prices of 2026-10-17.</summary>
    private static readonly Dictionary<string, (decimal In, decimal Cached, decimal Out)> Prices = new()
    {
        ["gpt-5-nano"] = (0.05m, 0.005m, 0.40m),
        ["gpt-4.1-nano"] = (0.10m, 0.025m, 0.40m),
        ["gpt-5.4-nano"] = (0.20m, 0.02m, 1.25m),
        ["gpt-5.6-luna"] = (0.20m, 0.02m, 1.20m),
        ["gpt-5-mini"] = (0.25m, 0.025m, 2.00m),
        ["gpt-4.1-mini"] = (0.40m, 0.10m, 1.60m),
        ["gpt-5.4-mini"] = (0.75m, 0.075m, 4.50m),
    };

    private static decimal? Cost(string spec, ChatUsage u) =>
        Prices.TryGetValue(spec.Split(':')[0], out var p)
            ? ((u.Prompt - u.Cached) * p.In + u.Cached * p.Cached + u.Completion * p.Out) / 1_000_000m
            : null;

    private static string Report(List<Run> runs, List<LiveCase> cases, int repeat, string? judge)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# Talia evals · {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"{cases.Count} casos × {repeat} · knowledge {TaliaKnowledge.Embedded.Version} · juez: {judge ?? "ninguno"}");
        sb.AppendLine();
        sb.AppendLine("| Modelo | Pasa reglas | Juez (1–5) | Juez ≥4 | Tokens/preg. (entrada · caché · salida · razon.) | US$/1,000 preg. | p50 ms | p95 ms |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var g in runs.GroupBy(x => x.Model))
        {
            var n = g.Count();
            var scored = g.Where(x => x.Score is not null).ToList();
            var total = g.Aggregate(ChatUsage.None, (a, x) => a + x.Usage);
            var cost = Cost(g.Key, total);
            var ms = g.Select(x => x.Milliseconds).Order().ToList();
            sb.AppendLine(string.Create(inv, $"| {g.Key} | {g.Count(x => x.Passed)}/{n} ({100.0 * g.Count(x => x.Passed) / n:0}%) | " +
                $"{(scored.Count == 0 ? "—" : scored.Average(x => x.Score!.Value).ToString("0.00", inv))} | " +
                $"{(scored.Count == 0 ? "—" : $"{100.0 * scored.Count(x => x.Score >= 4) / scored.Count:0}%")} | " +
                $"{total.Prompt / n:N0} · {total.Cached / n:N0} · {total.Completion / n:N0} · {total.Reasoning / n:N0} | " +
                $"{(cost is null ? "?" : (cost.Value / n * 1000).ToString("0.00", inv))} | {ms[ms.Count / 2]:N0} | {ms[(int)Math.Min(ms.Count - 1, Math.Ceiling(ms.Count * 0.95) - 1)]:N0} |"));
        }
        sb.AppendLine();
        sb.AppendLine("## Por bot");
        sb.AppendLine();
        sb.AppendLine("| Modelo | Bot | Pasa | Juez |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var g in runs.GroupBy(x => (x.Model, x.Bot)))
        {
            var scored = g.Where(x => x.Score is not null).ToList();
            sb.AppendLine(string.Create(inv, $"| {g.Key.Model} | {g.Key.Bot} | {g.Count(x => x.Passed)}/{g.Count()} | {(scored.Count == 0 ? "—" : scored.Average(x => x.Score!.Value).ToString("0.00", inv))} |"));
        }
        sb.AppendLine();
        sb.AppendLine("## Fallas y notas bajas");
        foreach (var x in runs.Where(x => !x.Passed || x.Score < 4).OrderBy(x => x.Case).ThenBy(x => x.Model))
        {
            sb.AppendLine();
            sb.AppendLine($"- **{x.Case}** · {x.Model} · juez {x.Score?.ToString(inv) ?? "—"} · herramientas: {string.Join(", ", x.Tools)}");
            foreach (var f in x.Failures) sb.AppendLine($"  - regla: {f}");
            if (x.Why is not null) sb.AppendLine($"  - juez: {x.Why}");
            sb.AppendLine($"  - respuesta: {x.Answer.ReplaceLineEndings(" ⏎ ")}");
        }
        return sb.ToString();
    }

    public sealed record Run(string Model, string Case, string Bot, string Answer, IReadOnlyList<string> Failures, IReadOnlyList<string> Tools, ChatUsage Usage,
        long Milliseconds, bool Passed, int? Score, string? Why);
}

/// <summary>One eval case from <c>Talia/cases.json</c>.</summary>
/// <param name="Contains">Every group must appear in the answer; a group passes with any of its options (no accents, no case, 2,500 = 2500).</param>
/// <param name="Tools">Each one must have been called; ["none"] = no tool at all.</param>
public sealed record LiveCase(string Id, string Bot, string Q, List<AssistantTurn>? History, List<string>? Tools, List<List<string>>? Contains,
    List<string>? NotContains, string? Rubric)
{
    public static List<LiveCase> Load() =>
        JsonSerializer.Deserialize<List<LiveCase>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Talia", "cases.json")), WebJson)!;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public List<string> Check(string answer, IReadOnlyList<string> called, IReadOnlyList<string> raw)
    {
        var failures = new List<string>();
        var text = Normalize(answer);
        foreach (var group in Contains ?? [])
            if (!group.Any(option => text.Contains(Normalize(option), StringComparison.Ordinal))) failures.Add($"falta {string.Join(" o ", group)}");
        foreach (var bad in NotContains ?? [])
            if (text.Contains(Normalize(bad), StringComparison.Ordinal)) failures.Add($"dice {bad}");
        if (Tools is ["none"]) { if (called.Count > 0) failures.Add($"usó {string.Join(", ", called)} sin necesidad"); }
        else foreach (var tool in Tools ?? []) if (!called.Contains(tool)) failures.Add($"no usó {tool}");
        if (raw.Any(r => r.Contains("**", StringComparison.Ordinal) || Regex.IsMatch(r, @"(?m)^#{1,6} "))) failures.Add("escribió Markdown (se limpió)");
        if (answer.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length > 160) failures.Add("más de 160 palabras");
        return failures;
    }

    private static string Normalize(string s)
    {
        var folded = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) folded.Append(ch);
        return Regex.Replace(folded.ToString(), @"(?<=\d),(?=\d{3})", "");
    }
}

/// <summary>What the model said and what the tools returned during one question, for the checks and the judge.</summary>
public sealed class LiveTrace
{
    public List<string> RawAnswers { get; } = [];
    public List<string> ToolResults { get; } = [];
}

/// <summary>Passes every call to the real model and writes into the current question's <see cref="LiveTrace"/> (async-local, so parallel questions
/// keep theirs apart).</summary>
public sealed class LiveRecordingModel(IChatModel inner) : IChatModel
{
    private static readonly AsyncLocal<LiveTrace?> Current = new();

    public IChatModel Inner => inner;

    public static IDisposable Begin(LiveTrace trace)
    {
        Current.Value = trace;
        return new End();
    }

    public Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct) =>
        CompleteAsync(messages, tools, new ChatTuning(), ct);

    public async Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, ChatTuning tuning, CancellationToken ct)
    {
        if (Current.Value is { } trace)
            lock (trace) trace.ToolResults.AddRange(messages.Where(m => m.Role == "tool" && !trace.ToolResults.Contains(m.Content!)).Select(m => m.Content!));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var reply = await inner.CompleteAsync(messages, tools, tuning, ct);
                if (Current.Value is { } t && reply.Content is { Length: > 0 } c) lock (t) t.RawAnswers.Add(c);
                return reply;
            }
            catch (HttpRequestException ex) when (attempt < 4 && ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests or >= System.Net.HttpStatusCode.InternalServerError)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }

    private sealed class End : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}

public sealed class LiveApp(IChatModel model) : CloudApp(model);

/// <summary>"Colmado La Esquina" with numbers the cases know (today is Thursday 2026-10-15, store time):
/// today 3 sales = RD$ 2,500 (María: 1,250 cash chuleta, 450 transfer arroz; José: 800 card pollo); yesterday 2 = RD$ 1,600 (José 1,000 cash
/// pollo; María 600 cash chuleta); Monday 1 = RD$ 2,000 (José, card, res para guisar). Yesterday's Z: María #7 short RD$ 150, José #8 even.</summary>
public sealed record LiveStore(Guid TenantId, Guid StoreId)
{
    public static async Task<LiveStore> SeedAsync(CloudApp app)
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner, "Colmado La Esquina");
        var maria = Guid.CreateVersion7();
        var jose = Guid.CreateVersion7();
        Guid chuleta = Guid.CreateVersion7(), pollo = Guid.CreateVersion7(), arroz = Guid.CreateVersion7(), res = Guid.CreateVersion7();
        var now = app.Clock.GetUtcNow().UtcDateTime; // 11:00 in the store
        var yesterday = now.AddDays(-1);
        var register1 = Guid.CreateVersion7();
        var register2 = Guid.CreateVersion7();
        (await node.SendAsync(
            Events.Envelope(new UserProfileChangedV1(maria, "María", true), now.AddDays(-5)),
            Events.Envelope(new UserProfileChangedV1(jose, "José", true), now.AddDays(-5)),
            Events.Envelope(new RegisterChangedV1(register1, "Caja 1"), now.AddDays(-5)),
            Events.Envelope(new RegisterChangedV1(register2, "Caja 2"), now.AddDays(-5)),
            Events.Envelope(PanelTests.Sale(1250, maria, chuleta, "Chuleta de cerdo", new SalePaymentV1("Cash", 1250)), now.AddHours(-2)),
            Events.Envelope(PanelTests.Sale(450, maria, arroz, "Arroz selecto 10 lb", new SalePaymentV1("Transfer", 450)), now.AddHours(-1)),
            Events.Envelope(PanelTests.Sale(800, jose, pollo, "Pollo entero", new SalePaymentV1("Card", 800)), now.AddMinutes(-30)),
            Events.Envelope(PanelTests.Sale(1000, jose, pollo, "Pollo entero", new SalePaymentV1("Cash", 1000)), yesterday),
            Events.Envelope(PanelTests.Sale(600, maria, chuleta, "Chuleta de cerdo", new SalePaymentV1("Cash", 600)), yesterday.AddHours(1)),
            Events.Envelope(PanelTests.Sale(2000, jose, res, "Res para guisar", new SalePaymentV1("Card", 2000)), now.AddDays(-3)),
            Events.Envelope(new ShiftClosedV1(Guid.CreateVersion7(), register1, maria, 7, 3000, 2850, -150), yesterday.AddHours(8)),
            Events.Envelope(new ShiftClosedV1(Guid.CreateVersion7(), register2, jose, 8, 2000, 2000, 0), yesterday.AddHours(8))))
            .EnsureSuccessStatusCode();
        await app.ApplyAsync();
        var me = await owner.Client.GetFromJsonAsync<JsonElement>("/api/me");
        return new(me.GetProperty("tenantId").GetGuid(), node.StoreId);
    }
}
