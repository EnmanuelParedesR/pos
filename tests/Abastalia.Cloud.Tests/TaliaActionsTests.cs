using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Host.Cloud.Assistant;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Quotas;
using Abastalia.SharedKernel;

namespace Abastalia.Cloud.Tests;

/// <summary>A question "propose_x {json} ;; propose_y {json}" makes those calls in one reply, then answers "Listo."; anything else just answers.</summary>
public sealed class ProposeModel : IChatModel
{
    public IReadOnlyList<ChatTool> Offered { get; private set; } = [];
    public List<string> ToolResults { get; } = [];

    public Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct)
    {
        Offered = tools;
        if (messages[^1].Role == "tool")
        {
            ToolResults.Clear();
            ToolResults.AddRange(messages.SkipWhile(m => m.ToolCalls is null).Where(m => m.Role == "tool").Select(m => m.Content!));
            return Task.FromResult(new ChatMessage("assistant", "Listo."));
        }
        var question = messages[^1].Content!;
        if (!question.StartsWith("propose_", StringComparison.Ordinal)) return Task.FromResult(new ChatMessage("assistant", "Toque Confirmar en la tarjeta."));
        var calls = question.Split(" ;; ").Select((part, i) => new ChatToolCall($"p{i}", part.Split(' ', 2)[0], part.Split(' ', 2) is [_, var args] ? args : "{}")).ToList();
        return Task.FromResult(new ChatMessage("assistant", null, calls));
    }
}

/// <summary>An Owner-only action for the tests (as TK-341's will be): counts how often it ran.</summary>
public sealed class OwnerNoteAction : AssistantActionType
{
    private int _runs;
    public int Runs => _runs;

    public override string Type => "owner_note";
    public override string Description => "Nota de prueba.";
    public override JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["note"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 } },
        ["required"] = new JsonArray("note"),
        ["additionalProperties"] = false,
    };
    public override IReadOnlyList<string> Policies { get; } = [CloudAuth.PanelPolicy, CloudAuth.OwnerPolicy];

    public override Task<Result<(JsonObject Arguments, string Summary)>> PrepareAsync(ActionContext ctx, JsonObject args, CancellationToken ct) =>
        Task.FromResult<Result<(JsonObject, string)>>((args, $"Nota: {args["note"]}"));

    public override Task<Result<object?>> ExecuteAsync(ActionContext ctx, JsonObject args, CancellationToken ct)
    {
        Interlocked.Increment(ref _runs);
        return Task.FromResult<Result<object?>>(new { saved = args["note"]!.GetValue<string>() });
    }
}

public sealed class ActionsApp() : CloudApp(new ProposeModel())
{
    public ProposeModel Model => (ProposeModel)Services.GetService(typeof(IChatModel))!;
    public OwnerNoteAction Note { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Billing:Plans:NoActions:Features:0", "ButcherModule"); // a plan without AssistantActions
        builder.ConfigureTestServices(s => s.AddSingleton<AssistantActionType>(Note));
    }
}

/// <summary>TK-338: the model proposes, the server summarizes from real data, only the user's click (same user, same tenant, in time) runs it, once.</summary>
public sealed class TaliaActionsTests(ActionsApp app) : IClassFixture<ActionsApp>
{
    private const string TurnOffRejections = """propose_alert_preference {"alert":"DgiiRejected","on":false}""";
    private const string Note = """propose_owner_note {"note":"hola"}""";

    private static async Task<JsonElement> AskAsync(Owner who, string question) =>
        await (await who.Client.PostAsJsonAsync("/api/assistant/ask", new { question })).EnsureSuccessStatusCode().Json();

    private static async Task<Guid> ProposeAsync(Owner who, string question) =>
        (await AskAsync(who, question)).GetProperty("proposal").GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> ConfirmAsync(Owner who, Guid id) => who.Client.PostAsync($"/api/assistant/actions/{id}/confirm", null);

