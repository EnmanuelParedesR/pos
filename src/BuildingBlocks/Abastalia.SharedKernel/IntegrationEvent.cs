namespace Abastalia.SharedKernel;

/// <summary>Public, versioned event written to the outbox in the same transaction as the change.
/// Never change a published event: add a V2.</summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    /// <summary>Set by the outbox from the application clock when the event is saved.</summary>
    DateTime OccurredUtc { get; set; }
    Guid AggregateId { get; }
}

public abstract record IntegrationEvent(Guid AggregateId) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public DateTime OccurredUtc { get; set; }
}
