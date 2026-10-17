using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Local;

/// <summary>TK-091 (ADR-0009): over https every request needs a register's client certificate, except what comes before pairing.</summary>
internal static class DeviceCertificates
{
    /// <summary>The same code the API gives a revoked register: it forgets its pairing and asks for a new code.</summary>
    public static readonly Error Missing = Error.Unauthorized("Identity.DeviceNotPaired", "Este equipo no está emparejado con la tienda.");

    private static readonly string[] Optional = ["/health", "/api/identity/devices/pair", "/api/identity/setup"];

    public static bool IsOptional(PathString path) => Optional.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>TK-359: the largest body read without a client certificate (pairing, setup: the CSR is at most 4,000 characters). Larger is 413.</summary>
    public const long MaxAnonymousBody = 16 * 1024;

    /// <summary>What people compare when pairing: the first 64 bits of the SHA-256, as the register shows it ("AB12 CD34 EF56 7890").</summary>
    public static string Fingerprint(X509Certificate2 certificate)
    {
        var hex = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        return string.Join(' ', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }
}
