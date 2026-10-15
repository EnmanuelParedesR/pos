using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Toki.CashRegister.Contracts;
using Toki.Purchasing.Contracts;

namespace Toki.Cloud.Tests;

public sealed class IngestTests(CloudApp app) : IClassFixture<CloudApp>
{
    private object Closed(decimal difference = 0) => Events.Envelope(
        new ShiftClosedV1(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, 1000, 1000 + difference, difference), app.Clock.GetUtcNow().UtcDateTime);

    [Fact]
    public async Task Ingest_needs_valid_node_credentials()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var body = new { events = new[] { Closed() } };

        (await app.CreateClient().PostAsJsonAsync("/ingest/v1/events", body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var wrongSecret = app.CreateClient();
        wrongSecret.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Node", $"{node.NodeId}.not-the-secret");
        (await wrongSecret.PostAsJsonAsync("/ingest/v1/events", body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var unknownNode = app.CreateClient();
        unknownNode.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Node", $"{Guid.CreateVersion7()}.whatever");
        (await unknownNode.PostAsJsonAsync("/ingest/v1/events", body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A panel token is not a node.
        (await owner.Client.PostAsJsonAsync("/ingest/v1/events", body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await node.Client.PostAsJsonAsync("/ingest/v1/events", body)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sending_the_same_batch_twice_stores_each_event_once_and_accepts_it_both_times()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var batch = new[] { Closed(), Closed(-50) };

        var first = await (await node.SendAsync(batch)).Json();
        var second = await (await node.SendAsync(batch)).Json();

        var ids = first.GetProperty("accepted").EnumerateArray().Select(e => e.GetGuid()).ToList();
        ids.Count.ShouldBe(2);
        second.GetProperty("accepted").EnumerateArray().Select(e => e.GetGuid()).ShouldBe(ids);
        (await app.InDbAsync(db => db.Inbox.CountAsync(i => ids.Contains(i.EventId)))).ShouldBe(2);

        await app.ApplyAsync();
        await node.SendAsync(batch);
        await app.ApplyAsync();
        (await app.InDbAsync(db => db.ClosedShifts.CountAsync(s => s.StoreId == node.StoreId))).ShouldBe(2);
    }

    [Fact]
    public async Task An_event_type_without_projector_is_stored_but_not_applied()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var purchase = new PurchasePostedV1(Guid.CreateVersion7(), Guid.CreateVersion7(), "B0100000001", 1000, 180, 1180, false);

        (await node.SendAsync(Events.Envelope(purchase, app.Clock.GetUtcNow().UtcDateTime))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.ApplyAsync();

        var row = await app.InDbAsync(db => db.Inbox.SingleAsync(i => i.EventId == purchase.EventId));
        row.Type.ShouldBe("Toki.Purchasing.Contracts.PurchasePostedV1");
        row.AppliedUtc.ShouldBeNull();
        row.Attempts.ShouldBe(0);
        JsonDocument.Parse(row.Payload).RootElement.GetProperty("ncf").GetString().ShouldBe("B0100000001");
    }

    [Fact]
    public async Task A_gzip_batch_is_accepted_and_the_node_freshness_is_tracked()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var closed = Closed();
        var json = JsonSerializer.SerializeToUtf8Bytes(new { events = new[] { closed } });
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) await gzip.WriteAsync(json);
        var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        var r = await node.Client.PostAsync("/ingest/v1/events", content);

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Json()).GetProperty("accepted").GetArrayLength().ShouldBe(1);
        var saved = await app.InDbAsync(db => db.Nodes.SingleAsync(n => n.Id == node.NodeId));
        saved.LastSyncUtc.ShouldBe(app.Clock.GetUtcNow().UtcDateTime);
        saved.LastEventUtc.ShouldBe(app.Clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task A_batch_has_at_most_200_events()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var r = await node.SendAsync([.. Enumerable.Range(0, 201).Select(_ => Closed())]);
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await r.ErrorCode()).ShouldBe("Ingest.InvalidBatch");
    }

    [Fact]
    public async Task A_payload_sent_as_a_json_string_is_accepted_too()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var e = new ShiftClosedV1(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 7, 500, 480, -20) { OccurredUtc = app.Clock.GetUtcNow().UtcDateTime };
        var payload = JsonSerializer.Serialize(e, Toki.BuildingBlocks.Persistence.Json.Options);

        (await node.SendAsync(new { id = e.EventId, type = typeof(ShiftClosedV1).FullName, aggregateId = e.ShiftId, occurredUtc = e.OccurredUtc, payload }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.ApplyAsync();

        (await app.InDbAsync(db => db.ClosedShifts.SingleAsync(s => s.ShiftId == e.ShiftId))).Difference.ShouldBe(-20);
    }
}
