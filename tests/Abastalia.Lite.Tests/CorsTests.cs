using System.Net;
using Shouldly;

namespace Abastalia.Lite.Tests;

/// <summary>TK-324: the selling app (Abastalia.Client.Lite) calls the host from its own origin (Lite:AppOrigins): the preflight passes before the
/// device check, the signed headers are allowed, and Date is readable so the device can sign with the host's clock. Any other origin gets nothing.</summary>
public sealed class CorsTests(LiteApp app) : IClassFixture<LiteApp>
{
    [Fact]
    public async Task The_app_origin_may_send_signed_requests_and_read_the_host_clock()
    {
        var preflight = await PreflightAsync(LiteApp.AppOrigin);
        preflight.StatusCode.ShouldBe(HttpStatusCode.NoContent); // not the device check's 401: a preflight carries no signature
        preflight.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([LiteApp.AppOrigin]);
        var allowed = string.Join(',', preflight.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant();
        foreach (var header in new[] { "authorization", "content-type", "x-device-signature", "x-body-sha256" }) allowed.ShouldContain(header);

        // An unsigned call is refused, and the app can still read why.
        using var refused = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me") { Headers = { { "Origin", LiteApp.AppOrigin } } };
        var answer = await app.CreateClient().SendAsync(refused);
        answer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        answer.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([LiteApp.AppOrigin]);
        string.Join(',', answer.Headers.GetValues("Access-Control-Expose-Headers")).ShouldContain("Date");
    }

    [Fact]
    public async Task Another_origin_is_not_allowed()
    {
        var preflight = await PreflightAsync("https://otro-sitio.test");
        preflight.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    private Task<HttpResponseMessage> PreflightAsync(string origin) => app.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/sales")
    {
        Headers =
        {
            { "Origin", origin },
            { "Access-Control-Request-Method", "POST" },
            { "Access-Control-Request-Headers", "authorization,content-type,x-device-signature,x-body-sha256" },
        },
    });
}
