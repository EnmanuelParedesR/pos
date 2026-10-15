using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Toki.Client.UI.State;

namespace Toki.Client.Wpf;

/// <summary>The device token encrypted with DPAPI for the Windows user, in %LOCALAPPDATA%\Toki\client.dat.
/// Another Windows user, or the file copied to another PC, cannot read it.</summary>
internal sealed class ProtectedStore : ILocalStore
{
    // Not a secret (the repo is public): it only keeps this blob apart from other DPAPI data. The protection is the Windows user.
    private static readonly byte[] Entropy = "Toki.Client.v1"u8.ToArray();
    private readonly string _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki", "client.dat");
    private readonly Lock _lock = new();

    public Task<string?> ReadAsync(string key)
    {
        lock (_lock) return Task.FromResult(Read().GetValueOrDefault(key));
    }

    public Task SaveAsync(string key, string? value)
    {
        lock (_lock)
        {
            var data = Read();
            if (value is null) data.Remove(key);
            else data[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            // Written aside and then moved over: a power cut mid-write never leaves a file DPAPI cannot read (that would unpair the PC).
            var temp = _file + ".tmp";
            File.WriteAllBytes(temp, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(data), Entropy, DataProtectionScope.CurrentUser));
            File.Move(temp, _file, overwrite: true);
        }
        return Task.CompletedTask;
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_file)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser))) ?? [];
        }
        catch (CryptographicException)
        {
            return []; // another user's or another PC's file: this PC has to be paired again
        }
    }
}
