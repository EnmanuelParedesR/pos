using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Settings;

/// <summary>TK-226: which version's "Novedades" each user closed, on the store's server (so it follows the user, not the device).</summary>
public sealed class ReleaseNotesSeenTests(TokiApp app) : IClassFixture<TokiApp>
{
    private static async Task<string?> SeenAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/settings/release-notes/seen")).GetProperty("version").GetString();

    [Fact]
    public async Task Each_user_keeps_the_last_version_seen()
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        var other = app.Client(await app.UserAsync(Roles.Cashier));
        (await SeenAsync(cashier)).ShouldBeNull();

        (await cashier.PutAsJsonAsync("/api/settings/release-notes/seen", new { Version = "1.0.0" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await cashier.PutAsJsonAsync("/api/settings/release-notes/seen", new { Version = "1.1.0" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SeenAsync(cashier)).ShouldBe("1.1.0");
        (await SeenAsync(other)).ShouldBeNull();

        (await cashier.PutAsJsonAsync("/api/settings/release-notes/seen", new { Version = "" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);    }
}
