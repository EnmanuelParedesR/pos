using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Ingest;

/// <param name="Payload">The event JSON as stored in the node outbox: a JSON object, or that JSON as a string.</param>
public sealed record IngestEvent(Guid Id, string? Type, Guid AggregateId, DateTime OccurredUtc, JsonElement Payload);
public sealed record IngestRequest(IReadOnlyList<IngestEvent>? Events);
public sealed record IngestResponse(IReadOnlyList<Guid> Accepted);

internal static class IngestEndpoint
{
    public const int MaxBatch = 200;

    public static void MapIngest(this IEndpointRouteBuilder app) =>
        app.MapPost("/ingest/v1/events", async (IngestRequest request, ClaimsPrincipal user, CloudDbContext db, InboxSignal signal, TimeProvider clock, CancellationToken ct) =>
        {
            var events = request.Events ?? [];
            if (events.Count is 0 or > MaxBatch)
                return EndpointExtensions.Problem(Error.Validation("Ingest.InvalidBatch", $"El lote debe traer entre 1 y {MaxBatch} eventos."));
            if (events.Any(e => e.Id == Guid.Empty || string.IsNullOrWhiteSpace(e.Type) || e.Type.Length > 200
                                || e.Payload.ValueKind is not (JsonValueKind.Object or JsonValueKind.String)))
                return EndpointExtensions.Problem(Error.Validation("Ingest.InvalidEvent", "Un evento del lote no trae id, tipo o contenido."));

            var node = NodeIdentity.From(user);
            var now = clock.GetUtcNow().UtcDateTime;
            var batch = events.DistinctBy(e => e.Id).ToList();
            await InsertNewAsync(db, node, batch, now, ct);

            // Node freshness: last batch received and newest event time (never moves back).
            var newest = batch.Max(e => Utc(e.OccurredUtc));
            var tracked = await db.Nodes.SingleAsync(n => n.Id == node.NodeId, ct);
            tracked.LastSyncUtc = now;
            if (tracked.LastEventUtc is null || newest > tracked.LastEventUtc) tracked.LastEventUtc = newest;
            await db.SaveChangesAsync(ct);

            signal.Notify();
            return Results.Ok(new IngestResponse(batch.Select(e => e.Id).ToList()));
        }).RequireAuthorization(CloudAuth.NodePolicy);

    /// <summary>"…Z" binds as UTC, an offset as Local, no zone as Unspecified (taken as UTC, as the outbox writes it).</summary>
    private static DateTime Utc(DateTime v) => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc);

    /// <summary>Inserts the events this tenant has not received yet. A duplicate is simply already there (idempotent by event id).</summary>
    private static async Task InsertNewAsync(CloudDbContext db, NodeIdentity node, List<IngestEvent> batch, DateTime now, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var ids = batch.Select(e => e.Id).ToList();
            var known = await db.Inbox.Where(i => i.TenantId == node.TenantId && ids.Contains(i.EventId)).Select(i => i.EventId).ToListAsync(ct);
            db.Inbox.AddRange(batch.Where(e => !known.Contains(e.Id)).Select(e => new InboxEvent
            {
                TenantId = node.TenantId, EventId = e.Id, StoreId = node.StoreId, NodeId = node.NodeId, Type = e.Type!,
                AggregateId = e.AggregateId, OccurredUtc = Utc(e.OccurredUtc),
                Payload = e.Payload.ValueKind == JsonValueKind.String ? e.Payload.GetString()! : e.Payload.GetRawText(), ReceivedUtc = now,
            }));
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                // The same batch arrived twice at once (a node retry): the other request inserted some rows first. Look again.
                db.ChangeTracker.Clear();
            }
        }
    }
}
