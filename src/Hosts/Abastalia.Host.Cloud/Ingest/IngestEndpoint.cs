using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Ingest;

/// <param name="Payload">The event JSON as stored in the node outbox: a JSON object, or that JSON as a string.</param>
public sealed record IngestEvent(Guid Id, string? Type, Guid AggregateId, DateTime OccurredUtc, JsonElement Payload);
public sealed record IngestRequest(IReadOnlyList<IngestEvent>? Events);
/// <param name="LicenseUtc">TK-287: when the business's subscription last changed (a payment, a staff override, a block). The store pulls its
/// license when this moves, so a block or an unblock from the console reaches it with its next upload instead of its next 4-hour heartbeat.</param>
public sealed record IngestResponse(IReadOnlyList<Guid> Accepted, DateTime? LicenseUtc = null);

/// <remarks>R11 B-Med3: a compromised store PC cannot fill the database. Per PC, <c>Ingest:BatchesPerMinutePerNode</c> (60) batches; per event,
/// <see cref="MaxPayloadBytes"/>; a type Abastalia does not know yet waits only up to <c>Ingest:MaxUnknownPending</c> (1000) per business; and per
/// business, <c>Ingest:MaxEventsPerTenantDay</c> (100,000) events a day. Past a cap the batch is refused with 429 (the PC keeps it and retries)
/// and a warning is logged ("Ingest cap reached"), which the Azure alert on the console logs picks up.</remarks>
internal static class IngestEndpoint
{
    public const int MaxBatch = 200;
    public const int MaxPayloadBytes = 64 * 1024;
    public const string NodeRateLimit = "ingest-node";
    /// <summary>A day or backlog cap does not clear in a minute: the store asks again in 15.</summary>
    public const string CapRetryAfterSeconds = "900";

    public static void MapIngest(this IEndpointRouteBuilder app) =>
        app.MapPost("/ingest/v1/events", async (IngestRequest request, ClaimsPrincipal user, CloudDbContext db, InboxSignal signal, TimeProvider clock,
            IConfiguration config, ILogger<InboxApplier> log, HttpResponse response, CancellationToken ct) =>
        {
            var events = request.Events ?? [];
            if (events.Count is 0 or > MaxBatch)
                return EndpointExtensions.Problem(Error.Validation("Ingest.InvalidBatch", $"El lote debe traer entre 1 y {MaxBatch} eventos."));
            if (events.Any(e => e.Id == Guid.Empty || string.IsNullOrWhiteSpace(e.Type) || e.Type.Length > 200 || !e.Type.StartsWith("Abastalia.", StringComparison.Ordinal)
                                || e.Payload.ValueKind is not (JsonValueKind.Object or JsonValueKind.String)))
                return EndpointExtensions.Problem(Error.Validation("Ingest.InvalidEvent", "Un evento del lote no trae id, tipo o contenido."));
            if (events.Any(e => Payload(e).Length > MaxPayloadBytes))
                return EndpointExtensions.Problem(Error.Validation("Ingest.EventTooLarge", "Un evento del lote pasa de 64 KB."));

            var node = NodeIdentity.From(user);
            var now = clock.GetUtcNow().UtcDateTime;
            var batch = events.DistinctBy(e => e.Id).ToList();
            if (await CapReachedAsync(db, node, batch, now, config, ct) is { } cap)
            {
                log.LogWarning("Ingest cap reached for tenant {TenantId} (node {NodeId}): {Cap}", node.TenantId, node.NodeId, cap);
                response.Headers.RetryAfter = CapRetryAfterSeconds; // TK-248: the store keeps its outbox and asks again then
                return Results.Problem(detail: "La nube no acepta más datos de esta tienda por ahora. Se enviarán más tarde.", statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Ingest.Cap", extensions: new Dictionary<string, object?> { ["code"] = "Ingest.Cap" });
            }
            await InsertNewAsync(db, node, batch, now, ct);

            // Node freshness: last batch received and newest event time (never moves back).
            var newest = batch.Max(e => Utc(e.OccurredUtc));
            var tracked = await db.Nodes.SingleAsync(n => n.Id == node.NodeId, ct);
            tracked.LastSyncUtc = now;
            if (tracked.LastEventUtc is null || newest > tracked.LastEventUtc) tracked.LastEventUtc = newest;
            await db.SaveChangesAsync(ct);

            signal.Notify();
            var license = await db.TenantSubscriptions.AsNoTracking().Where(s => s.TenantId == node.TenantId).Select(s => (DateTime?)s.UpdatedUtc).FirstOrDefaultAsync(ct);
            return Results.Ok(new IngestResponse(batch.Select(e => e.Id).ToList(), license is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : null));
        }).RequireAuthorization(CloudAuth.NodePolicy).RequireRateLimiting(NodeRateLimit);

    private static string Payload(IngestEvent e) => e.Payload.ValueKind == JsonValueKind.String ? e.Payload.GetString()! : e.Payload.GetRawText();

    /// <summary>Which cap this batch would pass, or null.</summary>
    private static async Task<string?> CapReachedAsync(CloudDbContext db, NodeIdentity node, List<IngestEvent> batch, DateTime now, IConfiguration config,
        CancellationToken ct)
    {
        var since = now.AddDays(-1);
        if (await db.Inbox.CountAsync(i => i.TenantId == node.TenantId && i.ReceivedUtc > since, ct) + batch.Count > config.GetValue("Ingest:MaxEventsPerTenantDay", 100_000))
            return "events per day";
        var unknown = batch.Count(e => !Projectors.ByType.ContainsKey(e.Type!));
        if (unknown == 0) return null;
        var known = Projectors.ByType.Keys.ToList();
        return await db.Inbox.CountAsync(i => i.TenantId == node.TenantId && i.AppliedUtc == null && !known.Contains(i.Type), ct) + unknown
               > config.GetValue("Ingest:MaxUnknownPending", 1000)
            ? "unknown event types"
            : null;
    }

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
                AggregateId = e.AggregateId, OccurredUtc = Utc(e.OccurredUtc), Payload = Payload(e), ReceivedUtc = now,
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
