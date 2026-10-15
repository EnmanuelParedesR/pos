using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Toki.BuildingBlocks.Security;

public static class TokiClaims
{
    public const string User = "sub";
    public const string Name = "name";
    public const string Device = "device";
    public const string Session = "session";
    public const string Permission = "permission";
}

/// <summary>Who is calling: the cashier's session on a paired device.</summary>
public interface IUserContext
{
    Guid UserId { get; }
    string Name { get; }
    Guid? DeviceId { get; }
    Guid? SessionId { get; }
    bool Has(string permission);

    /// <summary>Supervisor who approved the current command, if any. Recorded in the audit trail.</summary>
    Guid? SupervisorId { get; set; }
}

internal sealed class HttpUserContext(IHttpContextAccessor accessor) : IUserContext
{
    private ClaimsPrincipal User => accessor.HttpContext?.User ?? new ClaimsPrincipal();

    public Guid UserId => Guid.TryParse(User.FindFirstValue(TokiClaims.User), out var id) ? id : Guid.Empty;
    // "sistema" is a display name the store can see, so it stays in Spanish.
    public string Name => User.FindFirstValue(TokiClaims.Name) ?? "sistema";
    public Guid? DeviceId => Guid.TryParse(User.FindFirstValue(TokiClaims.Device), out var id) ? id : null;
    public Guid? SessionId => Guid.TryParse(User.FindFirstValue(TokiClaims.Session), out var id) ? id : null;
    public bool Has(string permission) => User.HasClaim(TokiClaims.Permission, permission);
    public Guid? SupervisorId { get; set; }
}
