using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

/// <summary>TK-213. Own app: it replaces the database.</summary>
public sealed class RestoreBackupTests(TokiApp app) : IClassFixture<TokiApp>
{
    private const string Passphrase = "la chuleta de los viernes 2026";

    [Fact]
    public async Task The_back_office_restores_a_backup_only_with_the_registers_closed_and_the_right_passphrase()
    {
        if (TestSqlServer.IsEnabled) Assert.Skip("SQL Server writes the .bak on its own machine (the container); this test uses Sqlite.");
        var admin = app.Client(await app.AdminAsync());
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Antes", DiscountCapPercent = 10m })).EnsureSuccessStatusCode();
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        var file = (await (await admin.PostAsync("/api/backups", null)).Json()).GetProperty("fileName").GetString()!;
        (await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Después", DiscountCapPercent = 10m })).EnsureSuccessStatusCode();

        (await (await admin.PostAsJsonAsync("/api/backups/no-existe.toki-backup/restore", new { Passphrase })).ErrorCode()).ShouldBe("Backup.NotFound");
        (await (await admin.PostAsJsonAsync("/api/backups/..%5Cotro.toki-backup/restore", new { Passphrase })).ErrorCode()).ShouldBe("Backup.NotFound");
        (await (await admin.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase = "otra frase cualquiera" })).ErrorCode()).ShouldBe("Backup.WrongPassphrase");
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        var restored = await admin.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase });
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = (await restored.Json()).GetProperty("backupBefore").GetString()!;
        before.ShouldContain("-antes-de-restaurar");

        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/business")).GetProperty("name").GetString().ShouldBe("Antes");
        (await app.VerifyAuditAsync()).ShouldBeNull(); // the restore is recorded on the restored chain
        var folder = app.Services.GetRequiredService<IConfiguration>()["Backup:Folder"]!;
        Directory.GetFiles(Path.Combine(folder, "work")).ShouldBeEmpty();

        // The data before the restore comes back from the backup made first.
        (await admin.PostAsJsonAsync($"/api/backups/{before}/restore", new { Passphrase })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/business")).GetProperty("name").GetString().ShouldBe("Después");

        // A register selling right now: refused, nothing replaced.
        await app.CashierWithShiftAsync();
        (await (await admin.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase })).ErrorCode()).ShouldBe("Backup.RegistersOpen");
    }
}
