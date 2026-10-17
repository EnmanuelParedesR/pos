using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Host.Cloud.Nodes;

namespace Abastalia.Cloud.Tests;

/// <summary>The cloud with a fake Abastalia.Host.Lite behind <c>Lite:Url</c>.</summary>
public sealed class LiteCloudApp : CloudApp
{
    public const string Key = "lite-provisioning-key-0123456789abcdef";

    public FakeLite Lite { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Lite:Url", FakeLite.Url);
        builder.UseSetting("Lite:ProvisioningKey", Key);
        builder.ConfigureTestServices(s => s.AddHttpClient(LiteDeviceEndpoints.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Lite));
    }
}

/// <summary>Answers as Host.Lite's internal routes do, and keeps each request with its key.</summary>
public sealed class FakeLite : HttpMessageHandler
{
    public const string Url = "https://lite.test/";

    public ConcurrentQueue<(HttpMethod Method, string Path, string? Key)> Requests { get; } = new();

    public bool Down { get; set; }

    /// <summary>The businesses it serves; any other gets 404.</summary>
    public ConcurrentDictionary<Guid, bool> Hosted { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down) throw new HttpRequestException("down");
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue((request.Method, path, request.Headers.TryGetValues("X-Provisioning-Key", out var k) ? k.Single() : null));
        HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
        if (!Hosted.ContainsKey(Guid.Parse(path.Split('/')[3])))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { code = "Lite.NotFound" }) });
        return Task.FromResult(path.EndsWith("/device-codes", StringComparison.Ordinal) ? Json(new { code = "12345678", expiresUtc = "2026-10-15T15:10:00Z" })
            : path.EndsWith("/devices", StringComparison.Ordinal) ? Json(new { allowed = 1, devices = new[] { new { id = Guid.Empty, name = "Celular", createdUtc = "2026-10-15T15:00:00Z" } } })
            : new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}

/// <summary>TK-321: "Tiendas" asks Host.Lite for the business's selling device, always for the token's tenant; Host.Lite says whether it serves it.</summary>
public sealed class LiteDevicesTests(LiteCloudApp app) : IClassFixture<LiteCloudApp>
{
    [Fact]
    public async Task The_panel_pairs_lists_and_revokes_the_selling_device_of_its_own_business()
    {
        var owner = await app.OwnerAsync();
        (await owner.Client.GetAsync("/api/panel/lite/devices")).StatusCode.ShouldBe(HttpStatusCode.NotFound); // a business the host does not serve
        app.Lite.Requests.Clear();

        app.Lite.Hosted[owner.TenantId] = true;
        var devices = await owner.Client.GetFromJsonAsync<JsonElement>("/api/panel/lite/devices");
        devices.GetProperty("devices")[0].GetProperty("name").GetString().ShouldBe("Celular");
        (await owner.Client.PostAsync("/api/panel/lite/device-codes", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var device = Guid.CreateVersion7();
        (await owner.Client.PostAsync($"/api/panel/lite/devices/{device}/revoke", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        app.Lite.Requests.Select(r => (r.Method.Method, r.Path, r.Key)).ShouldBe([
            ("GET", $"/internal/tenants/{owner.TenantId}/devices", LiteCloudApp.Key),
            ("POST", $"/internal/tenants/{owner.TenantId}/device-codes", LiteCloudApp.Key),
            ("POST", $"/internal/tenants/{owner.TenantId}/devices/{device}/revoke", LiteCloudApp.Key),
        ]);

        app.Lite.Down = true;
        (await owner.Client.GetAsync("/api/panel/lite/devices")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        app.Lite.Down = false;
    }
}
