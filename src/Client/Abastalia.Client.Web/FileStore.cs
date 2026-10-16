using System.Text.Json;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.Web;

/// <summary>Development only: the device token in a JSON file under the user's profile.</summary>
// shortcut: plain file (the profile's ACL protects it); the store app (Abastalia.Client.Wpf) encrypts it with DPAPI.
internal sealed class FileStore(string file) : ILocalStore, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string?> ReadAsync(string key) => (await ReadAllAsync()).GetValueOrDefault(key);

    public async Task SaveAsync(string key, string? value)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAllAsync();
            if (value is null) all.Remove(key);
            else all[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(all));
        }
        finally { _lock.Release(); }
    }

    public void Dispose() => _lock.Dispose();

    private async Task<Dictionary<string, string>> ReadAllAsync() =>
        File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(file)) ?? [] : [];
}
