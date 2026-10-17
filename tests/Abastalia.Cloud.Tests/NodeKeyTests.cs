using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Host.Cloud.Nodes;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-241: nodes sign every call with their own key (ADR-0008); disconnecting or an Owner removing a register revokes the node.</summary>
public sealed class NodeKeyTests(CloudApp app) : IClassFixture<CloudApp>
{
    private const string StatusPath = "/fiscal/v1/receiver/status";

    private async Task<(Guid NodeId, Guid StoreId, ECDsa Key, Owner Owner)> RegisterAsync()
    {
        var owner = await app.OwnerAsync();
        var store = (await (await owner.Client.PostAsJsonAsync("/api/stores", new { name = "Tienda" })).Json()).GetProperty("id").GetGuid();
        var code = (await (await owner.Client.PostAsync($"/api/stores/{store}/connection-codes", null)).Json()).GetProperty("code").GetString();
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var r = await app.CreateClient().PostAsJsonAsync("/api/nodes/register",
            new { code, nodeName = "PC principal", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return ((await r.Json()).GetProperty("nodeId").GetGuid(), store, key, owner);
    }

    private static string Sign(ECDsa key, Guid nodeId, DateTimeOffset at, string path, string? nonce = null, string body = "")
    {
        var seconds = at.ToUnixTimeSeconds();
        nonce ??= NodeSignature.Base64Url(RandomNumberGenerator.GetBytes(16));
        var signature = key.SignData(Encoding.UTF8.GetBytes(NodeSignature.Payload(nodeId, seconds, nonce, "POST", path, NodeSignature.BodyHash(Encoding.UTF8.GetBytes(body)))),
            HashAlgorithmName.SHA256);
        return $"{nodeId}.{seconds}.{nonce}.{NodeSignature.Base64Url(signature)}";
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string credentials, string body = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Node", credentials);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task A_signed_call_works_once_inside_its_window_and_only_for_its_path()
    {
        var (nodeId, _, key, _) = await RegisterAsync();
        var client = app.CreateClient();
        var now = app.Clock.GetUtcNow();

        var signed = Sign(key, nodeId, now, StatusPath);
        (await PostAsync(client, StatusPath, signed)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client, StatusPath, signed)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // replay
        (await PostAsync(client, "/api/nodes/revoke", Sign(key, nodeId, now, StatusPath))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // other path
        (await PostAsync(client, StatusPath, Sign(key, nodeId, now.AddMinutes(-16), StatusPath))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // too old
        (await PostAsync(client, StatusPath, Sign(key, nodeId, now.AddMinutes(10), StatusPath))).StatusCode.ShouldBe(HttpStatusCode.OK); // a PC clock a bit ahead

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (await PostAsync(client, StatusPath, Sign(other, nodeId, now, StatusPath))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // another key
        // The shared-secret header format from before TK-241 gets nothing.
        (await PostAsync(client, StatusPath, $"{nodeId}.{NodeSignature.Base64Url(RandomNumberGenerator.GetBytes(32))}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_body_is_signed_too()
    {
        var (nodeId, _, key, _) = await RegisterAsync();
        var client = app.CreateClient();
        var now = app.Clock.GetUtcNow();
        (await PostAsync(client, StatusPath, Sign(key, nodeId, now, StatusPath, body: "{}"), "{}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(client, StatusPath, Sign(key, nodeId, now, StatusPath, body: "{}"), "{\"x\":1}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Disconnecting_revokes_the_node_in_the_cloud()
    {
        var (nodeId, _, key, _) = await RegisterAsync();
        var client = app.CreateClient();
        (await PostAsync(client, "/api/nodes/revoke", Sign(key, nodeId, app.Clock.GetUtcNow(), "/api/nodes/revoke"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.InDbAsync(db => db.Nodes.SingleAsync(n => n.Id == nodeId))).RevokedUtc.ShouldNotBeNull();
        (await PostAsync(client, StatusPath, Sign(key, nodeId, app.Clock.GetUtcNow(), StatusPath))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_owner_removes_a_register_of_their_own_tenant_only()
    {
        var (nodeId, storeId, key, owner) = await RegisterAsync();
        var stranger = await app.OwnerAsync();
        (await stranger.Client.PostAsync($"/api/panel/nodes/{nodeId}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await owner.Client.PostAsync($"/api/panel/nodes/{nodeId}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PostAsync(app.CreateClient(), StatusPath, Sign(key, nodeId, app.Clock.GetUtcNow(), StatusPath))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var stores = await owner.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/panel/sync-status");
        stores.EnumerateArray().Single(s => s.GetProperty("storeId").GetGuid() == storeId).GetProperty("nodes").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_PC_without_a_key_cannot_register_and_keeps_its_code()
    {
        var owner = await app.OwnerAsync();
        var store = (await (await owner.Client.PostAsJsonAsync("/api/stores", new { name = "Tienda" })).Json()).GetProperty("id").GetGuid();
        var code = (await (await owner.Client.PostAsync($"/api/stores/{store}/connection-codes", null)).Json()).GetProperty("code").GetString();
        var noKey = await app.CreateClient().PostAsJsonAsync("/api/nodes/register", new { code, nodeName = "PC vieja" });
        (await noKey.ErrorCode()).ShouldBe("Nodes.UpdateRequired");
        // The code was not used up: the updated PC connects with it.
        (await RegisterWithCodeAsync(app.CreateClient(), code!)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task R11_the_nonce_store_lives_in_the_database_and_sweeps_expired_nonces_at_most_once_a_minute()
    {
        var nonces = new NodeNonces(); // a new instance, as after a restart: the database still refuses a replay
        var prefix = Guid.NewGuid().ToString("N");
        var start = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var aExpires = start + 2 * NodeSignature.Window;
        Task<bool> Use(string key, DateTime at) => app.InDbAsync(db => nonces.TryUseAsync(db, prefix + key, at, CancellationToken.None));
        Task<int> Count() => app.InDbAsync(db => db.NodeNonces.CountAsync(n => EF.Functions.Like(n.Key, prefix + "%")));
        (await Use("a", start)).ShouldBeTrue();
        (await Use("a", start)).ShouldBeFalse(); // replay
        (await app.InDbAsync(db => new NodeNonces().TryUseAsync(db, prefix + "a", start, CancellationToken.None))).ShouldBeFalse(); // after a restart too
        (await Use("b", aExpires.AddSeconds(-30))).ShouldBeTrue(); // sweeps; "a" is still valid
        (await Use("c", aExpires.AddSeconds(1))).ShouldBeTrue(); // "a" expired, but the last sweep was 31 s ago
        (await Count()).ShouldBe(3);
        (await Use("d", aExpires.AddSeconds(31))).ShouldBeTrue(); // a minute after the last sweep: "a" goes
        (await Count()).ShouldBe(3);
    }

    private static async Task<HttpStatusCode> RegisterWithCodeAsync(HttpClient client, string code)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (await client.PostAsJsonAsync("/api/nodes/register", new { code, nodeName = "PC", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) })).StatusCode;
    }
}
