using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.IntegrationTests.Infra;
using Toki.IntegrationTests.Sync;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>TK-233: "Solicitar acompañamiento" goes to the cloud through the node's channel; without the cloud the owner is told to connect.</summary>
public sealed class AccompanimentTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task The_request_needs_the_cloud_goes_as_the_node_and_is_audited()
    {
        var admin = app.Client(await app.AdminAsync());
        var ask = new { contactName = " Ana Pérez ", phone = "809-555-1234", preferredTimes = "Mañanas" };

        // Not connected: the form still loads (to say so), sending is refused with what to do.
        var offline = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/accompaniment");
        offline.GetProperty("connected").GetBoolean().ShouldBeFalse();
        var refused = await admin.PostAsJsonAsync("/api/fiscal/accompaniment", ask);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Json()).GetProperty("detail").GetString()!.ShouldContain("Conecte la tienda a la nube");

        await app.ConnectAsync();
        var request = new { id = Guid.NewGuid(), status = "New", contactName = "Ana Pérez", phone = "809-555-1234", preferredTimes = "Mañanas",
            session1Utc = (DateTime?)null, session2Utc = (DateTime?)null, createdUtc = DateTime.UtcNow };
        var asked = false;
        app.Cloud.Respond = path => path switch
        {
            "/fiscal/v1/accompaniment/status" => Json(new { request = asked ? request : null }),
            "/fiscal/v1/accompaniment" => Asked(),
            _ => null,
        };

        HttpResponseMessage Asked()
        {
            asked = true;
            return Json(new { request });
        }

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/accompaniment");
        (before.GetProperty("connected").GetBoolean(), before.GetProperty("request").ValueKind).ShouldBe((true, JsonValueKind.Null));

        var sent = await admin.PostAsJsonAsync("/api/fiscal/accompaniment", ask);
        sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sent.Json()).GetProperty("status").GetString().ShouldBe("New");
        var call = app.Cloud.To("/fiscal/v1/accompaniment").Single();
        app.Cloud.SignedByNode(call).ShouldBeTrue();
        call.Body.GetProperty("contactName").GetString().ShouldBe("Ana Pérez");
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/accompaniment")).GetProperty("request").GetProperty("id").GetGuid().ShouldBe(request.id);

        (await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.AnyAsync(a => a.Action == "fiscal.accompaniment-request")))
            .ShouldBeTrue();

        // A cloud that is down is not "not connected": the owner gets the network message.
        app.Cloud.Respond = _ => throw new HttpRequestException("down");
        (await (await admin.GetAsync("/api/fiscal/accompaniment")).ErrorCode()).ShouldBe("Fiscal.CloudUnreachable");
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
