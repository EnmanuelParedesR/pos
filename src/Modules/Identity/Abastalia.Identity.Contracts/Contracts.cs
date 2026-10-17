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

/// <summary>TK-320: the first run (the owner and the first device) of a business the Lite host provisions, the same command as a store's setup.
/// Only works while the business has no users. Never inside another command.</summary>
public interface IFirstRun
{
    /// <returns>The device's secret (it proves the device on every request), or why not.</returns>
    Task<Result<FirstDevice>> SetUpAsync(string ownerName, string ownerCode, string pin, string deviceName, CancellationToken ct);
}

public sealed record FirstDevice(Guid OwnerId, Guid DeviceId, string DeviceSecret);

/// <summary>TK-190: how many active registers reported their peripherals, and how many have a scale or a printer.</summary>
public sealed record PeripheralsSummary(int Reported, int WithScale, int WithPrinter);

public interface IDevicePeripherals
{
    Task<PeripheralsSummary> SummaryAsync(CancellationToken ct);
}
