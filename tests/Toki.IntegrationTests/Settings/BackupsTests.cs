using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Settings;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Settings;

/// <summary>TK-092. Own app: it writes backup files.</summary>
public sealed class BackupsTests(TokiApp app) : IClassFixture<TokiApp>
{
    private const string Passphrase = "la chuleta de los viernes 2026";

    [Fact]
    public async Task Encryption_opens_only_with_the_passphrase_and_detects_a_truncated_file()
    {
        var data = RandomNumberGenerator.GetBytes(2_500_000); // three chunks
        var salt = BackupEncryption.NewSalt();
        using var encrypted = new MemoryStream();
        await BackupEncryption.EncryptAsync(new MemoryStream(data), encrypted, BackupEncryption.DeriveKey(Passphrase, salt), salt, CancellationToken.None);

        using var plain = new MemoryStream();
        await BackupEncryption.DecryptAsync(new MemoryStream(encrypted.ToArray()), plain, Passphrase, CancellationToken.None);
        plain.ToArray().ShouldBe(data);

        await Should.ThrowAsync<CryptographicException>(() => BackupEncryption.DecryptAsync(new MemoryStream(encrypted.ToArray()), Stream.Null, "otra frase cualquiera", CancellationToken.None));
        var truncated = encrypted.ToArray()[..^(1024 * 1024)];
        await Should.ThrowAsync<CryptographicException>(() => BackupEncryption.DecryptAsync(new MemoryStream(truncated), Stream.Null, Passphrase, CancellationToken.None));
    }

    [Fact]
    public async Task An_encrypted_backup_restores_with_the_passphrase()
    {
        if (TestSqlServer.IsEnabled) Assert.Skip("SQL Server writes the .bak on its own machine (the container); this test uses Sqlite.");
        var admin = app.Client(await app.AdminAsync());
        (await (await admin.PostAsync("/api/backups", null)).ErrorCode()).ShouldBe("Backup.NoPassphrase");
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase = "corta" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Carnicería del respaldo", DiscountCapPercent = 10m })).EnsureSuccessStatusCode();

        var made = await (await admin.PostAsync("/api/backups", null)).Json();
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/backups");
        list.EnumerateArray().Select(r => r.GetProperty("fileName").GetString()).ShouldContain(made.GetProperty("fileName").GetString());

        var folder = app.Services.GetRequiredService<IConfiguration>()["Backup:Folder"]!;
        Directory.GetFiles(folder).ShouldAllBe(f => f.EndsWith(Backups.Extension)); // no plain copy left behind
        var restored = Path.Combine(folder, "restored.db");
        await using (var input = File.OpenRead(Path.Combine(folder, made.GetProperty("fileName").GetString()!)))
        await using (var output = File.Create(restored))
            await BackupEncryption.DecryptAsync(input, output, Passphrase, CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={restored};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Business";
        (await command.ExecuteScalarAsync()).ShouldBe("Carnicería del respaldo");
    }
}
