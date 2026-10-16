using Abastalia.SharedKernel;

namespace Abastalia.Identity.Contracts;

/// <summary>A user was created or edited (or is in the snapshot sent when the store connects to the cloud), so the panel can show names
/// instead of ids. Never carries the PIN, the code or the permissions.</summary>
public sealed record UserProfileChangedV1(Guid UserId, string Name, bool Active) : IntegrationEvent(UserId);

public sealed record UserName(Guid Id, string Name);

/// <summary>Every user's name (inactive ones too), for screens that show who did something (the audit log).</summary>
public interface IUserDirectory
{
    Task<IReadOnlyList<UserName>> AllAsync(CancellationToken ct);
}

/// <summary>TK-190: how many active registers reported their peripherals, and how many have a scale or a printer.</summary>
public sealed record PeripheralsSummary(int Reported, int WithScale, int WithPrinter);

public interface IDevicePeripherals
{
    Task<PeripheralsSummary> SummaryAsync(CancellationToken ct);
}
