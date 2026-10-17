using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using Abastalia.BuildingBlocks.Security;

namespace Abastalia.Lite.Tests;

/// <summary>TK-321 (ADR-0013): the selling device. It pairs with a code from the panel and its own P-256 key, signs every request (ADR-0008's node
/// format, timestamp and nonce, body hash), one at a time per business, the owner revokes it from the panel, and the plan allows one cashier.</summary>
public sealed class DeviceTests(LiteApp app) : IClassFixture<LiteApp>
{
    private const string Sessions = "/api/identity/sessions";
    private static readonly Guid CashierRole = new("0199e000-0000-7000-8000-000000000004");

    private async Task<Guid> ProvisionAsync()
    {
        var tenantId = Guid.CreateVersion7();
        (await app.Provision(new { TenantId = tenantId, OwnerName = "Dueña", OwnerCode = "dueno", LiteApp.Pin })).StatusCode.ShouldBe(HttpStatusCode.Created);
        return tenantId;
    }

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString() : null;

    [Fact]
    public async Task Pairing_takes_a_live_code_once()
    {
        var tenant = await ProvisionAsync();
        var code = await app.CodeAsync(tenant);

        var wrong = code == "12345678" ? "87654321" : "12345678";
        (await CodeOf(await app.PairAsync(wrong, new TestDevice()))).ShouldBe("Lite.InvalidCode");
        var badKey = await app.CreateClient().PostAsJsonAsync("/lite/devices/pair", new { code, publicKey = "bm90LWEta2V5" });
        (await CodeOf(badKey)).ShouldBe("Lite.InvalidKey");

        var device = await app.PairAsync(code);
        await app.SignInAsync(device, "dueno", LiteApp.Pin);
        (await app.Panel().PostAsync($"/internal/tenants/{tenant}/devices/{device.Id}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // Reused: the code paired the first one and is gone, even with no device left.
        var again = await app.PairAsync(code, new TestDevice());
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(again)).ShouldBe("Lite.InvalidCode");

        // Expired: 10 minutes.
        var late = await app.CodeAsync(tenant);
        app.Clock.Advance(TimeSpan.FromMinutes(11));
        (await CodeOf(await app.PairAsync(late, new TestDevice()))).ShouldBe("Lite.InvalidCode");

        // A new code ends the one before it.
        var first = await app.CodeAsync(tenant);
        var second = await app.CodeAsync(tenant);
        (await CodeOf(await app.PairAsync(first, new TestDevice()))).ShouldBe("Lite.InvalidCode");
        await app.PairAsync(second);

        // Only the cloud asks for codes, and only for a business of this host.
        (await app.Panel(key: null).PostAsync($"/internal/tenants/{tenant}/device-codes", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Panel().PostAsync($"/internal/tenants/{Guid.CreateVersion7()}/device-codes", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task One_device_sells_at_a_time_and_a_revoked_one_gets_401()
    {
        var o = await app.BusinessAsync("Dueña");
        var code = await app.CodeAsync(o.TenantId);
        var next = new TestDevice();

        var refused = await app.PairAsync(code, next);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(refused)).ShouldBe("Lite.DeviceLimit");
        var listed = await app.Panel().GetFromJsonAsync<JsonElement>($"/internal/tenants/{o.TenantId}/devices");
        listed.GetProperty("allowed").GetInt32().ShouldBe(1);
        listed.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ShouldBe([o.Device.Id]);

        (await app.Panel(key: null).PostAsync($"/internal/tenants/{o.TenantId}/devices/{o.Device.Id}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Panel().PostAsync($"/internal/tenants/{Guid.CreateVersion7()}/devices/{o.Device.Id}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Panel().GetAsync($"/internal/tenants/{Guid.CreateVersion7()}/devices")).StatusCode.ShouldBe(HttpStatusCode.NotFound); // not hosted here
        (await app.Client(o).GetAsync("/api/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.Panel().PostAsync($"/internal/tenants/{o.TenantId}/devices/{o.Device.Id}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.Client(o).GetAsync("/api/identity/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.Device(o.Device).PostAsJsonAsync(Sessions, new { Code = "dueno", LiteApp.Pin })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.Panel().GetFromJsonAsync<JsonElement>($"/internal/tenants/{o.TenantId}/devices")).GetProperty("devices").GetArrayLength().ShouldBe(0);

        // The code was not spent by the refusal: the new device pairs with it now.
        (await app.PairAsync(code, next)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_signed_request_counts_once_within_15_minutes_and_for_its_own_body_and_path()
    {
        var o = await app.BusinessAsync("Dueña");
        var now = app.Clock.GetUtcNow().ToUnixTimeSeconds();
        var body = $$"""{"code":"dueno","pin":"{{LiteApp.Pin}}"}""";

        var nonce = TestDevice.NewNonce();
        (await SendAsync(o.Device, Sessions, body, seconds: now, nonce: nonce)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(o.Device, Sessions, body, seconds: now, nonce: nonce)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // replayed

        (await SendAsync(o.Device, Sessions, body.Replace("dueno", "otro", StringComparison.Ordinal), signedBody: body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(o.Device, Sessions, body, claimedBody: "{}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // signed hash ≠ header
        (await SendAsync(o.Device, Sessions, body, signedPath: "/api/identity/users")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(o.Device, Sessions, body, seconds: now - 16 * 60)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(o.Device, Sessions, body, seconds: now + 16 * 60)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(o.Device, Sessions, body, seconds: now - 14 * 60)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(new TestDevice { Id = o.Device.Id }, Sessions, body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // another key

        // Without the body-hash header, or with only a store's device secret, nothing answers.
        var unsigned = new HttpRequestMessage(HttpMethod.Post, Sessions) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        unsigned.Headers.Add("X-Device-Signature", o.Device.Sign("POST", Sessions, NodeSignature.BodyHash(Encoding.UTF8.GetBytes(body)), now, TestDevice.NewNonce()));
        (await app.CreateClient().SendAsync(unsigned)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var secret = app.CreateClient();
        secret.DefaultRequestHeaders.Add("X-Device", "anything");
        (await secret.PostAsJsonAsync(Sessions, new { Code = "dueno", LiteApp.Pin })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_plan_allows_the_owner_and_one_cashier()
    {
        var o = await app.BusinessAsync("Dueña");
        var owner = app.Client(o);
        (await owner.PostAsJsonAsync("/api/identity/users", new { Name = "Cajera", Code = "c1", Pin = "4321", RoleId = CashierRole })).EnsureSuccessStatusCode();
        var second = await owner.PostAsJsonAsync("/api/identity/users", new { Name = "Otra cajera", Code = "c2", Pin = "4322", RoleId = CashierRole });
        second.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeOf(second)).ShouldBe("Identity.CashierLimitReached");

        // The cashier signs in with the PIN on the same device.
        await app.SignInAsync(o.Device, "c1", "4321");
    }

    [Fact]
    public async Task The_limit_message_says_the_plans_cap_and_the_way_up()
    {
        var o = await app.BusinessAsync("Dueña");
        var refused = await app.PairAsync(await app.CodeAsync(o.TenantId), new TestDevice());
        var detail = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
        detail.ShouldContain("1 dispositivo de venta");
        detail.ShouldContain("plan mayor");
        detail.ShouldContain("instalación en una PC");
    }

    private Task<HttpResponseMessage> SendAsync(TestDevice device, string path, string body, string? signedBody = null, string? claimedBody = null,
        string? signedPath = null, long? seconds = null, string? nonce = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        string Hash(string s) => NodeSignature.BodyHash(Encoding.UTF8.GetBytes(s));
        request.Headers.Add("X-Device-Signature", device.Sign("POST", signedPath ?? path, Hash(signedBody ?? body),
            seconds ?? app.Clock.GetUtcNow().ToUnixTimeSeconds(), nonce ?? TestDevice.NewNonce()));
        request.Headers.Add(NodeSignature.BodyHashHeader, Hash(claimedBody ?? signedBody ?? body));
        return app.CreateClient().SendAsync(request);
    }
}
