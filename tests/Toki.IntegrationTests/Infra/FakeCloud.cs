using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>TK-241: answer the register with a secret, as a cloud from before (the store then keeps the secret). Default: no secret, the key.</summary>
    public bool IssueSecret { get; set; }

    /// <summary>The public key the last register sent.</summary>
    public byte[]? NodePublicKey { get; private set; }

    /// <summary>Takes over the answer for a path when it returns a response (or throws, like a network failure).</summary>
    public Func<string, HttpResponseMessage?>? Respond { get; set; }

    /// <summary>Which received event ids the ingest answers as accepted. Default: all.</summary>
    public Func<Guid, bool> Accept { get; set; } = _ => true;


    public IEnumerable<Received> To(string path)
    {
        lock (Requests) return Requests.Where(r => r.Path == path).ToList();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var raw = bytes;
        var gzip = request.Content?.Headers.ContentEncoding.Contains("gzip") == true;
        if (gzip)
        {
            using var inflated = new MemoryStream();
            using (var z = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)) await z.CopyToAsync(inflated, cancellationToken);
            bytes = inflated.ToArray();
        }
        var body = bytes.Length == 0 ? default : JsonDocument.Parse(bytes).RootElement.Clone();
        var path = request.RequestUri!.AbsolutePath;
        lock (Requests) Requests.Add(new Received(request.RequestUri.GetLeftPart(UriPartial.Authority), path, request.Headers.Authorization?.ToString(), gzip, body, raw));

        if (Respond?.Invoke(path) is { } custom) return custom;
        return path switch
        {
            "/api/nodes/register" when body.GetProperty("code").GetString() == ValidCode && Register(body) =>
                Json(new { tenantId = TenantId, storeId = StoreId, nodeId = NodeId, nodeSecret = IssueSecret ? NodeSecret : null }),
            "/api/nodes/revoke" => new HttpResponseMessage(HttpStatusCode.NoContent),
            "/api/nodes/rekey" when Register(body) => new HttpResponseMessage(HttpStatusCode.NoContent),
            "/ingest/v1/events" =>
                Json(new { accepted = body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Where(Accept).ToList() }),
            "/fiscal/v1/archive" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private bool Register(JsonElement body)
    {
        NodePublicKey = body.TryGetProperty("publicKey", out var k) && k.GetString() is { } key ? Convert.FromBase64String(key) : null;
        return true;
    }

    /// <summary>TK-241: the request carries <c>Node nodeId.seconds.nonce.signature</c>, signed with the key the register sent (the cloud's own check).</summary>
    public bool SignedByNode(Received r)
    {
        if (r.Authorization?.Split(' ', '.') is not ["Node", var node, var seconds, var nonce, var signature] || node != NodeId.ToString() || NodePublicKey is null) return false;
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(NodePublicKey, out _);
        signature = signature.Replace('-', '+').Replace('_', '/');
        var bytes = Convert.FromBase64String(signature.PadRight(signature.Length + (4 - signature.Length % 4) % 4, '='));
        var bodyHash = Convert.ToBase64String(SHA256.HashData(r.Raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return key.VerifyData(Encoding.UTF8.GetBytes($"{node}.{seconds}.{nonce}.POST {r.Path} {bodyHash}"), bytes, HashAlgorithmName.SHA256);
    }

    /// <param name="Raw">The body as sent (before inflating), which the node signature covers (R10 #7).</param>
    public sealed record Received(string Origin, string Path, string? Authorization, bool Gzip, JsonElement Body, byte[] Raw);
}
