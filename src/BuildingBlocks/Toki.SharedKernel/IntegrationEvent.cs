namespace Toki.SharedKernel;

/// <summary>Public, versioned event written to the outbox in the same transaction as the change.
/// Never change a published event: add a V2.</summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTime OcurridoUtc { get; }
    Guid AgregadoId { get; }
}

public abstract record IntegrationEvent(Guid AgregadoId) : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public DateTime OcurridoUtc { get; init; } = DateTime.UtcNow;
}
