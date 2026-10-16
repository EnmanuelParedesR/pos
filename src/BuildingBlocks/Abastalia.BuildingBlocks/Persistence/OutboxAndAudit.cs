using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Security;
using Abastalia.SharedKernel;

namespace Abastalia.BuildingBlocks.Persistence;

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

internal sealed class AuditLog(InfraDbContext db, UnitOfWork uow, IUserContext user, AuditKey key, TimeProvider clock) : IAuditLog
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
        record.Hash = AuditChain.Hash(record, key.For(record.Sequence));
        db.AuditRecords.Add(record);

        await db.AuditHead.Where(h => h.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Hash, record.Hash), ct);
    }
}

/// <summary>R11 A10: the audit chain's key. Without it, anyone with write access to the database could rewrite records and recompute every
/// SHA-256 after them. The key is a <see cref="ServiceKeys"/> secret (never in the database); records written before it existed (an upgraded
/// store) keep their plain SHA-256, and the first keyed sequence is stored with the key, outside the database too.</summary>
/// <remarks>shortcut: a backup restored on another PC brings records keyed with the old PC's key; the check reports the chain broken there
/// (the paper Z reports and the heads sent to the cloud still prove the old chain).</remarks>
public sealed class AuditKey(ServiceKeys keys)
{
    private const string Name = "audit-chain";

    /// <summary>The key for a record being written (created at the first one, from this sequence on).</summary>
    internal byte[] For(long sequence)
    {
        var stored = keys.GetOrCreate(Name, () => [.. BitConverter.GetBytes(sequence), .. RandomNumberGenerator.GetBytes(32)]);
        return sequence >= BitConverter.ToInt64(stored, 0) ? stored[8..] : [];
    }

    /// <summary>The key a stored record must have been written with: empty before the first keyed sequence (or with no key yet).</summary>
    public Func<long, byte[]> Verifier()
    {
        var stored = keys.Find(Name);
        return sequence => stored is not null && sequence >= BitConverter.ToInt64(stored, 0) ? stored[8..] : [];
    }
}

public static class AuditChain
{
    /// <param name="key">HMAC-SHA256 key (R11 A10); empty for the plain SHA-256 of records written before the key existed.</param>
    public static byte[] Hash(AuditRecord r, byte[]? key = null)
    {
        var content = string.Join('|', r.Sequence, r.Id, r.DateUtc.ToString("O"), r.UserId, r.SupervisorId,
            r.DeviceId, r.Action, r.ResourceType, r.ResourceId, r.Data);
        byte[] data = [.. r.PreviousHash, .. Encoding.UTF8.GetBytes(content)];
        return key is { Length: > 0 } ? HMACSHA256.HashData(key, data) : SHA256.HashData(data);
    }

    /// <summary>Recomputes the chain. Returns the first broken sequence number, or null when intact.
    /// The chain must also end at the head row: deleting the newest records breaks it at the first missing number.</summary>
    public static async Task<long?> VerifyAsync(InfraDbContext db, AuditKey key, CancellationToken ct)
    {
        var head = await db.AuditHead.AsNoTracking().SingleAsync(h => h.Id == 1, ct);
        var keyOf = key.Verifier();
        var previous = new byte[32];
        long last = 0;
        await foreach (var r in db.AuditRecords.AsNoTracking().Where(r => r.Sequence <= head.Sequence).OrderBy(r => r.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (r.Sequence != last + 1 || !r.PreviousHash.SequenceEqual(previous) || !Hash(r, keyOf(r.Sequence)).SequenceEqual(r.Hash)) return last + 1;
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
