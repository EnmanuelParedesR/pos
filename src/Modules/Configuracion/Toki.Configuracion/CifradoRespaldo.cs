using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Toki.Configuracion;

/// <summary>Encrypted backup file: AES-256-GCM in 1 MiB chunks. The owner's passphrase gives a key (PBKDF2-SHA256, salt in the header);
/// each file draws 16 random bytes and derives its own key from it (HKDF), so nonces (the chunk number) never repeat across files.
/// The whole header and a last-chunk flag are authenticated with every chunk: an edited header, reordered chunks or a cut file are all detected.</summary>
public static class CifradoRespaldo
{
    public const int Iteraciones = 600_000;
    private const int Bloque = 1024 * 1024;
    private const int LargoEncabezado = 8 + 16 + 4 + 16;
    private static readonly byte[] Firma = "TOKIRSP2"u8.ToArray();

    public static byte[] NuevaSal() => RandomNumberGenerator.GetBytes(16);

    public static byte[] DerivarClave(string frase, byte[] sal, int iteraciones = Iteraciones) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(frase), sal, iteraciones, HashAlgorithmName.SHA256, 32);

    public static async Task CifrarAsync(Stream entrada, Stream salida, byte[] clave, byte[] sal, CancellationToken ct)
    {
        var encabezado = new byte[LargoEncabezado];
        Firma.CopyTo(encabezado, 0);
        sal.CopyTo(encabezado, 8);
        BinaryPrimitives.WriteInt32BigEndian(encabezado.AsSpan(24), Iteraciones);
        RandomNumberGenerator.Fill(encabezado.AsSpan(28, 16));
        await salida.WriteAsync(encabezado, ct);

        using var aes = new AesGcm(ClaveDelArchivo(clave, encabezado), 16);
        var plano = new byte[Bloque];
        var siguiente = new byte[Bloque];
        var leidos = await entrada.ReadAtLeastAsync(plano, Bloque, throwOnEndOfStream: false, ct);
        for (long n = 0; ; n++)
        {
            var proximos = await entrada.ReadAtLeastAsync(siguiente, Bloque, throwOnEndOfStream: false, ct);
            var ultimo = proximos == 0;
            var cifrado = new byte[leidos];
            var tag = new byte[16];
            aes.Encrypt(Nonce(n), plano.AsSpan(0, leidos), cifrado, tag, Asociados(encabezado, ultimo));
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
        var encabezado = new byte[LargoEncabezado];
        if (await entrada.ReadAtLeastAsync(encabezado, LargoEncabezado, throwOnEndOfStream: false, ct) != LargoEncabezado || !encabezado.AsSpan(0, 8).SequenceEqual(Firma))
            throw new CryptographicException("No es un respaldo de Toki.");
        var iteraciones = BinaryPrimitives.ReadInt32BigEndian(encabezado.AsSpan(24));
        // An edited count could make opening take hours; the header is checked by every chunk anyway.
        if (iteraciones is < 100_000 or > 10_000_000) throw new CryptographicException("El respaldo está dañado.");

        using var aes = new AesGcm(ClaveDelArchivo(DerivarClave(frase, encabezado[8..24], iteraciones), encabezado), 16);
        var largo = new byte[5];
        for (long n = 0; ; n++)
        {
            if (await entrada.ReadAtLeastAsync(largo, 5, throwOnEndOfStream: false, ct) != 5) throw new CryptographicException("El respaldo está cortado.");
            var tam = BinaryPrimitives.ReadInt32BigEndian(largo);
            if (tam is < 0 or > Bloque || largo[4] > 1) throw new CryptographicException("El respaldo está dañado.");
            var cifrado = new byte[tam];
            var tag = new byte[16];
            if (await entrada.ReadAtLeastAsync(cifrado, tam, throwOnEndOfStream: false, ct) != tam || await entrada.ReadAtLeastAsync(tag, 16, throwOnEndOfStream: false, ct) != 16)
                throw new CryptographicException("El respaldo está cortado.");
            var plano = new byte[tam];
            aes.Decrypt(Nonce(n), cifrado, tag, plano, Asociados(encabezado, largo[4] == 1));
            await salida.WriteAsync(plano, ct);
            if (largo[4] == 1) return;
        }
    }

    private static byte[] ClaveDelArchivo(byte[] clave, byte[] encabezado) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, clave, 32, salt: encabezado[28..44], info: "toki-respaldo"u8.ToArray());

    private static byte[] Nonce(long n)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), n);
        return nonce;
    }

    private static byte[] Asociados(byte[] encabezado, bool ultimo) => [.. encabezado, ultimo ? (byte)1 : (byte)0];
}
