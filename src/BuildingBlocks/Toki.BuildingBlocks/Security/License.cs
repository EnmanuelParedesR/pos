using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Security;

/// <summary>Sellable functions switched on by the license (docs/funcionalidades-v1.md §5). <see cref="SupplierInvoiceReading"/> (TK-248) is the paid
/// add-on of invoice reading with AI: every plan reads a few a month without it; the cloud counts and enforces the month (docs/cuotas.md).</summary>
public enum Feature { ButcherModule, WholesaleModule, CloudBackup, AiAssistant, SupplierInvoiceReading }

public enum Limit { Registers }

/// <summary>Trial: no license installed, 14 days from the first sale with everything on (TK-196). Grace: expired less than 15 days ago
/// (offline stores keep everything). Restricted: the trial ended or the license expired longer ago: paid functions off and one register
/// besides the main PC, but selling, e-CF, exports and backups never stop and no data is lost (TK-220).</summary>
public enum LicenseStatus { Trial, Active, Grace, Restricted }

/// <summary>What the store's license allows (arquitectura-v1-vsa §9). Selling, fiscal and backup code must never ask it (an architecture test checks).</summary>
public interface IFeatureService
{
    LicenseStatus Status { get; }
    /// <summary>When the trial ends, or when the installed license expires; null while the trial has not started (no sale yet).</summary>
    DateTime? EndsUtc { get; }
    bool IsEnabled(Feature feature);
    int LimitOf(Limit limit);
    /// <summary>R11 B-Low8: the installed license as Toki signed it (JWS), for the cloud to verify; null without one.</summary>
    string? LicenseJws => null;
}

/// <summary>The license as the vendor signed it (docs/licencias.md). <see cref="Features"/> holds <see cref="Feature"/> names; <see cref="Limits"/> by <see cref="Limit"/> name.
/// The store verifies it; the owner's offline script and, since TK-249, the cloud sign it.</summary>
public sealed record LicenseData(string Store, string Plan, IReadOnlyList<string> Features, IReadOnlyDictionary<string, int> Limits, DateTime IssuedUtc, DateTime ExpiresUtc);

/// <summary>Compact JWS with ES256 (ECDSA P-256, SHA-256, signature r‖s). Only the vendor's private keys sign; the store has only the public keys,
/// so there is no license generator in the client (the old system shipped one).</summary>
public static class Jws
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static readonly Error Invalid = Error.Validation("License.Invalid", "La licencia no es válida o no la firmó Toki.");

    public static string Sign(LicenseData data, ECDsa privateKey)
    {
        var input = SigningInput(data);
        return Compose(input, privateKey.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256));
    }

    /// <summary><c>header.body</c>: what is signed. TK-249: the cloud hands its SHA-256 to Key Vault, which answers r‖s (<see cref="Compose"/>).</summary>
    public static string SigningInput(LicenseData data) =>
        $"{B64("""{"alg":"ES256","typ":"JWT"}"""u8)}.{B64(JsonSerializer.SerializeToUtf8Bytes(data, Options))}";

    public static string Compose(string signingInput, byte[] signature) => $"{signingInput}.{B64(signature)}";

    /// <summary>Valid when any of <paramref name="publicKeys"/> signed it (TK-249: the owner's offline key or the cloud's Key Vault key).</summary>
    public static Result<LicenseData> Verify(string jws, params IEnumerable<ECDsa> publicKeys)
    {
        var parts = jws.Trim().Split('.');
        if (parts.Length != 3) return Invalid;
        try
        {
            using var header = JsonDocument.Parse(Bytes(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "ES256") return Invalid;
            var input = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            var signature = Bytes(parts[2]);
            if (!publicKeys.Any(k => k.VerifyData(input, signature, HashAlgorithmName.SHA256))) return Invalid;
            return JsonSerializer.Deserialize<LicenseData>(Bytes(parts[1]), Options) is { } data ? data : Invalid;
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
