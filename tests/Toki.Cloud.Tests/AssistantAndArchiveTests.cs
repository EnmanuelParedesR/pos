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
    public async Task After_90_days_the_xml_moves_to_cold_storage_and_the_owner_still_gets_it_back_by_its_eNCF()
    {
        await using var own = new CloudApp(); // moves its own clock
        var owner = await own.OwnerAsync();
        var node = await own.StoreWithNodeAsync(owner);
        var body = new
        {
            encf = "E320000000777", environment = "eCF", status = "Accepted", signedUtc = DateTime.UtcNow, signedXmlGzip = Gzip("<ECF>firmado</ECF>"),
            rfceGzip = (string?)Gzip("<RFCE>firmado</RFCE>"), replacedByENcf = (string?)null,
        };
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { encf = "E310000000001", rfceGzip = (string?)null })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Move(own)).ShouldBe(0); // still hot
        var hot = await owner.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/archive/E320000000777");
        (hot.GetProperty("cold").GetBoolean(), hot.GetProperty("signedXml").GetString()).ShouldBe((false, "<ECF>firmado</ECF>"));

        own.Clock.Advance(TimeSpan.FromDays(91));
        (await Move(own)).ShouldBe(2);
        var row = await own.InDbAsync(db => db.EcfRelays.SingleAsync(r => r.ENcf == "E320000000777"));
        (row.SignedXmlGzip.Length, row.RfceGzip, row.ColdHasRfce, row.ColdKey).ShouldBe((0, null, true, $"{owner.TenantId:N}/eCF/E320000000777"));
        File.Exists(Path.Combine(own.ArchiveFolder, $"{owner.TenantId:N}", "eCF", "E320000000777.rfce.xml.gz")).ShouldBeTrue();

        // A later upload (a status change) keeps the cold copy: the XML there never changes.
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { replacedByENcf = (string?)"E320000000778" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Move(own)).ShouldBe(0);

        var owner2 = await own.SignInAsync(owner.Email); // the old token expired with the clock
        var cold = await owner2.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/archive/e320000000777");
        (cold.GetProperty("cold").GetBoolean(), cold.GetProperty("ready").GetBoolean()).ShouldBe((true, true));
        (cold.GetProperty("signedXml").GetString(), cold.GetProperty("rfceXml").GetString(), cold.GetProperty("replacedByENcf").GetString())
            .ShouldBe(("<ECF>firmado</ECF>", "<RFCE>firmado</RFCE>", "E320000000778"));
        (await owner2.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/archive/E310000000001")).GetProperty("rfceXml").ValueKind.ShouldBe(JsonValueKind.Null);

        var stranger = await own.OwnerAsync();
        (await stranger.Client.GetAsync("/api/fiscal/archive/E320000000777")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await own.CreateClient().GetAsync("/api/fiscal/archive/E320000000777")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task R9_4_the_same_eNCF_in_a_test_environment_and_in_production_are_two_documents_hot_and_cold()
    {
        await using var own = new CloudApp(); // moves its own clock
        var owner = await own.OwnerAsync();
        var node = await own.StoreWithNodeAsync(owner);
        var body = new { encf = "E310000000042", environment = "TesteCF", status = "Accepted", signedUtc = DateTime.UtcNow, signedXmlGzip = Gzip("<ECF>prueba</ECF>") };
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { environment = "eCF", signedXmlGzip = Gzip("<ECF>real</ECF>") })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { environment = "CerteCF", signedXmlGzip = Gzip("<ECF>certificacion</ECF>") })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await own.InDbAsync(db => db.EcfRelays.CountAsync(r => r.TenantId == owner.TenantId))).ShouldBe(3);

        async Task<string?> Xml(Owner who, string query = "") =>
            (await who.Client.GetFromJsonAsync<JsonElement>($"/api/fiscal/archive/E310000000042{query}")).GetProperty("signedXml").GetString();
        (await Xml(owner)).ShouldBe("<ECF>real</ECF>"); // production first
        (await Xml(owner, "?environment=TesteCF")).ShouldBe("<ECF>prueba</ECF>");

        own.Clock.Advance(TimeSpan.FromDays(91));
        (await Move(own)).ShouldBe(3);
        File.Exists(Path.Combine(own.ArchiveFolder, $"{owner.TenantId:N}", "eCF", "E310000000042.xml.gz")).ShouldBeTrue();
        File.Exists(Path.Combine(own.ArchiveFolder, $"{owner.TenantId:N}", "TesteCF", "E310000000042.xml.gz")).ShouldBeTrue();
        var again = await own.SignInAsync(owner.Email);
        (await Xml(again)).ShouldBe("<ECF>real</ECF>");
        (await Xml(again, "?environment=certecf")).ShouldBe("<ECF>certificacion</ECF>");
    }

    private static async Task<int> Move(CloudApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Toki.Host.Cloud.Relay.ColdArchiver>().MoveAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_archive_needs_node_credentials_and_a_gzipped_document()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var body = new { encf = "E310000000001", environment = "eCF", status = "Accepted", signedXmlGzip = Gzip("<ECF/>") };

        (await app.CreateClient().PostAsJsonAsync("/fiscal/v1/archive", body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { environment = "Simulated" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var notGzip = await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { signedXmlGzip = Convert.ToBase64String("<ECF/>"u8.ToArray()) });
        notGzip.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await notGzip.ErrorCode()).ShouldBe("Archive.InvalidDocument");
        (await node.Client.PostAsJsonAsync("/fiscal/v1/archive", body with { encf = "B0100000001" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
