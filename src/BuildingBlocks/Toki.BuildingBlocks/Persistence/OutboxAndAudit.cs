using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Persistence;

public interface IOutbox
{
    /// <summary>Saved with the command's transaction; nothing is published if the command fails.</summary>
    void Add(IIntegrationEvent @event);
}

public interface IAuditLog
{
    /// <summary>Appends a hash-chained record inside the command's transaction.</summary>
    Task RecordAsync(string action, string resourceType, Guid resourceId, object data, CancellationToken ct);
}

internal sealed class Outbox(InfraDbContext db, IUserContext user, TimeProvider clock) : IOutbox
{
    public void Add(IIntegrationEvent @event)
    {
        @event.OccurredUtc = clock.GetUtcNow().UtcDateTime;
        db.Outbox.Add(new OutboxMessage
        {
            Id = @event.EventId,
            Type = @event.GetType().FullName!,
            AggregateId = @event.AggregateId,
            Payload = JsonSerializer.Serialize(@event, @event.GetType(), Json.Options),
            OccurredUtc = @event.OccurredUtc,
            UserId = user.UserId == Guid.Empty ? null : user.UserId,
            DeviceId = user.DeviceId,
        });
    }
}

internal sealed class AuditLog(InfraDbContext db, UnitOfWork uow, IUserContext user, TimeProvider clock) : IAuditLog
{
    public async Task RecordAsync(string action, string resourceType, Guid resourceId, object data, CancellationToken ct)
    {
        // Outside a transaction the head update would commit alone and the record would be lost, breaking the chain.
        if (uow.Transaction is null) throw new InvalidOperationException("The audit log is only written inside a command.");

        // Bumping the head first takes its write lock, so concurrent commands cannot fork the chain.
        await db.AuditHead.Where(h => h.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Sequence, h => h.Sequence + 1), ct);
        var head = await db.AuditHead.AsNoTracking().SingleAsync(h => h.Id == 1, ct);

        var record = new AuditRecord
        {
            Sequence = head.Sequence,
            Id = Guid.CreateVersion7(),
            DateUtc = clock.GetUtcNow().UtcDateTime,
            UserId = user.UserId == Guid.Empty ? null : user.UserId,
            SupervisorId = user.SupervisorId,
            DeviceId = user.DeviceId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Data = JsonSerializer.Serialize(data, Json.Options),
            PreviousHash = head.Hash,
        };
        record.Hash = AuditChain.Hash(record);
        db.AuditRecords.Add(record);

        await db.AuditHead.Where(h => h.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Hash, record.Hash), ct);
    }
}

public static class AuditChain
{
    public static byte[] Hash(AuditRecord r)
    {
        var content = string.Join('|', r.Sequence, r.Id, r.DateUtc.ToString("O"), r.UserId, r.SupervisorId,
            r.DeviceId, r.Action, r.ResourceType, r.ResourceId, r.Data);
        return SHA256.HashData([.. r.PreviousHash, .. Encoding.UTF8.GetBytes(content)]);
    }

    /// <summary>Recomputes the chain. Returns the first broken sequence number, or null when intact.
    /// The chain must also end at the head row: deleting the newest records breaks it at the first missing number.</summary>
    public static async Task<long?> VerifyAsync(InfraDbContext db, CancellationToken ct)
    {
        var head = await db.AuditHead.AsNoTracking().SingleAsync(h => h.Id == 1, ct);
        var previous = new byte[32];
        long last = 0;
        await foreach (var r in db.AuditRecords.AsNoTracking().Where(r => r.Sequence <= head.Sequence).OrderBy(r => r.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (r.Sequence != last + 1 || !r.PreviousHash.SequenceEqual(previous) || !Hash(r).SequenceEqual(r.Hash)) return last + 1;
            (previous, last) = (r.Hash, r.Sequence);
        }
        return last == head.Sequence && previous.SequenceEqual(head.Hash) ? null : last + 1;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
