using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Sync;

namespace Abastalia.IntegrationTests.Sync;

/// <summary>TK-343: the store's executor. The cloud returns a typed proposal; the store writes the card from its own data and runs it once, through
/// the action's own endpoint, with the person's session, when they click Confirmar. Own app: it connects to the cloud.</summary>
public sealed class TaliaProposalsTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static HttpResponseMessage Proposes(string name) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { answer = "Le dejé la tarjeta.", proposal = new { type = "create_category", arguments = new { name } } }),
    };

    /// <summary>Asks Talia (the fake cloud proposes a category) and returns the card the store built.</summary>
    private async Task<JsonElement> AskAsync(HttpClient client, string name)
    {
        if (!await app.InScopeAsync(sp => sp.GetRequiredService<SyncDbContext>().Connections.AnyAsync())) await app.ConnectAsync();
        app.Cloud.Respond = path => path == AskTalia.CloudPath ? Proposes(name) : null;
        var r = await client.PostAsJsonAsync("/api/assistant/ask", new { question = $"Crea la categoría {name}" });
        app.Cloud.Respond = null;
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return await r.Json();
    }

    /// <summary>What the card's Confirmar sends: its own request, the proposal's id in the header.</summary>
    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, JsonElement card, string? body = null, string? path = null) =>
        client.SendAsync(new HttpRequestMessage(new HttpMethod(card.GetProperty("method").GetString()!), path ?? card.GetProperty("path").GetString())
        {
            Content = new StringContent(body ?? card.GetProperty("body").GetRawText(), Encoding.UTF8, "application/json"),
            Headers = { { AllowAssistantExtensions.Header, card.GetProperty("id").GetString() } },
        });

    private Task<int> CategoriesAsync(string name) =>
        app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.Inventory.Persistence.InventoryDbContext>().Categories.CountAsync(c => c.Name == name));

    private Task<string> StatusAsync(Guid id) =>
        app.InScopeAsync(async sp => (await sp.GetRequiredService<SyncDbContext>().TaliaProposals.SingleAsync(p => p.Id == id)).Status.ToString());

    [Fact]
    public async Task Proposal_preview_confirm_runs_once_through_the_endpoint_and_the_audit_says_via_talia()
    {
        var admin = app.Client(await app.AdminAsync());

        var json = await AskAsync(admin, "  Bebidas   frías ");

        // The store declared what it runs; the cloud's proposal became the store's own card.
        app.Cloud.To(AskTalia.CloudPath).Last().Body.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ShouldBe(["create_category"]);
        json.GetProperty("answer").GetString().ShouldBe("Le dejé la tarjeta.");
        var card = json.GetProperty("proposal");
        card.GetProperty("summary").GetString().ShouldBe("Crear la categoría «Bebidas frías»");
        (card.GetProperty("method").GetString(), card.GetProperty("path").GetString()).ShouldBe(("POST", "/api/inventory/categories"));
        (await CategoriesAsync("Bebidas frías")).ShouldBe(0); // nothing changes until the click

        var first = await ConfirmAsync(admin, card);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await first.Json()).GetProperty("id").GetGuid();
        (await CategoriesAsync("Bebidas frías")).ShouldBe(1);
        var id = card.GetProperty("id").GetGuid();
        (await StatusAsync(id)).ShouldBe("Done");

        // A second click (or a retry) gets the same answer and runs nothing.
        var second = await ConfirmAsync(admin, card);
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await second.Json()).GetProperty("id").GetGuid().ShouldBe(created);
        (await CategoriesAsync("Bebidas frías")).ShouldBe(1);

        var audit = await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.AsNoTracking()
            .Where(a => a.Action == "inventory.save-category" && a.ResourceId == created).ToListAsync());
        var data = JsonDocument.Parse(audit.Single().Data).RootElement;
        (data.GetProperty("via").GetString(), data.GetProperty("proposal").GetGuid(), data.GetProperty("name").GetString())
            .ShouldBe(("talia", id, "Bebidas frías"));
        (await app.VerifyAuditAsync()).ShouldBeNull();

        // The same name again: the store refuses to build the card (its data), nothing to confirm.
        var again = await AskAsync(admin, "bebidas FRÍAS");
        again.GetProperty("proposal").ValueKind.ShouldBe(JsonValueKind.Null);
        again.GetProperty("proposalError").GetString().ShouldBe("Ya existe la categoría «Bebidas frías».");
    }

    [Fact]
    public async Task Only_its_owner_runs_it_exactly_as_proposed_and_never_after_cancel_or_expiry()
    {
        var admin = app.Client(await app.AdminAsync());
        var card = (await AskAsync(admin, "Lácteos")).GetProperty("proposal");

        // Another user with the same permission: the same 404 as an id that does not exist.
        var supervisor = app.Client(await app.UserAsync(Roles.Supervisor));
        var other = await ConfirmAsync(supervisor, card);
        (other.StatusCode, await other.ErrorCode()).ShouldBe((HttpStatusCode.NotFound, "Talia.ProposalNotFound"));
        // Not what the card said: another name, or the card's id on another endpoint.
        var changed = await ConfirmAsync(admin, card, body: """{"name":"Licores","color":"#6B1F5E","notes":[],"isActive":true}""");
        (changed.StatusCode, await changed.ErrorCode()).ShouldBe((HttpStatusCode.Forbidden, "Talia.ProposalMismatch"));
        (await CategoriesAsync("Licores")).ShouldBe(0);

        (await admin.PostAsync($"/api/assistant/proposals/{card.GetProperty("id").GetString()}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cancelled = await ConfirmAsync(admin, card);
        (cancelled.StatusCode, await cancelled.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "Talia.ProposalCancelled"));
        (await CategoriesAsync("Lácteos")).ShouldBe(0);

        var late = (await AskAsync(admin, "Panadería")).GetProperty("proposal");
        app.Clock.Advance(TaliaProposals.Lifetime + TimeSpan.FromSeconds(1));
        var expired = await ConfirmAsync(admin, late);
        (expired.StatusCode, await expired.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "Talia.ProposalExpired"));
        (await CategoriesAsync("Panadería")).ShouldBe(0);
    }

    [Fact]
    public async Task An_endpoint_not_marked_AllowAssistant_refuses_any_proposal()
    {
        var admin = app.Client(await app.AdminAsync());
        var card = (await AskAsync(admin, "Carnes")).GetProperty("proposal");

        // PUT categories (rename) is a real endpoint the admin may call, but Talia may not drive it.
        var r = await ConfirmAsync(admin, card, path: $"/api/inventory/categories/{Guid.NewGuid()}");

        (r.StatusCode, await r.ErrorCode()).ShouldBe((HttpStatusCode.Forbidden, "Talia.EndpointNotAllowed"));
        (await StatusAsync(card.GetProperty("id").GetGuid())).ShouldBe("Proposed");
    }

    [Fact]
    public async Task Without_the_permission_the_store_builds_no_card_and_the_endpoint_refuses_at_confirm()
    {
        var accountant = await app.UserAsync(Roles.Accountant); // Administración, but no products.manage
        var client = app.Client(accountant);

        // A cloud that proposed anyway (old, or wrong): the store checks the permission itself.
        var json = await AskAsync(client, "Bebidas");
        json.GetProperty("proposal").ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("proposalError").GetString()!.ShouldContain("no tiene permiso");

        // A proposal made while the role still had it: the endpoint's own permission decides at the click, and it stays unrun.
        var id = Guid.CreateVersion7();
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<SyncDbContext>();
            var now = app.Clock.GetUtcNow().UtcDateTime;
            db.TaliaProposals.Add(new TaliaProposal
            {
                Id = id, UserId = accountant.UserId, Type = "create_category", Summary = "Crear la categoría «Bebidas»", Method = "POST",
                Path = "/api/inventory/categories", Body = """{"name":"Bebidas","color":"#6B1F5E","notes":[],"isActive":true}""",
                Status = TaliaProposalStatus.Proposed, CreatedUtc = now, ExpiresUtc = now + TaliaProposals.Lifetime,
            });
            return await db.SaveChangesAsync();
        });
        var card = JsonDocument.Parse($$$"""{"id":"{{{id}}}","method":"POST","path":"/api/inventory/categories","body":{"name":"Bebidas","color":"#6B1F5E","notes":[],"isActive":true}}""").RootElement;

        (await ConfirmAsync(client, card)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await StatusAsync(id)).ShouldBe("Proposed");
        (await CategoriesAsync("Bebidas")).ShouldBe(0);
    }

    [Fact]
    public async Task Approval_required_keeps_the_proposal_open_and_the_retry_with_the_approval_runs_it_once()
    {
        var admin = await app.AdminAsync();
        var id = Guid.CreateVersion7();
        const string body = """{"amount":10}""";
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<SyncDbContext>();
            var now = app.Clock.GetUtcNow().UtcDateTime;
            db.TaliaProposals.Add(new TaliaProposal
            {
                Id = id, UserId = admin.UserId, Type = "test_action", Summary = "Prueba", Method = "POST", Path = "/api/test", Body = body,
                Status = TaliaProposalStatus.Proposed, CreatedUtc = now, ExpiresUtc = now + TaliaProposals.Lifetime,
            });
            return await db.SaveChangesAsync();
        });
        var filter = AllowAssistantExtensions.Filter(["test_action"]);
        var runs = 0;

        // The endpoint, as the approval decorator answers it: a supervisor first, then (with the approval in the body) done.
        async Task<object?> ConfirmAsync(string json, bool approved) => await app.InScopeAsync(async sp =>
        {
            var http = new DefaultHttpContext { RequestServices = sp };
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AbastaliaClaims.User, admin.UserId.ToString())], "test"));
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            (http.Request.Method, http.Request.Path) = ("POST", "/api/test");
            var bytes = Encoding.UTF8.GetBytes(json);
            (http.Request.Body, http.Request.ContentLength) = (new MemoryStream(bytes), bytes.Length);
            http.Request.Headers[AllowAssistantExtensions.Header] = id.ToString();
            return await filter(new DefaultEndpointFilterInvocationContext(http), _ =>
            {
                runs++;
                return ValueTask.FromResult<object?>(approved
                    ? Results.Created("/api/test/1", new { ok = true })
                    : EndpointExtensions.Problem(ApprovalErrors.Required(Permissions.SalesVoid, "Prueba")));
            });
        });

        var first = await ConfirmAsync(body, approved: false);
        ((IStatusCodeHttpResult)first!).StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        (await StatusAsync(id)).ShouldBe("Proposed"); // the card asks a supervisor and sends it again

        var approval = Guid.NewGuid();
        var second = await ConfirmAsync($$"""{"amount":10,"supervisorApprovalId":"{{approval}}"}""", approved: true);
        ((IStatusCodeHttpResult)second!).StatusCode.ShouldBe(StatusCodes.Status201Created);
        (await StatusAsync(id)).ShouldBe("Done");

        // Once done, a third click answers from the table without running the endpoint.
        await ConfirmAsync($$"""{"amount":10,"supervisorApprovalId":"{{approval}}"}""", approved: true);
        runs.ShouldBe(2);
    }
}
