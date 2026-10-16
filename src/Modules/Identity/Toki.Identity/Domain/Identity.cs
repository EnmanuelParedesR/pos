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
    public static readonly Guid CashierWithOpeningId = new("0199e000-0000-7000-8000-000000000004");
    public static readonly Guid StockKeeperId = new("0199e000-0000-7000-8000-000000000005");
    public static readonly Guid AccountantId = new("0199e000-0000-7000-8000-000000000006");

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Permissions { get; set; } = [];

    /// <summary>What the user with this role may do: Administrador always everything, including permissions added after the database was created.</summary>
    public IReadOnlyList<string> Effective => Id == AdministratorId ? P.All : Permissions;

    /// <remarks>The roles other than Administrador are written out on purpose, not derived from <see cref="P.All"/>: the owner edits them, and a new
    /// permission must not become a migration that rewrites their roles in every store (R6 #9). Administrador always gets every permission anyway.
    /// costs.view (TK-201) is the owner's: Supervisor does not get it by default (the owner grants it in Roles if the manager should see margins).
    /// TK-216: Cajero no longer opens the register (Cajero con apertura does); Inventario and Contador are starting points the owner copies or edits.
    /// R11 A11: Supervisor buys but does not pay suppliers (payables.pay is the owner's); SecurityReviewIdentity changes only untouched seeded roles.</remarks>
    public static IEnumerable<Role> Defaults()
    {
        yield return new Role { Id = AdministratorId, Name = "Administrador", Permissions = [.. P.All] };
        yield return new Role
        {
            Id = SupervisorId, Name = "Supervisor",
            Permissions = [P.ProductsManage, P.PricesChange, P.InventoryView, P.InventoryAdjust, P.InventoryCount, P.InventoryTransfer,
                P.RegisterOpen, P.RegisterClose, P.RegisterCashIn, P.RegisterWithdraw, P.RegisterApproveClose, P.RegisterOpenDrawer,
                P.SalesCreate, P.SalesVoid, P.SalesReturn, P.SalesLargeDiscount, P.SalesPriceOverride, P.SalesRemoveLine,
                P.PurchasingManage, P.CustomersManage, P.CustomersQuickAdd, P.CreditCollect, P.CreditSell, P.CreditManage, P.CreditExceedLimit,
                P.ReportsView, P.ShiftsView, P.FiscalReports],
        };
        // R11 A9: the cashier adds a customer at the register (quick-add) but does not edit customers' credit or see the inventory screens.
        yield return new Role
        {
            Id = CashierId, Name = "Cajero",
            Permissions = [P.SalesCreate, P.RegisterClose, P.RegisterCashIn, P.CreditCollect, P.CustomersQuickAdd],
        };
        yield return new Role
        {
            Id = CashierWithOpeningId, Name = "Cajero con apertura",
            Permissions = [P.SalesCreate, P.RegisterOpen, P.RegisterClose, P.RegisterCashIn, P.CreditCollect, P.CustomersQuickAdd],
        };
        yield return new Role
        {
            Id = StockKeeperId, Name = "Inventario",
            Permissions = [P.ProductsManage, P.InventoryView, P.InventoryAdjust, P.InventoryCount, P.InventoryTransfer, P.PurchasingManage],
        };
        yield return new Role { Id = AccountantId, Name = "Contador", Permissions = [P.ReportsView, P.FiscalReports, P.CostsView, P.ShiftsView] };
    }
}

internal enum PinResult { Correct, Incorrect, Locked, Inactive }

internal sealed class User
{
    public const int AttemptsBeforeLockout = 5;
    /// <summary>R11 A4: wrong PINs are forgotten only after this long without another one; a right PIN does not wipe them.</summary>
    public static readonly TimeSpan FailureMemory = TimeSpan.FromHours(1);

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
    /// <summary>The last wrong PIN (R11 A4): <see cref="FailedAttempts"/> decays from here, not on the next right PIN.</summary>
    public DateTime? LastFailedUtc { get; set; }

    public void ChangePin(string pin, byte[] pepper)
    {
        PinHash = Pin.Hash(pin, pepper);
        FailedAttempts = 0;
        LockedUntilUtc = null;
        LastFailedUtc = null;
    }

