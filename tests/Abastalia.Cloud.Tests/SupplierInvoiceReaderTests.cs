using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Shouldly;
using Abastalia.Host.Cloud.Assistant;

namespace Abastalia.Cloud.Tests;

/// <summary>Answers every invoice with the same reading, as the model would through the tool. No real AI call.</summary>
public sealed class FakeInvoiceModel : IChatModel
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public string Arguments { get; set; } = """
        {"supplierRnc":"101234567","supplierName":"Granja Los Pinos","ncf":"E310000000123","date":"2026-10-14",
         "lines":[{"description":"Pierna de cerdo","quantity":"50","unitCost":95.5,"itbisPercent":0},{"quantity":1}],
         "subtotal":4775,"itbis":0,"total":4775,"confidence":1.7,"doubtful":["lines"]}
        """;

    public Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct)
    {
        lock (Calls) Calls.Add(messages);
        return Task.FromResult(new ChatMessage("assistant", null, [new ChatToolCall("call_1", SupplierInvoiceReader.ToolName, Arguments)]));
    }
}

public sealed class InvoiceReaderApp() : CloudApp(new FakeInvoiceModel())
{
    public const int PerDay = 3;

    public FakeInvoiceModel Model => (FakeInvoiceModel)Services.GetService(typeof(IChatModel))!;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ai:SupplierInvoicesPerDay", PerDay.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

public sealed class SupplierInvoiceReaderTests(InvoiceReaderApp app) : IClassFixture<InvoiceReaderApp>
{
    private static object Photo(string type = "image/jpeg") => new { file = Convert.ToBase64String([0xFF, 0xD8, 0xFF, 1, 2, 3]), contentType = type, fileName = "factura.jpg" };

    [Fact]
    public async Task A_node_gets_the_invoice_fields_read_from_the_photo()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());

        var r = await node.Client.PostAsJsonAsync("/ai/v1/supplier-invoice", Photo());

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var read = await r.Json();
        read.GetProperty("supplierRnc").GetString().ShouldBe("101234567");
        read.GetProperty("ncf").GetString().ShouldBe("E310000000123");
        read.GetProperty("date").GetString().ShouldBe("2026-10-14");
        var line = read.GetProperty("lines").EnumerateArray().ShouldHaveSingleItem(); // a line without a description is dropped
        line.GetProperty("quantity").GetDecimal().ShouldBe(50m);
        line.GetProperty("unitCost").GetDecimal().ShouldBe(95.5m);
        read.GetProperty("confidence").GetDecimal().ShouldBe(1m);
        read.GetProperty("doubtful").EnumerateArray().Select(d => d.GetString()).ShouldBe(["lines"]);

        var user = app.Model.Calls[^1].Single(m => m.Role == "user");
        var file = user.Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
        file.ContentType.ShouldBe("image/jpeg");
        file.Data.ShouldBe(new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 });
    }

    [Fact]
    public async Task Only_nodes_send_invoices_and_only_PDFs_or_photos()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);

        (await owner.Client.PostAsJsonAsync("/ai/v1/supplier-invoice", Photo())).StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await app.CreateClient().PostAsJsonAsync("/ai/v1/supplier-invoice", Photo())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await (await node.Client.PostAsJsonAsync("/ai/v1/supplier-invoice", Photo("text/html"))).ErrorCode()).ShouldBe("Ai.InvalidFile");
    }

    [Fact]
    public async Task R9_14_a_tenant_reads_a_limited_number_of_invoices_a_day_and_none_with_a_restricted_license()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var second = await app.StoreWithNodeAsync(owner, "Sucursal"); // the limit is the business's, not the PC's
        for (var i = 0; i < InvoiceReaderApp.PerDay; i++)
            (await (i % 2 == 0 ? node : second).Client.PostAsJsonAsync("/ai/v1/supplier-invoice", Photo())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var calls = app.Model.Calls.Count;
        var over = await second.Client.PostAsJsonAsync("/ai/v1/supplier-invoice", Photo());
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await over.ErrorCode()).ShouldBe("Ai.DailyLimit");
        app.Model.Calls.Count.ShouldBe(calls); // the model was not called

        var restricted = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var now = app.Clock.GetUtcNow().UtcDateTime;
        (await restricted.SendAsync(Events.Envelope(PlatformTests.Heartbeat(license: new Abastalia.Sync.Contracts.NodeLicenseV1("Restricted", now.AddDays(-1))), now)))
            .EnsureSuccessStatusCode();
        await app.ApplyAsync();
        var refused = await restricted.Client.PostAsJsonAsync("/ai/v1/supplier-invoice", Photo());
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ErrorCode()).ShouldBe("Ai.LicenseRestricted");
    }

    [Fact]
    public async Task The_wire_message_carries_the_image_and_the_PDF_as_content_parts()
    {
        var handler = new CaptureHandler();
        var model = new AzureOpenAIChatModel(new HttpClient(handler),
            new AzureOpenAIOptions { Endpoint = "https://ai.test", Deployment = "gpt-5-nano", ApiKey = "test-key" });

        await model.CompleteAsync([new("user", "Esta es la factura.", Attachments:
            [new("image/png", [1, 2], "a.png"), new("application/pdf", [3], "b.pdf")])], [SupplierInvoiceReader.Tool], CancellationToken.None);

        var content = JsonNode.Parse(handler.Body!)!["messages"]![0]!["content"]!.AsArray();
        content[0]!["type"]!.GetValue<string>().ShouldBe("text");
        content[1]!["image_url"]!["url"]!.GetValue<string>().ShouldBe("data:image/png;base64,AQI=");
        content[2]!["file"]!["file_data"]!.GetValue<string>().ShouldBe("data:application/pdf;base64,Aw==");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"{}"}}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}

public sealed class SupplierInvoiceWithoutAiTests(CloudApp app) : IClassFixture<CloudApp>
{
    [Fact]
    public async Task Without_a_model_configured_the_cloud_says_so()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var r = await node.Client.PostAsJsonAsync("/ai/v1/supplier-invoice", new { file = "AQID", contentType = "application/pdf", fileName = "f.pdf" });
        r.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await r.ErrorCode()).ShouldBe("Ai.NotConfigured");
    }
}
