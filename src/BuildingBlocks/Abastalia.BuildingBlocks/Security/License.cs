using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Abastalia.SharedKernel;

namespace Abastalia.BuildingBlocks.Security;

/// <summary>Sellable functions switched on by the license (docs/funcionalidades-v1.md §5). <see cref="SupplierInvoiceReading"/> (TK-248) is the paid
/// add-on of invoice reading with AI: every plan reads a few a month without it; the cloud counts and enforces the month (docs/cuotas.md).</summary>
public enum Feature { ButcherModule, WholesaleModule, CloudBackup, AiAssistant, SupplierInvoiceReading }

/// <summary><see cref="Cashiers"/> (TK-277): active users who can sell or open a shift, Administradores included except one (the owner, R12).</summary>
public enum Limit { Registers, Cashiers }

/// <summary>Trial: no license installed, 14 days from the first sale with everything on (TK-196). Grace: expired less than 15 days ago
/// (offline stores keep everything). Restricted: the trial ended or the license expired longer ago: paid functions off and one register
/// besides the main PC (TK-220). Blocked (TK-279): the license expired more than its <see cref="LicenseData.BlockAfterDays"/> ago (30 by default,
/// also after the trial) or Abastalia blocked the business: no new sale, shift or e-CF; closing a shift, sending the e-CF already issued,
/// reports, exports, backups and installing a license go on, and no data is lost.</summary>
public enum LicenseStatus { Trial, Active, Grace, Restricted, Blocked }

/// <summary>What the store's license allows (arquitectura-v1-vsa §9). Selling, fiscal and backup code must never ask it (an architecture test checks);
/// TK-279: their endpoints only carry the Blocked gate (<c>RequireUsableLicense</c>).</summary>
public interface IFeatureService
{
    LicenseStatus Status { get; }
    /// <summary>When the trial ends, or when the installed license expires; null while the trial has not started (no sale yet).</summary>
    DateTime? EndsUtc { get; }
    bool IsEnabled(Feature feature);
    int LimitOf(Limit limit);
    /// <summary>R11 B-Low8: the installed license as Abastalia signed it (JWS), for the cloud to verify; null without one.</summary>
    string? LicenseJws => null;
    /// <summary>R12: what a Blocked store answers, by cause (expired, trial over, suspended by Abastalia).</summary>
    Error BlockedError => Jws.Blocked;
}

/// <summary>The license as the vendor signed it (docs/licencias.md). <see cref="Features"/> holds <see cref="Feature"/> names; <see cref="Limits"/> by <see cref="Limit"/> name.
/// The store verifies it; the owner's offline script and, since TK-249, the cloud sign it.</summary>
/// <param name="TenantId">R11 B-Low8 (TK-268): the cloud business it was issued for. The cloud only lets it unlock the paid extras of that business;
/// the store itself ignores it (it may not be connected to the cloud).</param>
/// <param name="Blocked">TK-279: Abastalia blocked the business (platform console): the store is Blocked while this is its installed license.</param>
/// <param name="BlockAfterDays">TK-279: days after <paramref name="ExpiresUtc"/> until Blocked; null = 30. Only a signed value sets it, never a local setting.</param>
/// <param name="Rnc">R12: the business's RNC; the store refuses a license for another RNC. Null (older licenses): accepted until they expire.</param>
public sealed record LicenseData(string Store, string Plan, IReadOnlyList<string> Features, IReadOnlyDictionary<string, int> Limits, DateTime IssuedUtc, DateTime ExpiresUtc,
    Guid? TenantId = null, bool Blocked = false, int? BlockAfterDays = null, string? Rnc = null);

/// <summary>R12 (security B3): the cloud's signed word that a business's store trial lasts <paramref name="Days"/> (TK-276, a referral). Signed with the
/// license keys under its own JWS <c>typ</c> (<see cref="Jws.TrialType"/>), so it never passes for a license nor a license for it.</summary>
public sealed record TrialGrant(Guid TenantId, int Days, DateTime IssuedUtc);

