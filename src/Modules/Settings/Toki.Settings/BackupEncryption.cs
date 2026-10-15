using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Toki.Settings;

/// <summary>Encrypted backup file: AES-256-GCM in 1 MiB chunks. The owner's passphrase gives a key (PBKDF2-SHA256, salt in the header);
/// each file draws 16 random bytes and derives its own key from it (HKDF), so nonces (the chunk number) never repeat across files.
/// The whole header and a last-chunk flag are authenticated with every chunk: an edited header, reordered chunks or a cut file are all detected.
/// The magic bytes and the HKDF info string are part of the file format: they stay as they were.</summary>
public static class BackupEncryption
{
    public const int Iterations = 600_000;
    private const int ChunkSize = 1024 * 1024;
    private const int HeaderLength = 8 + 16 + 4 + 16;
    private static readonly byte[] Magic = "TOKIRSP2"u8.ToArray();

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(16);

    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations = Iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static async Task EncryptAsync(Stream input, Stream output, byte[] key, byte[] salt, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        salt.CopyTo(header, 8);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), Iterations);
        RandomNumberGenerator.Fill(header.AsSpan(28, 16));
        await output.WriteAsync(header, ct);

        using var aes = new AesGcm(FileKey(key, header), 16);
        var plain = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var read = await input.ReadAtLeastAsync(plain, ChunkSize, throwOnEndOfStream: false, ct);
        for (long n = 0; ; n++)
        {
            var nextRead = await input.ReadAtLeastAsync(next, ChunkSize, throwOnEndOfStream: false, ct);
            var last = nextRead == 0;
            var cipher = new byte[read];
            var tag = new byte[16];
            aes.Encrypt(Nonce(n), plain.AsSpan(0, read), cipher, tag, AssociatedData(header, last));
            var length = new byte[5];
            BinaryPrimitives.WriteInt32BigEndian(length, read);
            length[4] = last ? (byte)1 : (byte)0;
            await output.WriteAsync(length, ct);
            await output.WriteAsync(cipher, ct);
            await output.WriteAsync(tag, ct);
            if (last) return;
            (plain, next, read) = (next, plain, nextRead);
        }
    }

    /// <exception cref="CryptographicException">Wrong passphrase, or the file was changed or cut.</exception>
    public static async Task DecryptAsync(Stream input, Stream output, string passphrase, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        if (await input.ReadAtLeastAsync(header, HeaderLength, throwOnEndOfStream: false, ct) != HeaderLength || !header.AsSpan(0, 8).SequenceEqual(Magic))
            throw new CryptographicException("No es un respaldo de Toki.");
        var iterations = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24));
        // An edited count could make opening take hours; the header is checked by every chunk anyway.
        if (iterations is < 100_000 or > 10_000_000) throw new CryptographicException("El respaldo está dañado.");

        using var aes = new AesGcm(FileKey(DeriveKey(passphrase, header[8..24], iterations), header), 16);
        var length = new byte[5];
        for (long n = 0; ; n++)
        {
            if (await input.ReadAtLeastAsync(length, 5, throwOnEndOfStream: false, ct) != 5) throw new CryptographicException("El respaldo está cortado.");
            var size = BinaryPrimitives.ReadInt32BigEndian(length);
            if (size is < 0 or > ChunkSize || length[4] > 1) throw new CryptographicException("El respaldo está dañado.");
            var cipher = new byte[size];
            var tag = new byte[16];
            if (await input.ReadAtLeastAsync(cipher, size, throwOnEndOfStream: false, ct) != size || await input.ReadAtLeastAsync(tag, 16, throwOnEndOfStream: false, ct) != 16)
                throw new CryptographicException("El respaldo está cortado.");
            var plain = new byte[size];
            aes.Decrypt(Nonce(n), cipher, tag, plain, AssociatedData(header, length[4] == 1));
            await output.WriteAsync(plain, ct);
            if (length[4] == 1) return;
        }
    }

    private static byte[] FileKey(byte[] key, byte[] header) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt: header[28..44], info: "toki-respaldo"u8.ToArray());

    private static byte[] Nonce(long n)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), n);
        return nonce;
    }

    private static byte[] AssociatedData(byte[] header, bool last) => [.. header, last ? (byte)1 : (byte)0];
}
