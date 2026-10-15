using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using P = Toki.BuildingBlocks.Security.Permissions;
using Toki.SharedKernel;

namespace Toki.Identity.Domain;

internal sealed class Role
{
    public static readonly Guid AdministratorId = new("0199e000-0000-7000-8000-000000000001");
    public static readonly Guid SupervisorId = new("0199e000-0000-7000-8000-000000000002");
    public static readonly Guid CashierId = new("0199e000-0000-7000-8000-000000000003");

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Permissions { get; set; } = [];

    /// <remarks>Supervisor and Cajero are written out on purpose, not derived from <see cref="P.All"/>: the owner edits them, and a new
    /// permission must not become a migration that rewrites their roles in every store (R6 #9). Administrador always gets every permission anyway.</remarks>
    public static IEnumerable<Role> Defaults()
    {
        yield return new Role { Id = AdministratorId, Name = "Administrador", Permissions = [.. P.All] };
        yield return new Role
        {
            Id = SupervisorId, Name = "Supervisor",
            Permissions = [P.ProductsManage, P.PricesChange, P.InventoryView, P.InventoryAdjust, P.InventoryCount, P.InventoryTransfer,
                P.RegisterOperate, P.RegisterWithdraw, P.RegisterApproveClose, P.RegisterOpenDrawer,
                P.SalesCreate, P.SalesVoid, P.SalesReturn, P.SalesLargeDiscount, P.SalesPriceOverride, P.SalesRemoveLine,
                P.PurchasingManage, P.CustomersManage, P.CreditManage, P.CreditExceedLimit, P.ReportsView],
        };
        yield return new Role
        {
            Id = CashierId, Name = "Cajero",
            Permissions = [P.SalesCreate, P.RegisterOperate, P.InventoryView, P.CustomersManage],
        };
    }
}

internal enum PinResult { Correct, Incorrect, Locked, Inactive }

internal sealed class User
{
    public const int AttemptsBeforeLockout = 5;

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Short code typed or picked at the register (for example "01"). Stored upper-case so both databases compare it the same way.</summary>
    public string Code { get; set; } = "";

    public static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();
    public string PinHash { get; set; } = "";
    public Guid RoleId { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntilUtc { get; set; }

    public void ChangePin(string pin)
    {
        PinHash = Pin.Hash(pin);
        FailedAttempts = 0;
        LockedUntilUtc = null;
    }

    /// <summary>Checks the PIN with progressive lockout: 5 failures lock 5 minutes, each further failure doubles it (max 1 hour).</summary>
    public PinResult TryPin(string pin, DateTime nowUtc)
    {
        if (!IsActive) return PinResult.Inactive;
        if (LockedUntilUtc > nowUtc) return PinResult.Locked;
        if (Pin.Verify(PinHash, pin))
        {
            FailedAttempts = 0;
            LockedUntilUtc = null;
            return PinResult.Correct;
        }
        FailedAttempts++;
        if (FailedAttempts >= AttemptsBeforeLockout)
        {
            var minutes = Math.Min(5 * Math.Pow(2, FailedAttempts - AttemptsBeforeLockout), 60);
            LockedUntilUtc = nowUtc.AddMinutes(minutes);
        }
        return PinResult.Incorrect;
    }
}

internal sealed class Device
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] TokenHash { get; set; } = [];
    public bool IsRevoked { get; set; }
    public DateTime CreatedUtc { get; set; }
}

internal sealed class PairingCode
{
    public Guid Id { get; set; }
    public string DeviceName { get; set; } = "";
    public byte[] CodeHash { get; set; } = [];
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

internal sealed class Session
{
    public static readonly TimeSpan Duration = TimeSpan.FromHours(12);

    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }
}

internal sealed class SupervisorApproval
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    public Guid Id { get; set; }
    public Guid SupervisorId { get; set; }
    public Guid RequesterId { get; set; }
    public Guid DeviceId { get; set; }
    public string Permission { get; set; } = "";
    public Guid ResourceId { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

internal static class Pin
{
    private static readonly PasswordHasher<object> Hasher = new();

    public static bool IsValid(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);
    public static string Hash(string pin) => Hasher.HashPassword(null!, pin);
    public static bool Verify(string hash, string pin) =>
        Hasher.VerifyHashedPassword(null!, hash, pin) != PasswordVerificationResult.Failed;
}

/// <summary>Random bearer secrets. Only their SHA-256 is stored.</summary>
internal static class Token
{
    public static string New() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string ShortCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class IdentityErrors
{
    public static readonly Error InvalidCredentials = Error.Unauthorized("Identity.InvalidCredentials", "Código o PIN incorrecto.");
    public static readonly Error UserLocked = Error.Unauthorized("Identity.UserLocked", "Demasiados intentos. Espere unos minutos o pida a un supervisor que cambie el PIN.");
    public static readonly Error DeviceNotPaired = Error.Unauthorized("Identity.DeviceNotPaired", "Este equipo no está emparejado con la tienda.");
    public static readonly Error AlreadySetUp = Error.Conflict("Identity.AlreadySetUp", "La tienda ya fue instalada.");
    public static readonly Error DuplicateCode = Error.Conflict("Identity.DuplicateCode", "Ya existe un usuario con ese código.");
    public static readonly Error UserNotFound = Error.NotFound("Identity.UserNotFound", "El usuario no existe.");
    public static readonly Error RoleNotFound = Error.NotFound("Identity.RoleNotFound", "El rol no existe.");
    public static readonly Error DeviceNotFound = Error.NotFound("Identity.DeviceNotFound", "El dispositivo no existe.");
    public static readonly Error InvalidPairingCode = Error.Unauthorized("Identity.InvalidPairingCode", "El código de emparejamiento no es válido o ya venció.");
    public static readonly Error SupervisorLacksPermission = Error.Forbidden("Identity.SupervisorLacksPermission", "Ese usuario no puede autorizar esta acción.");
    public static readonly Error InvalidApproval = Error.Forbidden("Identity.InvalidApproval", "La autorización del supervisor no es válida, ya se usó o venció.");
    public static readonly Error UnknownPermission = Error.Validation("Identity.UnknownPermission", "Uno de los permisos no existe.");
    public static readonly Error AdministratorOnly = Error.Forbidden("Identity.AdministratorOnly", "Solo un administrador puede dar o quitar el rol Administrador.");
}
