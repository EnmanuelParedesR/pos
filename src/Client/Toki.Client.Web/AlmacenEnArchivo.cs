using System.Text.Json;
using Toki.Client.UI.Estado;

namespace Toki.Client.Web;

/// <summary>Development only: the device token in a JSON file under the user's profile.</summary>
// shortcut: plain file (the profile's ACL protects it); the store app (Toki.Client.Wpf) encrypts it with DPAPI.
internal sealed class AlmacenEnArchivo(string archivo) : IAlmacenLocal, IDisposable
{
    private readonly SemaphoreSlim _cerrojo = new(1, 1);

    public async Task<string?> LeerAsync(string clave) => (await LeerTodoAsync()).GetValueOrDefault(clave);

    public async Task GuardarAsync(string clave, string? valor)
    {
        await _cerrojo.WaitAsync();
        try
        {
            var todo = await LeerTodoAsync();
            if (valor is null) todo.Remove(clave);
            else todo[clave] = valor;
            Directory.CreateDirectory(Path.GetDirectoryName(archivo)!);
            await File.WriteAllTextAsync(archivo, JsonSerializer.Serialize(todo));
        }
        finally { _cerrojo.Release(); }
    }

    public void Dispose() => _cerrojo.Dispose();

    private async Task<Dictionary<string, string>> LeerTodoAsync() =>
        File.Exists(archivo) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(archivo)) ?? [] : [];
}
