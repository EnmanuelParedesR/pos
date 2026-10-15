using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Toki.IntegrationTests.Infra;

/// <summary>The cloud side of ADR-0008, in memory: the primary handler of the app's cloud HttpClient. Records every request
/// (gzip bodies already inflated) and answers as the real cloud would, unless a test sets <see cref="Respond"/>.</summary>
public sealed class FakeCloud : HttpMessageHandler
{
    public const string Url = "https://cloud.test";
    public const string ValidCode = "12345678";
    public const string NodeSecret = "node-secret-for-tests-0123456789";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid StoreId { get; } = Guid.NewGuid();
    public Guid NodeId { get; } = Guid.NewGuid();

    public List<Received> Requests { get; } = [];

    /// <summary>Takes over the answer for a path when it returns a response (or throws, like a network failure).</summary>
    public Func<string, HttpResponseMessage?>? Respond { get; set; }

    /// <summary>Which received event ids the ingest answers as accepted. Default: all.</summary>
    public Func<Guid, bool> Accept { get; set; } = _ => true;

    /// <summary>What the e-CF relay answers.</summary>
    public object RelayAnswer { get; set; } = new { status = "Simulated", trackId = (string?)null, message = (string?)null };

    public IEnumerable<Received> To(string path)
    {
        lock (Requests) return Requests.Where(r => r.Path == path).ToList();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var gzip = request.Content?.Headers.ContentEncoding.Contains("gzip") == true;
        if (gzip)
        {
            using var inflated = new MemoryStream();
            using (var z = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)) await z.CopyToAsync(inflated, cancellationToken);
            bytes = inflated.ToArray();
        }
        var body = bytes.Length == 0 ? default : JsonDocument.Parse(bytes).RootElement.Clone();
        var path = request.RequestUri!.AbsolutePath;
        lock (Requests) Requests.Add(new Received(request.RequestUri.GetLeftPart(UriPartial.Authority), path, request.Headers.Authorization?.ToString(), gzip, body));

        if (Respond?.Invoke(path) is { } custom) return custom;
        return path switch
        {
            "/api/nodes/register" when body.GetProperty("code").GetString() == ValidCode =>
                Json(new { tenantId = TenantId, storeId = StoreId, nodeId = NodeId, nodeSecret = NodeSecret }),
            "/ingest/v1/events" =>
                Json(new { accepted = body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Where(Accept).ToList() }),
            "/relay/v1/ecf" => Json(RelayAnswer),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    public sealed record Received(string Origin, string Path, string? Authorization, bool Gzip, JsonElement Body);
}
