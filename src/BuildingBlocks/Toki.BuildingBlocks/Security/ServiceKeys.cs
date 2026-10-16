using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace Toki.BuildingBlocks.Security;

/// <summary>R11 A3/A10: small secrets of this store server that must never be in the database or its backups (the PIN pepper, the audit key).
/// Each is a file in the key folder (<c>Toki:KeysFolder</c>, ProgramData\Toki\keys), encrypted with the key ring the Settings module keeps there
/// (DPAPI of this machine). Someone with only the database, or a backup, cannot recompute them.</summary>
/// <remarks>A backup restored on another PC finds other secrets here: see IdentityRestoreGuard (PINs) and <see cref="Persistence.AuditKey"/>.</remarks>
public sealed class ServiceKeys(IDataProtectionProvider protection, IConfiguration config)
{
    private readonly ConcurrentDictionary<string, byte[]> _cache = new();
    private readonly Lock _create = new();

    public string Folder => config["Toki:KeysFolder"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "keys");

    /// <summary>The secret, or null when this server has none yet.</summary>
    public byte[]? Find(string name)
    {
        if (_cache.TryGetValue(name, out var cached)) return cached;
        var file = PathOf(name);
        if (!File.Exists(file)) return null;
        var value = Protector(name).Unprotect(File.ReadAllBytes(file));
        return _cache[name] = value;
    }

    /// <summary>The secret, created by <paramref name="create"/> (32 random bytes by default) the first time.</summary>
    public byte[] GetOrCreate(string name, Func<byte[]>? create = null)
    {
        if (Find(name) is { } found) return found;
        lock (_create)
        {
            if (Find(name) is { } raced) return raced;
            var value = create?.Invoke() ?? RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Folder);
            var file = PathOf(name);
            File.WriteAllBytes(file + ".tmp", Protector(name).Protect(value));
            File.Move(file + ".tmp", file, overwrite: false);
            return _cache[name] = value;
        }
    }

    private string PathOf(string name) => Path.Combine(Folder, name + ".secret");
    private IDataProtector Protector(string name) => protection.CreateProtector("Toki.ServiceKeys." + name);
}
