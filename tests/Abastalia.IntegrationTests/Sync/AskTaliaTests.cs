using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Sync;

/// <summary>TK-342: Administración asks Talia through the store server, which asks the cloud as its node. Own app: it connects to the cloud.</summary>
public sealed class AskTaliaTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static readonly object Question = new
    {
        question = "¿Dónde cambio un precio?",
        history = new[] { new { role = "user", content = new string('a', 5000) }, new { role = "assistant", content = "Hola" } },
    };

    private static HttpResponseMessage Answer() =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { answer = "En Productos.", screen = "/admin/products" }) };

    [Fact]
    public async Task Talia_asks_the_cloud_as_the_node_with_who_is_asking_and_says_she_needs_internet_without_it()
    {
        var admin = app.Client(await app.AdminAsync());

        // Not connected to the cloud yet.
        var offline = await admin.PostAsJsonAsync("/api/assistant/ask", Question);
        var problem = await offline.Json();
        (offline.StatusCode, problem.GetProperty("code").GetString()).ShouldBe((HttpStatusCode.Conflict, "Talia.NotConnected"));
        problem.GetProperty("detail").GetString()!.ShouldStartWith("Talia necesita internet");

        await app.ConnectAsync();
        app.Cloud.Respond = path => path == "/api/nodes/assistant/ask" ? Answer() : null;
        var r = await admin.PostAsJsonAsync("/api/assistant/ask", Question);

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await r.Json();
        (json.GetProperty("answer").GetString(), json.GetProperty("screen").GetString()).ShouldBe(("En Productos.", "/admin/products"));
        var sent = app.Cloud.To("/api/nodes/assistant/ask").Single();
        app.Cloud.SignedByNode(sent).ShouldBeTrue();
        sent.Body.GetProperty("question").GetString().ShouldBe("¿Dónde cambio un precio?");
        var user = sent.Body.GetProperty("user");
        user.GetProperty("role").GetString().ShouldBe("Administrador");
        user.GetProperty("name").GetString().ShouldNotBeNullOrEmpty();
        user.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ShouldContain("settings.manage");
        sent.Body.GetProperty("today").GetString().ShouldBe(Abastalia.SharedKernel.BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd"));
        sent.Body.GetProperty("history")[0].GetProperty("content").GetString()!.Length.ShouldBe(2000);
        user.EnumerateObject().Select(p => p.Name).ShouldBe(["name", "role", "permissions"]); // never the PIN or the session

        // No internet: the same message, nothing else changes.
        app.Cloud.Respond = _ => throw new HttpRequestException("sin internet");
        var down = await admin.PostAsJsonAsync("/api/assistant/ask", Question);
        (down.StatusCode, await down.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "Talia.NeedsInternet"));

        // The cloud's own refusal (the month's questions) reaches the person as the cloud wrote it.
        app.Cloud.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = JsonContent.Create(new { code = "Quota.Exceeded", detail = "Ya se usaron las preguntas a Talia de este mes." }),
        };
        var quota = await admin.PostAsJsonAsync("/api/assistant/ask", Question);
        (await quota.Json()).GetProperty("detail").GetString().ShouldBe("Ya se usaron las preguntas a Talia de este mes.");
        app.Cloud.Respond = null;

        (await admin.PostAsJsonAsync("/api/assistant/ask", new { question = " " })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_who_works_in_Administracion_asks_Talia()
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier)); // sells, with customers.manage and inventory.view: not the back office
        (await cashier.PostAsJsonAsync("/api/assistant/ask", Question)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.Anonymous().PostAsJsonAsync("/api/assistant/ask", Question)).StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        var accountant = app.Client(await app.UserAsync(Roles.Accountant));
        (await accountant.PostAsJsonAsync("/api/assistant/ask", Question)).StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }
}
