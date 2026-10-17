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

/// <summary>TK-320: the first run of a business the Lite host provisions, the same command as a store's setup. TK-321: only the owner; the selling
/// device pairs later with a code from the panel (<see cref="ISellingDevices"/>). Only works while the business has no users. Never inside another command.</summary>
public interface IFirstRun
{
    /// <returns>The owner's id, or why not.</returns>
    Task<Result<Guid>> SetUpAsync(string ownerName, string ownerCode, string pin, CancellationToken ct);
}

/// <summary>TK-321: the Lite host's selling devices, with a store's register limit (<c>Limit.Registers</c>), audit and revocation. The host proves
/// each one by its signed requests and says so with <see cref="VerifiedDevice"/>. Each call is its own command: never inside another one.</summary>
public interface ISellingDevices
{
    /// <param name="id">Chosen by the host, so its own row of the device is saved in the same transaction.</param>
    Task<Result> AddAsync(Guid id, string name, CancellationToken ct);

    /// <summary>Its sessions close at once.</summary>
    Task<Result> RevokeAsync(Guid id, CancellationToken ct);
}

/// <summary>TK-321: set by the Lite host as a request feature once the request's signature proved this device; Identity then takes it in place of
/// a client certificate or a device secret.</summary>
public sealed record VerifiedDevice(Guid Id);

/// <summary>TK-190: how many active registers reported their peripherals, and how many have a scale or a printer.</summary>
public sealed record PeripheralsSummary(int Reported, int WithScale, int WithPrinter);

public interface IDevicePeripherals
{
    Task<PeripheralsSummary> SummaryAsync(CancellationToken ct);
}