/// <summary>TK-308: the cloud's signed time at a store's license pull, for that pull's fresh <paramref name="Nonce"/> (an old answer cannot be replayed
/// to turn the clock back). Signed with the license keys under <see cref="Jws.TimeType"/>; the store corrects its license clock with it as with the DGII's.</summary>
public sealed record ServerTime(Guid TenantId, string Nonce, DateTime Utc);

/// <summary>Compact JWS with ES256 (ECDSA P-256, SHA-256, signature r‖s). Only the vendor's private keys sign; the store has only the public keys,
/// so there is no license generator in the client (the old system shipped one).</summary>
public static class Jws
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static readonly Error Invalid = Error.Validation("License.Invalid", "La licencia no es válida o no la firmó Abastalia.");

    /// <summary>TK-279: what a Blocked store answers to a new sale, shift or e-CF.</summary>
    public static readonly Error Blocked = Error.Forbidden("License.Blocked",
        "Su licencia está vencida: no se pueden hacer ventas nuevas. Pague su plan o escríbale a Abastalia y active la licencia en Configuración → Licencia. Sus datos, reportes y respaldos siguen disponibles.");

    /// <summary>R12: Blocked 30 days after the free trial ended (same code, its own words).</summary>
    public static readonly Error TrialOver = Error.Forbidden("License.Blocked",
        "La prueba gratis terminó: no se pueden hacer ventas nuevas. Compre su plan y active la licencia en Configuración → Licencia. Sus datos, reportes y respaldos siguen disponibles.");

    /// <summary>R12: Blocked because Abastalia suspended the business from its console.</summary>
    public static readonly Error Suspended = Error.Forbidden("License.Blocked",
        "Abastalia suspendió el servicio de este negocio: no se pueden hacer ventas nuevas. Escríbale a Abastalia para resolverlo. Sus datos, reportes y respaldos siguen disponibles.");

    /// <summary>The JWS <c>typ</c> of a license, of a <see cref="TrialGrant"/> and of a <see cref="ServerTime"/>.</summary>
    public const string LicenseType = "JWT", TrialType = "abastalia-trial", TimeType = "abastalia-time";

    public static string Sign(LicenseData data, ECDsa privateKey) => Sign(data, privateKey, LicenseType);

    public static string Sign<T>(T data, ECDsa privateKey, string type)
    {
        var input = SigningInput(data, type);
        return Compose(input, privateKey.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256));
    }

    /// <summary><c>header.body</c>: what is signed. TK-249: the cloud hands its SHA-256 to Key Vault, which answers r‖s (<see cref="Compose"/>).</summary>
    public static string SigningInput(LicenseData data) => SigningInput(data, LicenseType);

    public static string SigningInput<T>(T data, string type) =>
        $"{B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = type }))}.{B64(JsonSerializer.SerializeToUtf8Bytes(data, Options))}";

    public static string Compose(string signingInput, byte[] signature) => $"{signingInput}.{B64(signature)}";

    /// <summary>Valid when any of <paramref name="publicKeys"/> signed it (TK-249: the owner's offline key or the cloud's Key Vault key).</summary>
    public static Result<LicenseData> Verify(string jws, params IEnumerable<ECDsa> publicKeys) => Verify<LicenseData>(jws, LicenseType, publicKeys);

    /// <summary>Valid when its header's <c>typ</c> is <paramref name="type"/> (a license without one counts as a license) and any of the keys signed it.</summary>
    public static Result<T> Verify<T>(string jws, string type, IEnumerable<ECDsa> publicKeys)
    {
        var parts = jws.Trim().Split('.');
        if (parts.Length != 3) return Invalid;
        try
        {
            using var header = JsonDocument.Parse(Bytes(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "ES256") return Invalid;
            if ((header.RootElement.TryGetProperty("typ", out var typ) ? typ.GetString() : LicenseType) != type) return Invalid;
            var input = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            var signature = Bytes(parts[2]);
            if (!publicKeys.Any(k => k.VerifyData(input, signature, HashAlgorithmName.SHA256))) return Invalid;
            return JsonSerializer.Deserialize<T>(Bytes(parts[1]), Options) is { } data ? data : Invalid;
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Invalid;
        }
    }

    private static string B64(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Bytes(string b64url)
    {
        var s = b64url.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