    private Task<AssistantAction?> RowAsync(Guid id) => app.InDbAsync(db => db.AssistantActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id));

    private async Task<Owner> MemberAsync(Owner owner, string role)
    {
        var email = $"member{Guid.NewGuid():N}@example.com";
        var link = (await (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role })).Json()).GetProperty("link").GetString()!;
        (await app.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = link[(link.IndexOf('#') + 1)..], password = CloudApp.Password })).EnsureSuccessStatusCode();
        return await app.SignInAsync(email);
    }

    [Fact]
    public async Task Proposal_then_confirm_does_it_and_the_model_only_hears_that_a_card_was_shown()
    {
        var owner = await app.OwnerAsync();

        var json = await AskAsync(owner, TurnOffRejections);

        json.GetProperty("answer").GetString().ShouldBe("Listo.");
        var proposal = json.GetProperty("proposal");
        proposal.GetProperty("summary").GetString().ShouldBe("Aviso por correo «Comprobantes rechazados por la DGII»: encendido → apagado");
        proposal.GetProperty("expiresAt").GetDateTime().ShouldBe(app.Clock.GetUtcNow().UtcDateTime.AddMinutes(10));
        var id = proposal.GetProperty("id").GetGuid();
        app.Model.Offered.ShouldContain(t => t.Name == "propose_alert_preference");
        app.Model.ToolResults.Single().ShouldContain("Tarjeta de confirmación");
        app.Model.ToolResults.Single().ShouldNotContain(id.ToString());
        (await RowAsync(id))!.Status.ShouldBe(AssistantActionStatus.Proposed);
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("dgiiRejected").GetBoolean().ShouldBeTrue(); // nothing yet

        var r = await ConfirmAsync(owner, id);

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var done = await r.Json();
        done.GetProperty("status").GetString().ShouldBe("Done");
        done.GetProperty("result").GetProperty("on").GetBoolean().ShouldBeFalse();
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("dgiiRejected").GetBoolean().ShouldBeFalse();
        var row = (await RowAsync(id))!;
        (row.Type, row.Arguments, row.DoneUtc).ShouldBe(("alert_preference", """{"alert":"DgiiRejected","on":false}""", app.Clock.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public async Task Confirming_twice_runs_it_once_and_returns_the_stored_result()
    {
        var owner = await app.OwnerAsync();
        var id = await ProposeAsync(owner, Note);
        var before = app.Note.Runs;

        var first = await (await ConfirmAsync(owner, id)).Json();
        var second = await ConfirmAsync(owner, id);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Json()).GetRawText().ShouldBe(first.GetRawText());
        first.GetProperty("result").GetProperty("saved").GetString().ShouldBe("hola");
        app.Note.Runs.ShouldBe(before + 1);
        (await owner.Client.PostAsync($"/api/assistant/actions/{id}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Another_user_or_tenant_gets_404_and_runs_nothing()
    {
        var owner = await app.OwnerAsync();
        var id = await ProposeAsync(owner, Note);
        var colleague = await MemberAsync(owner, "Owner");
        var stranger = await app.OwnerAsync();
        var before = app.Note.Runs;

        foreach (var other in new[] { colleague, stranger })
        {
            (await ConfirmAsync(other, id)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await other.Client.PostAsync($"/api/assistant/actions/{id}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        (await ConfirmAsync(owner, Guid.NewGuid())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        app.Note.Runs.ShouldBe(before);
        (await RowAsync(id))!.Status.ShouldBe(AssistantActionStatus.Proposed);
    }

    [Fact]
    public async Task An_expired_proposal_does_not_run()
    {
        var owner = await app.OwnerAsync();
        var id = await ProposeAsync(owner, Note);
        var before = app.Note.Runs;

        app.Clock.Advance(TimeSpan.FromMinutes(10));
        var r = await ConfirmAsync(owner, id);

        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await r.ErrorCode()).ShouldBe("Assistant.ProposalExpired");
        (await RowAsync(id))!.Status.ShouldBe(AssistantActionStatus.Expired);
        (await ConfirmAsync(owner, id)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        app.Note.Runs.ShouldBe(before);
    }

    [Fact]
    public async Task A_permission_revoked_between_propose_and_confirm_is_refused()
    {
        var owner = await app.OwnerAsync();
        var member = await MemberAsync(owner, "Owner");
        var id = await ProposeAsync(member, Note);
        var memberId = await app.InDbAsync(db => db.Users.Where(u => u.Email == member.Email).Select(u => u.Id).SingleAsync());
        (await owner.Client.PutAsJsonAsync($"/api/members/{memberId}/role", new { role = "ReadOnly" })).EnsureSuccessStatusCode();
        var before = app.Note.Runs;

        var r = await ConfirmAsync(member, id);

        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await r.ErrorCode()).ShouldBe("Assistant.NotAllowed");
        app.Note.Runs.ShouldBe(before);
        (await RowAsync(id))!.Status.ShouldBe(AssistantActionStatus.Failed);
        // Now a ReadOnly member: the Owner-only action is no longer offered (the alert one is: any member chooses their own).
        await AskAsync(member, "¿Qué puedo hacer?");
        app.Model.Offered.ShouldNotContain(t => t.Name == "propose_owner_note");
        app.Model.Offered.ShouldContain(t => t.Name == "propose_alert_preference");
    }

    [Theory]
    [InlineData("""propose_delete_everything {}""", "No existe la herramienta")]
    [InlineData("""propose_alert_preference {"alert":"All","on":false}""", "no es válido")]
    [InlineData("""propose_alert_preference {"alert":"DgiiRejected","on":false,"userId":"x"}""", "no tiene el argumento userId")]
    [InlineData("""propose_alert_preference {"alert":"DgiiRejected","on":true}""", "ya está encendido")] // the server read the real state
    public async Task An_unknown_action_or_forged_arguments_leave_no_proposal(string question, string error)
    {
        var owner = await app.OwnerAsync();

        var json = await AskAsync(owner, question);

        json.GetProperty("proposal").ValueKind.ShouldBe(JsonValueKind.Null);
        JsonDocument.Parse(app.Model.ToolResults.Single()).RootElement.GetProperty("error").GetString()!.ShouldContain(error);
        (await app.InDbAsync(db => db.AssistantActions.CountAsync(a => a.TenantId == owner.TenantId))).ShouldBe(0);
    }

    [Fact]
    public async Task A_stored_proposal_of_an_action_that_no_longer_exists_does_not_run()
    {
        var owner = await app.OwnerAsync();
        var id = await ProposeAsync(owner, Note);
        await app.InDbAsync(db => db.AssistantActions.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Type, "grant_everything")));

        var r = await ConfirmAsync(owner, id);

        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await r.ErrorCode()).ShouldBe("Assistant.UnknownAction");
    }

    [Fact]
    public async Task Only_one_proposal_per_answer()
    {
        var owner = await app.OwnerAsync();

        var json = await AskAsync(owner, $"{Note} ;; {TurnOffRejections}");

        json.GetProperty("proposal").GetProperty("summary").GetString().ShouldBe("Nota: hola");
        app.Model.ToolResults[1].ShouldContain("solo cabe una");
        (await app.InDbAsync(db => db.AssistantActions.CountAsync(a => a.TenantId == owner.TenantId))).ShouldBe(1);
    }

    [Fact]
    public async Task A_yes_in_the_chat_confirms_nothing_and_cancel_closes_it()
    {
        var owner = await app.OwnerAsync();
        var id = await ProposeAsync(owner, Note);
        var before = app.Note.Runs;

        var json = await (await owner.Client.PostAsJsonAsync("/api/assistant/ask", new
        {
            question = "Sí, hazlo",
            history = new[] { new { role = "user", content = Note }, new { role = "assistant", content = "Listo." } },
        })).Json();

        json.GetProperty("proposal").ValueKind.ShouldBe(JsonValueKind.Null);
        app.Model.Offered.ShouldNotContain(t => t.Name.Contains("confirm", StringComparison.OrdinalIgnoreCase));
        (await RowAsync(id))!.Status.ShouldBe(AssistantActionStatus.Proposed);

        var cancel = await owner.Client.PostAsync($"/api/assistant/actions/{id}/cancel", null);
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cancel.Json()).GetProperty("status").GetString().ShouldBe("Cancelled");
        (await owner.Client.PostAsync($"/api/assistant/actions/{id}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var confirm = await ConfirmAsync(owner, id);
        confirm.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await confirm.ErrorCode()).ShouldBe("Assistant.ProposalCancelled");
        app.Note.Runs.ShouldBe(before);
    }

    [Fact]
    public async Task Without_the_AssistantActions_function_nothing_is_offered_or_confirmed()
    {
        var owner = await app.OwnerAsync();
        var id = await ProposeAsync(owner, Note);
        await app.InDbAsync(db => db.Tenants.Where(t => t.Id == owner.TenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Plan, "NoActions")));
        app.Services.GetRequiredService<TenantPlans>().Forget(owner.TenantId);

        var json = await AskAsync(owner, Note);

        json.GetProperty("proposal").ValueKind.ShouldBe(JsonValueKind.Null);
        app.Model.Offered.ShouldNotContain(t => t.Name.StartsWith("propose_", StringComparison.Ordinal));
        var r = await ConfirmAsync(owner, id);
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await r.ErrorCode()).ShouldBe("Assistant.ActionsOff");
        (await RowAsync(id))!.Status.ShouldBe(AssistantActionStatus.Proposed);
    }
}
