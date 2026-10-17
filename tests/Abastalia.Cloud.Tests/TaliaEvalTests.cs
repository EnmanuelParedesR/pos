using System.Text.Json.Nodes;
using Shouldly;
using Abastalia.Host.Cloud.Assistant;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-337: plays a case's script as the model, and keeps what the assistant sent it on each round.</summary>
public sealed class ScriptedChatModel(IReadOnlyList<EvalReply> script) : IChatModel
{
    public List<(List<ChatMessage> Messages, IReadOnlyList<ChatTool> Tools)> Rounds { get; } = [];

    public Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct)
    {
        var round = Rounds.Count;
        Rounds.Add(([.. messages], tools));
        if (round >= script.Count) throw new InvalidOperationException($"The script has {script.Count} replies; the assistant asked for one more.");
        var reply = script[round];
        return Task.FromResult(new ChatMessage("assistant", reply.Content,
            reply.ToolCalls?.Select((c, i) => new ChatToolCall($"call_{round}_{i}", c.Tool, c.Args?.ToJsonString() ?? "{}")).ToList(), Tokens: 10));
    }
}

public sealed class TaliaEvalTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "TaliaEvals");
    private static readonly IReadOnlyList<EvalCase> All = TaliaEval.Load(Folder);

    public static TheoryData<string> Cases => [.. All.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_case_passes_with_its_script_and_tool_results_reach_the_model_as_tool_messages(string name)
    {
        var c = All.Single(x => x.Name == name);
        var model = new ScriptedChatModel(c.Script ?? throw new InvalidOperationException($"{name} has no script."));

        var r = await TaliaEval.RunAsync(model, c, CancellationToken.None);

        r.Failures.ShouldBeEmpty(string.Join("; ", r.Failures));
        model.Rounds.Count.ShouldBe(c.Script.Count);
        // TK-348: the rules and the day's context are two system messages (the rules first, so Azure caches them).
        string.Join("\n", model.Rounds[0].Messages.Where(m => m.Role == "system").Select(m => m.Content)).ShouldContain("2026-10-15");
        model.Rounds[0].Messages[^1].ShouldBe(new ChatMessage("user", c.Question));
        model.Rounds[0].Tools.Select(t => t.Name).ShouldBe(new OwnerAssistant(model, null!).Offered.Select(t => t.Name)); // the read tools plus navigate (TK-339)
        r.Calls.Count.ShouldBe(c.Script.Sum(s => s.ToolCalls?.Count ?? 0));
        for (var round = 1; round < model.Rounds.Count; round++)
        {
            // Each call of the previous reply is answered by a tool message with its id, after the reply; never in a user or system message.
            var messages = model.Rounds[round].Messages;
            var calls = messages.Last(m => m.Role == "assistant").ToolCalls!;
            var results = messages.SkipWhile(m => m.ToolCalls != calls).Skip(1).ToList();
            results.Select(m => (m.Role, m.ToolCallId)).ShouldBe(calls.Select(call => ("tool", (string?)call.Id)));
            foreach (var (call, result) in calls.Zip(results))
                if (c.ToolData?.TryGetValue(call.Name, out var data) == true)
                    JsonNode.DeepEquals(JsonNode.Parse(result.Content!), data).ShouldBeTrue(call.Name);
        }
    }

    [Fact]
    public void Cases_have_unique_names_a_script_and_cover_every_read_tool()
    {
        All.Count.ShouldBeGreaterThanOrEqualTo(20);
        All.Select(c => c.Name).ShouldBeUnique();
        All.ShouldAllBe(c => c.Script != null && c.Script.Count > 0);
        var expected = All.SelectMany(c => c.Expect.Calls ?? []).Select(x => x.Tool).ToHashSet();
        OwnerAssistant.Tools.Select(t => t.Name).Where(t => !expected.Contains(t)).ShouldBeEmpty();
        All.Count(c => c.Name.StartsWith("refusal.", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task A_model_that_keeps_calling_tools_stops_at_the_round_limit()
    {
        var loop = Enumerable.Repeat(new EvalReply(ToolCalls: [new EvalCall("sync_status")]), 10).ToList();
        var model = new ScriptedChatModel(loop);

        var r = await TaliaEval.RunAsync(model, new EvalCase("loop", "¿Qué hay?", new EvalExpect(AnswerContains: ["No pude completar"])), CancellationToken.None);

        r.Passed.ShouldBeTrue(string.Join("; ", r.Failures));
        model.Rounds.Count.ShouldBe(OwnerAssistant.MaxRounds);
        r.Tokens.ShouldBe(10 * OwnerAssistant.MaxRounds);
    }

    [Fact]
    public async Task Wrong_answers_are_reported_one_by_one()
    {
        var model = new ScriptedChatModel([
            new(ToolCalls: [new EvalCall("sales_by_day", new JsonObject { ["from"] = "2026-10-14", ["to"] = "2026-10-15" }), new EvalCall("void_sale")]),
            new(Content: "Vendiste RD$ 500.00, nadie te debe."),
        ]);
        var c = new EvalCase("wrong", "¿Cuánto vendí hoy?", new EvalExpect(
            Calls: [new EvalCall("sales_by_day", new JsonObject { ["from"] = "2026-10-15", ["to"] = "*", ["storeId"] = null })],
            Forbidden: ["void_sale"], AnswerContains: ["18,350"], AnswerAnyOf: ["hoy"], AnswerNotContains: ["NADIE"]));

        var r = await TaliaEval.RunAsync(model, c, CancellationToken.None);

        r.Failures.Count.ShouldBe(5);
        r.Failures[0].ShouldStartWith("expected sales_by_day");
        r.Failures.ShouldContain("called the forbidden tool void_sale");
        // The unknown tool got an error as its result, which the model reads as data.
        model.Rounds[1].Messages[^1].Content!.ShouldContain("No existe la herramienta void_sale");
        r.Replies.Count.ShouldBe(2);
        r.Replies[0].ToolCalls![0].Args!["from"]!.GetValue<string>().ShouldBe("2026-10-14");
    }

    [Theory]
    [InlineData("""{"to":"2026-10-15"}""", """{"to":"2026-10-15"}""", true)]
    [InlineData("""{"topic":"PLANES"}""", """{"topic":"planes"}""", true)]
    [InlineData("""{"limit":"5"}""", """{"limit":5}""", true)]
    [InlineData("""{"to":"*"}""", """{"to":""}""", false)]
    [InlineData("""{"storeId":null}""", """{"storeId":""}""", true)]
    [InlineData("""{"storeId":null}""", """{"storeId":"x"}""", false)]
    [InlineData("""{"q":"~pollo"}""", """{"q":"Pechuga de POLLO"}""", true)]
    [InlineData("""{"q":"cafe"}""", """{"q":"Café"}""", true)]
    [InlineData("""{"to":"2026-10-15"}""", "no json", false)]
    public void Argument_matchers(string expected, string actual, bool matches) =>
        (TaliaEval.ArgsMismatch(JsonNode.Parse(expected)!.AsObject(), actual) is null).ShouldBe(matches);

    [Fact]
    public async Task The_command_prints_a_score_and_records_the_replies()
    {
        var record = Path.Combine(Path.GetTempPath(), $"talia_eval_{Guid.NewGuid():N}");
        var output = new StringWriter();

        var code = await TaliaEval.CliAsync(new ScriptedChatModel([new(Content: "Pregúntele a su contador.")]),
            [Folder, "--only", "asesoria-fiscal", "--record", record], output, CancellationToken.None);

        code.ShouldBe(0);
        output.ToString().ShouldContain("PASS refusal.asesoria-fiscal");
        output.ToString().ShouldContain("1/1 casos bien (100.0 %)");
        File.ReadAllText(Path.Combine(record, "refusal.asesoria-fiscal.json")).ShouldContain("Pregúntele a su contador.");
        (await TaliaEval.CliAsync(null, [], new StringWriter(), CancellationToken.None)).ShouldBe(2);
        Directory.Delete(record, true);
    }
}