    /// <summary>Checks the PIN with progressive lockout: 5 failures lock 5 minutes, each further failure doubles it (max 1 hour).
    /// R11 A4: the count only goes back to zero an hour after the last failure, so guessing between two right PINs still locks.
    /// A hash from before the pepper (R11 A3) is replaced by a peppered one on the first right PIN.</summary>
    public PinResult TryPin(string pin, DateTime nowUtc, byte[] pepper)
    {
        if (!IsActive) return PinResult.Inactive;
        if (LockedUntilUtc > nowUtc) return PinResult.Locked;
        if (LastFailedUtc + FailureMemory <= nowUtc) (FailedAttempts, LastFailedUtc) = (0, null);
        if (Pin.Verify(PinHash, pin, pepper) is var (ok, rehash) && ok)
        {
            LockedUntilUtc = null;
            if (rehash) PinHash = Pin.Hash(pin, pepper);
            return PinResult.Correct;
        }
        FailedAttempts++;
        LastFailedUtc = nowUtc;
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
    /// <summary>Hash of the device secret (X-Device). Registers paired with a certificate (TK-091) never receive theirs.</summary>
    public byte[] TokenHash { get; set; } = [];
    /// <summary>SHA-256 of the client certificate the store CA issued when it paired (TK-091); null for registers paired with a secret.
    /// Revoking the device denies the certificate too: it is only accepted while its device is not revoked.</summary>
    public string? CertificateThumbprint { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>TK-190: when the register last reported its scale and printer (kept on the PC itself); null if it never did.</summary>
    public DateTime? PeripheralsReportedUtc { get; set; }
    /// <summary>The scale's protocol (Cas, Toledo8217…); null without a scale. Never its port.</summary>
    public string? ScaleProtocol { get; set; }
    /// <summary>The receipt printer's paper width (58 or 80 mm); null without a printer. Never its name or port.</summary>
    public int? PrinterWidth { get; set; }
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
    /// <summary>R11 A4: the last request on this session (written at most once a minute). Null: none since it opened.</summary>
    public DateTime? LastSeenUtc { get; set; }
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
    /// <summary>R11 A1: SHA-256 of the summary the supervisor saw (amount, kind, lines). The command that uses it must have the same.</summary>
    public byte[] PayloadHash { get; set; } = [];
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }

    public static byte[] HashOf(string summary) => SHA256.HashData(Encoding.UTF8.GetBytes(summary));
}

/// <summary>PIN hashes. R11 A3: the PIN is first keyed with the store server's pepper (<see cref="PinPepper"/>, kept in the key folder, never in the
/// database or its backups), so a PinHash taken from a backup cannot be tried offline. "p1:{pepper id}:{hash}"; a hash without the prefix is from
/// before the pepper and is replaced at the next right PIN.</summary>
internal static class Pin
{
    private const string Prefix = "p1:";
    private static readonly PasswordHasher<object> Hasher = new();

    public static bool IsValid(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    public static string Hash(string pin, byte[] pepper) => $"{Prefix}{PepperId(pepper)}:{Hasher.HashPassword(null!, Keyed(pin, pepper))}";

    /// <returns>Whether the PIN is right, and whether its hash should be written again (old format or old algorithm).</returns>
    public static (bool Ok, bool Rehash) Verify(string hash, string pin, byte[] pepper)
    {
        if (!hash.StartsWith(Prefix, StringComparison.Ordinal))
            return (Hasher.VerifyHashedPassword(null!, hash, pin) != PasswordVerificationResult.Failed, true);
        var parts = hash.Split(':', 3);
        if (parts.Length != 3 || parts[1] != PepperId(pepper)) return (false, false); // peppered on another server: see IdentityRestoreGuard
        var r = Hasher.VerifyHashedPassword(null!, parts[2], Keyed(pin, pepper));
        return (r != PasswordVerificationResult.Failed, r == PasswordVerificationResult.SuccessRehashNeeded);
    }

    /// <summary>Whether this hash was made with another server's pepper (a backup restored on a new PC).</summary>
    public static bool IsForeign(string hash, byte[] pepper) =>
        hash.StartsWith(Prefix, StringComparison.Ordinal) && hash.Split(':', 3) is [_, var id, _] && id != PepperId(pepper);

    private static string Keyed(string pin, byte[] pepper) => Convert.ToBase64String(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(pin)));
    private static string PepperId(byte[] pepper) => Convert.ToHexString(SHA256.HashData(pepper), 0, 4);
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
    /// <summary>R11 A14: one answer for an unknown code, a wrong PIN and someone who may not approve this, so the answer never confirms a PIN.</summary>
    public static readonly Error CannotApprove = Error.Unauthorized("Identity.InvalidCredentials", "Código o PIN incorrecto, o esa persona no puede autorizar esto.");
    /// <summary>R11 A14: too many wrong PINs for one user from this register in a few minutes.</summary>
    public static readonly Error TooManyAttempts = Error.Unauthorized("Identity.TooManyAttempts", "Demasiados PIN incorrectos para esa persona desde esta caja. Espere unos minutos.");
    /// <summary>R11 A15: a PIN reset lets whoever types it sign in as that person.</summary>
    public static readonly Error PinResetAdministratorOnly = Error.Forbidden("Identity.PinResetAdministratorOnly",
        "Solo un administrador puede cambiar el PIN de otra persona.");
    public static readonly Error UnknownPermission = Error.Validation("Identity.UnknownPermission", "Uno de los permisos no existe.");
    public static readonly Error AdministratorOnly = Error.Forbidden("Identity.AdministratorOnly", "Solo un administrador puede dar o quitar el rol Administrador.");
    /// <summary>TK-220: a register beyond what the license allows (Restricted keeps the main PC and one register).</summary>
    public static readonly Error RegisterNotLicensed = Error.Forbidden("Identity.RegisterNotLicensed",
        "Toki está en modo Restringido: venden la PC principal y una caja. Para usar esta caja, compre la licencia en Configuración → Licencia. Sus datos están completos. " +
        "Si esta caja tiene un turno abierto, un supervisor lo cierra desde la PC principal en Turnos y cuadres.");
    public static readonly Error RoleInUse =Error.Conflict("Identity.RoleInUse", "Ese rol tiene usuarios. Cámbieles el rol antes de borrarlo.");

    /// <summary>TK-215: nobody gives a permission they do not have (a role with it, or a user with that role).</summary>
    public static Error PermissionsNotHeld(IEnumerable<string> missing) =>
        Error.Forbidden("Identity.PermissionsNotHeld", $"No puede dar permisos que usted no tiene: {string.Join(", ", missing)}.");
}
