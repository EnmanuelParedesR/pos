using Toki.SharedKernel;

namespace Toki.Identity.Contracts;

/// <summary>A user was created or edited (or is in the snapshot sent when the store connects to the cloud), so the panel can show names
/// instead of ids. Never carries the PIN, the code or the permissions.</summary>
public sealed record UserProfileChangedV1(Guid UserId, string Name, bool Active) : IntegrationEvent(UserId);
