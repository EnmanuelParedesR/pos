using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Toki.Configuracion;

/// <summary>Encrypted backup file: AES-256-GCM in 1 MiB chunks, with a key derived from the owner's passphrase (PBKDF2-SHA256).
/// The salt and iteration count travel in the header, so the passphrase alone opens a backup on any machine.
/// Each chunk says whether it is the last one, so a cut file is detected instead of restoring half a database.</summary>
public static class CifradoRespaldo
{
    public const int Iteraciones = 600_000;
    private const int Bloque = 1024 * 1024;
    private static readonly byte[] Firma = "TOKIRSP1"u8.ToArray();

    public static byte[] NuevaSal() => RandomNumberGenerator.GetBytes(16);

    public static byte[] DerivarClave(string frase, byte[] sal, int iteraciones = Iteraciones) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(frase), sal, iteraciones, HashAlgorithmName.SHA256, 32);

    public static async Task CifrarAsync(Stream entrada, Stream salida, byte[] clave, byte[] sal, CancellationToken ct)
    {
        var prefijo = RandomNumberGenerator.GetBytes(4);
        var encabezado = new byte[Firma.Length + 16 + 4 + 4];
        Firma.CopyTo(encabezado, 0);
        sal.CopyTo(encabezado, 8);
        BinaryPrimitives.WriteInt32BigEndian(encabezado.AsSpan(24), Iteraciones);
        prefijo.CopyTo(encabezado, 28);
        await salida.WriteAsync(encabezado, ct);

        using var aes = new AesGcm(clave, 16);
        var plano = new byte[Bloque];
        var siguiente = new byte[Bloque];
        var leidos = await LeerAsync(entrada, plano, ct);
        for (long n = 0; ; n++)
        {
            var proximos = await LeerAsync(entrada, siguiente, ct);
            var ultimo = proximos == 0;
            var cifrado = new byte[leidos];
            var tag = new byte[16];
            aes.Encrypt(Nonce(prefijo, n), plano.AsSpan(0, leidos), cifrado, tag, [ultimo ? (byte)1 : (byte)0]);
            var largo = new byte[5];
            BinaryPrimitives.WriteInt32BigEndian(largo, leidos);
            largo[4] = ultimo ? (byte)1 : (byte)0;
            await salida.WriteAsync(largo, ct);
            await salida.WriteAsync(cifrado, ct);
            await salida.WriteAsync(tag, ct);
            if (ultimo) return;
            (plano, siguiente, leidos) = (siguiente, plano, proximos);
        }
    }

    /// <exception cref="CryptographicException">Wrong passphrase, or the file was changed or cut.</exception>
    public static async Task DescifrarAsync(Stream entrada, Stream salida, string frase, CancellationToken ct)
    {
        var encabezado = new byte[32];
        if (await LeerAsync(entrada, encabezado, ct) != 32 || !encabezado.AsSpan(0, 8).SequenceEqual(Firma))
            throw new CryptographicException("No es un respaldo de Toki.");
        var clave = DerivarClave(frase, encabezado[8..24], BinaryPrimitives.ReadInt32BigEndian(encabezado.AsSpan(24)));
        var prefijo = encabezado[28..32];

        using var aes = new AesGcm(clave, 16);
        var largo = new byte[5];
        for (long n = 0; ; n++)
        {
            if (await LeerAsync(entrada, largo, ct) != 5) throw new CryptographicException("El respaldo está cortado.");
            var tam = BinaryPrimitives.ReadInt32BigEndian(largo);
            if (tam is < 0 or > Bloque) throw new CryptographicException("El respaldo está dañado.");
            var cifrado = new byte[tam];
            var tag = new byte[16];
            if (await LeerAsync(entrada, cifrado, ct) != tam || await LeerAsync(entrada, tag, ct) != 16) throw new CryptographicException("El respaldo está cortado.");
            var plano = new byte[tam];
            aes.Decrypt(Nonce(prefijo, n), cifrado, tag, plano, new[] { largo[4] });
            await salida.WriteAsync(plano, ct);
            if (largo[4] == 1) return;
        }
    }

    private static byte[] Nonce(byte[] prefijo, long n)
    {
        var nonce = new byte[12];
        prefijo.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), n);
        return nonce;
    }

    private static async Task<int> LeerAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length && await s.ReadAsync(buffer.AsMemory(total), ct) is var n and > 0) total += n;
        return total;
    }
}
