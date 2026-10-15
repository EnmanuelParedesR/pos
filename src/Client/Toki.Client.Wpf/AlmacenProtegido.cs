using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Toki.Client.UI.Estado;

namespace Toki.Client.Wpf;

/// <summary>The device token encrypted with DPAPI for the Windows user, in %LOCALAPPDATA%\Toki\cliente.dat.
/// Another Windows user, or the file copied to another PC, cannot read it.</summary>
internal sealed class AlmacenProtegido : IAlmacenLocal
{
    private static readonly byte[] Entropia = "Toki.Cliente.v1"u8.ToArray();
    private readonly string _archivo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki", "cliente.dat");
    private readonly Lock _cerrojo = new();

    public Task<string?> LeerAsync(string clave)
    {
        lock (_cerrojo) return Task.FromResult(Leer().GetValueOrDefault(clave));
    }

    public Task GuardarAsync(string clave, string? valor)
    {
        lock (_cerrojo)
        {
            var datos = Leer();
            if (valor is null) datos.Remove(clave);
            else datos[clave] = valor;
            Directory.CreateDirectory(Path.GetDirectoryName(_archivo)!);
            File.WriteAllBytes(_archivo, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(datos), Entropia, DataProtectionScope.CurrentUser));
        }
        return Task.CompletedTask;
    }

    private Dictionary<string, string> Leer()
    {
        if (!File.Exists(_archivo)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_archivo), Entropia, DataProtectionScope.CurrentUser))) ?? [];
        }
        catch (CryptographicException)
        {
            return []; // another user's or another PC's file: this PC has to be paired again
        }
    }
}
