using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Host.Cloud.Assistant;

namespace Toki.Cloud.Tests;

/// <summary>Asks for today's sales once, then answers with what the tool returned.</summary>
public sealed class FakeChatModel : IChatModel
{
    public List<List<ChatMessage>> Calls { get; } = [];

    public Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct)
    {
        lock (Calls) Calls.Add([.. messages]);
        var result = messages.LastOrDefault(m => m.Role == "tool");
        return Task.FromResult(result is null
            ? new ChatMessage("assistant", null, [new ChatToolCall("call_1", "sales_by_day", """{"from":"2026-10-15","to":"2026-10-15"}""")])
            : new ChatMessage("assistant", "Hoy vendiste RD$ 750.00."));
    }
}

public sealed class AssistantApp() : CloudApp(new FakeChatModel())
{
    public FakeChatModel Model => (FakeChatModel)Services.GetService(typeof(IChatModel))!;
}

public sealed class AssistantTests(AssistantApp app) : IClassFixture<AssistantApp>
{
    [Fact]
    public async Task The_assistant_answers_with_tools_that_only_see_the_callers_tenant()
    {
        var a = await app.OwnerAsync();
        var b = await app.OwnerAsync();
        var nodeA = await app.StoreWithNodeAsync(a, "Carnicería A");
        var nodeB = await app.StoreWithNodeAsync(b, "Carnicería B");
        var now = app.Clock.GetUtcNow().UtcDateTime;
        await nodeA.SendAsync(Events.Envelope(PanelTests.Sale(750, Guid.CreateVersion7(), Guid.CreateVersion7(), "Chuleta"), now));
        await nodeB.SendAsync(Events.Envelope(PanelTests.Sale(9999, Guid.CreateVersion7(), Guid.CreateVersion7(), "Costilla"), now));
        await app.ApplyAsync();

        var r = await a.Client.PostAsJsonAsync("/api/assistant/ask", new
        {
            question = "¿Cuánto vendí hoy?",
            history = new[] { new { role = "user", content = "Hola" }, new { role = "assistant", content = "¡Hola! ¿En qué te ayudo?" } },
        });

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Json()).GetProperty("answer").GetString().ShouldBe("Hoy vendiste RD$ 750.00.");
        var last = app.Model.Calls[^1];
        last[0].Role.ShouldBe("system");
        last[0].Content!.ShouldContain("Carnicería A");
        last[0].Content!.ShouldNotContain("Carnicería B");
        last.Select(m => m.Role).ShouldBe(["system", "user", "assistant", "user", "assistant", "tool"]);
        var day = JsonDocument.Parse(last[^1].Content!).RootElement.EnumerateArray().ShouldHaveSingleItem();
        day.GetProperty("total").GetDecimal().ShouldBe(750);
        day.GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Tools_refuse_ranges_longer_than_92_days()
    {
        var owner = await app.OwnerAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var assistant = new OwnerAssistant(app.Model, (Toki.Host.Cloud.Panel.PanelQueries)scope.ServiceProvider.GetService(typeof(Toki.Host.Cloud.Panel.PanelQueries))!);

        var result = await assistant.RunToolAsync(owner.TenantId, new ChatToolCall("1", "sales_by_day", """{"from":"2026-01-01","to":"2026-10-15"}"""), CancellationToken.None);

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()!.ShouldContain("92");
    }
}

public sealed class ArchiveAndConfigurationTests(CloudApp app) : IClassFixture<CloudApp>
{
    [Fact]
    public async Task Without_Azure_OpenAI_configuration_the_assistant_says_so()
    {
        var owner = await app.OwnerAsync();
        var r = await owner.Client.PostAsJsonAsync("/api/assistant/ask", new { question = "¿Cuánto vendí hoy?" });
        r.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await r.ErrorCode()).ShouldBe("Assistant.NotConfigured");
    }

    private static string Gzip(string xml)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(System.Text.Encoding.UTF8.GetBytes(xml));
        return Convert.ToBase64String(output.ToArray());
    }

    [Fact]
    public async Task The_archive_keeps_the_signed_document_and_its_DGII_result_and_a_later_upload_updates_it()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var body = new
        {
            encf = "E320000000123", environment = "TesteCF", status = "Rejected", trackId = (string?)null, dgiiCode = 2, messages = "145: RNC inválido",
            signedUtc = DateTime.UtcNow, signedXmlGzip = Gzip("<ECF>firmado</ECF>"), rfceGzip = Gzip("<RFCE>firmado</RFCE>"),
            replacesENcf = (string?)null, replacedByENcf = (string?)null,
        };

        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { replacedByENcf = (string?)"E320000000124" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var saved = await app.InDbAsync(db => db.EcfRelays.SingleAsync(x => x.ENcf == "E320000000123"));
        (saved.StoreId, saved.EcfType, saved.Status, saved.DgiiCode, saved.Environment).ShouldBe(
            (node.StoreId, Toki.Fiscal.Contracts.EcfType.Consumer32, Toki.Host.Cloud.Persistence.EcfRelayStatus.Rejected, 2, "TesteCF"));
        (saved.Message, saved.ReplacedByENcf).ShouldBe(("145: RNC inválido", "E320000000124"));
        saved.RfceGzip.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_archive_needs_node_credentials_and_a_gzipped_document()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var body = new { encf = "E310000000001", status = "Accepted", signedXmlGzip = Gzip("<ECF/>") };

        (await app.CreateClient().PostAsJsonAsync("/fiscal/v1/archive", body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var notGzip = await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { signedXmlGzip = Convert.ToBase64String("<ECF/>"u8.ToArray()) });
        notGzip.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await notGzip.ErrorCode()).ShouldBe("Archive.InvalidDocument");
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { encf = "B0100000001" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
