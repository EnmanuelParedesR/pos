using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Fiscal.Contracts;
using Toki.Host.Cloud.Assistant;
using Toki.Host.Cloud.Panel;
using Toki.Host.Cloud.Platform;

namespace Toki.Cloud.Tests;

/// <summary>TK-254: releases per channel with a staged rollout, versions in the field. TK-256: the assistant's certification tools.</summary>
public sealed class ReleasesTests(CloudApp app) : IClassFixture<CloudApp>
{
    private static async Task<JsonElement> AllowedAsync(StoreNode node, string channel) =>
        await (await node.Client.PostAsJsonAsync("/api/nodes/updates", new { channel, version = "1.4.0" })).Json();

    [Fact]
    public async Task Staff_release_a_version_per_channel_to_a_share_of_the_stores_and_see_the_versions_in_the_field()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        (await owner.Client.GetAsync("/api/platform/releases")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AllowedAsync(node, "pilot")).GetProperty("managed").GetBoolean().ShouldBeFalse(); // nothing released: the store follows its feed

        var staff = await app.StaffAsync();
        (await staff.Client.PutAsJsonAsync("/api/platform/releases", new { channel = "beta", version = "1.5.0", rolloutPercent = 100 })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await staff.Client.PutAsJsonAsync("/api/platform/releases", new { channel = "pilot", version = "1.5", rolloutPercent = 100 })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await staff.Client.PutAsJsonAsync("/api/platform/releases", new { channel = "pilot", version = "1.5.0", rolloutPercent = 100, notesUrl = "http://x" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await staff.Client.PutAsJsonAsync("/api/platform/releases", new { channel = "pilot", version = "1.5.0", rolloutPercent = 100, notesUrl = "https://toki.example/1.5" }))
            .EnsureSuccessStatusCode();
        var allowed = await AllowedAsync(node, "pilot");
        (allowed.GetProperty("managed").GetBoolean(), allowed.GetProperty("allowedVersion").GetString(), allowed.GetProperty("notesUrl").GetString())
            .ShouldBe((true, "1.5.0", "https://toki.example/1.5"));
        (await AllowedAsync(node, "stable")).GetProperty("managed").GetBoolean().ShouldBeFalse();

        // A newer one paused (0 %) reaches nobody; at 100 % it does. Every change is in the platform audit with who made it.
        (await staff.Client.PutAsJsonAsync("/api/platform/releases", new { channel = "pilot", version = "1.6.0", rolloutPercent = 0 })).EnsureSuccessStatusCode();
        (await AllowedAsync(node, "pilot")).GetProperty("allowedVersion").GetString().ShouldBe("1.5.0");
        (await staff.Client.PutAsJsonAsync("/api/platform/releases", new { channel = "pilot", version = "1.6.0", rolloutPercent = 100 })).EnsureSuccessStatusCode();
        (await AllowedAsync(node, "pilot")).GetProperty("allowedVersion").GetString().ShouldBe("1.6.0");
        (await app.InDbAsync(db => db.PlatformAudit.CountAsync(a => a.Method == "PUT" && a.Path == "/api/platform/releases"))).ShouldBe(6);
        (await app.InDbAsync(db => db.AppReleases.SingleAsync(r => r.Version == "1.6.0"))).ChangedBy.ShouldBe(CloudApp.StaffEmail);

        // The field: the store's heartbeat says 1.4.0.
        (await node.SendAsync(Events.Envelope(PlatformTests.Heartbeat("1.4.0"), app.Clock.GetUtcNow().UtcDateTime))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        var list = await staff.Client.GetFromJsonAsync<JsonElement>("/api/platform/releases");
        list.GetProperty("releases").GetArrayLength().ShouldBe(2);
        list.GetProperty("field").EnumerateArray().ShouldContain(v => v.GetProperty("version").GetString() == "1.4.0");
    }

    [Fact]
    public void The_rollout_reaches_stores_below_the_percent_and_always_the_same_ones_first()
    {
        var store = Guid.NewGuid();
        var bucket = Releases.Bucket(store);
        bucket.ShouldBe(Releases.Bucket(store));
        AppRelease R(string v, int p) => new() { Channel = "stable", Version = v, RolloutPercent = p };
        Releases.For([R("1.5.0", bucket), R("1.4.0", 100)], store)!.Version.ShouldBe("1.4.0"); // not yet in the 1.5.0 share
        Releases.For([R("1.5.0", bucket + 1), R("1.4.0", 100)], store)!.Version.ShouldBe("1.5.0");
        Releases.For([R("1.5.0", 0)], store).ShouldBeNull();
    }

    [Fact]
    public async Task The_assistant_says_the_tenants_own_certification_step_and_what_to_do_now()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner, "Colmado Ana");
        var now = app.Clock.GetUtcNow().UtcDateTime;
        (await node.SendAsync(Events.Envelope(new CertificationProgressedV1([0, 1], CertificationSteps.Count, now.AddDays(-3)), now))).EnsureSuccessStatusCode();
        await app.ApplyAsync();

        await using var scope = app.Services.CreateAsyncScope();
        var assistant = new OwnerAssistant(null!, scope.ServiceProvider.GetRequiredService<PanelQueries>()); // tools only: no model call
        var status = JsonDocument.Parse(await assistant.RunToolAsync(owner.TenantId, new ChatToolCall("1", "certification_status", "{}"), CancellationToken.None))
            .RootElement.EnumerateArray().ShouldHaveSingleItem();
        (status.GetProperty("storeName").GetString(), status.GetProperty("currentStep").GetInt32(), status.GetProperty("daysOnStep").GetInt32())
            .ShouldBe(("Colmado Ana", 3, 3));
        status.GetProperty("nextAction").GetString().ShouldBe(CertificationGuide.Steps[2].Next);

        // Another business sees only its own stores; the FAQ is fixed text.
        var other = await app.OwnerAsync();
        (await assistant.RunToolAsync(other.TenantId, new ChatToolCall("2", "certification_status", "{}"), CancellationToken.None)).ShouldBe("[]");
        (await assistant.RunToolAsync(owner.TenantId, new ChatToolCall("3", "certification_faq", "{}"), CancellationToken.None)).ShouldContain("Facturador Gratuito");
    }
}
