using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Identity.Persistence;
using Abastalia.Settings;
using Abastalia.IntegrationTests.Fiscal;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>TK-092. Own app: it writes backup files.</summary>
public sealed class BackupsTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
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

/// <summary>R12 (audit C): one backup or restore at a time. Own app: it writes backup files.</summary>
public sealed class BackupGateTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private const string Passphrase = "la chuleta de los viernes 2026";

    [Fact]
    public async Task R12_two_backups_at_once_take_turns_and_both_are_complete()
    {
        var admin = app.Client(await app.AdminAsync());
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase })).EnsureSuccessStatusCode();
        // Each clears the shared work folder when it starts: overlapping, one would delete the other's plain copy.
        var made = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => app.InScopeAsync(sp => sp.GetRequiredService<Backups>().CreateAsync(CancellationToken.None))));
        made.ShouldAllBe(r => r.IsSuccess);
        made.Select(r => r.Value.FileName).Distinct().Count().ShouldBe(3);
    }
}

/// <summary>TK-213. Own app: it replaces the database.</summary>
public sealed class RestoreBackupTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private const string Passphrase = "la chuleta de los viernes 2026";

    [Fact]
    public async Task The_back_office_restores_a_backup_only_with_the_registers_closed_and_the_right_passphrase()
    {
        // R12: also on a SQL Server of this PC (SQL Express), which runs RestoreSqlServerAsync; a container writes the .bak inside itself.
        if (!TestSqlServer.BacksUpOnThisMachine) Assert.Skip("SQL Server writes the .bak on its own machine (the container).");
        var admin = app.Client(await app.AdminAsync());
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Antes", DiscountCapPercent = 10m })).EnsureSuccessStatusCode();
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        var file = (await (await admin.PostAsync("/api/backups", null)).Json()).GetProperty("fileName").GetString()!;
        (await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Después", DiscountCapPercent = 10m })).EnsureSuccessStatusCode();

        (await (await admin.PostAsJsonAsync("/api/backups/no-existe.abastalia-backup/restore", new { Passphrase })).ErrorCode()).ShouldBe("Backup.NotFound");
        (await (await admin.PostAsJsonAsync("/api/backups/..%5Cotro.abastalia-backup/restore", new { Passphrase })).ErrorCode()).ShouldBe("Backup.NotFound");
        (await (await admin.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase = "otra frase cualquiera" })).ErrorCode()).ShouldBe("Backup.WrongPassphrase");
        var folder = app.Services.GetRequiredService<IConfiguration>()["Backup:Folder"]!;
        string[] BeforeRestores() => Directory.GetFiles(folder, "*-antes-de-restaurar" + Backups.Extension);
        BeforeRestores().ShouldBeEmpty(); // R12: a wrong phrase makes no backup of the current data first
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        var restored = await admin.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase });
        restored.StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = (await restored.Json()).GetProperty("backupBefore").GetString()!;
        before.ShouldContain("-antes-de-restaurar");

        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/business")).GetProperty("name").GetString().ShouldBe("Antes");
        (await app.VerifyAuditAsync()).ShouldBeNull(); // the restore is recorded on the restored chain
        Directory.GetFiles(Path.Combine(folder, "work")).ShouldBeEmpty();

        // The data before the restore comes back from the backup made first.
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        (await admin.PostAsJsonAsync($"/api/backups/{before}/restore", new { Passphrase })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/business")).GetProperty("name").GetString().ShouldBe("Después");

        // R12: the daily history keeps its count; the backups made before restoring are not counted nor pruned.
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.BackupKeep, 1)));
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        (await admin.PostAsync("/api/backups", null)).EnsureSuccessStatusCode();
        BeforeRestores().Length.ShouldBe(2);
        (Directory.GetFiles(folder, "*" + Backups.Extension).Length - BeforeRestores().Length).ShouldBe(1);

        // A register selling right now: refused, nothing replaced.
        await app.CashierWithShiftAsync();
        (await (await admin.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase })).ErrorCode()).ShouldBe("Backup.RegistersOpen");
    }
}

/// <summary>R9 #3: a restore never rewinds what already left the store. Own app: it replaces the database.</summary>
public sealed class RestoreGuardTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private const string Passphrase = "la chuleta de los viernes 2026";

    private async Task<(Guid Id, string Token)> PairAsync(HttpClient admin, string name)
    {
        var code = await (await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = name })).Json();
        var paired = await (await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code.GetProperty("code").GetString() })).Json();
        return (paired.GetProperty("deviceId").GetGuid(), paired.GetProperty("token").GetString()!);
    }

    private Task<long> NextAsync(EcfType type) => app.InScopeAsync(sp =>
        sp.GetRequiredService<FiscalDbContext>().Sequences.Where(s => s.Type == type).Select(s => s.Next).SingleAsync());

    private async Task<bool> CanSignInAsync(string device) =>
        (await app.Anonymous(device).PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin = AbastaliaApp.AdminPin })).IsSuccessStatusCode;

    /// <summary>The guards on their own (both databases): the restored data is played by rewinding the rows by hand.</summary>
    [Fact]
    public async Task The_guards_put_back_the_numbers_used_the_revocations_and_the_registers_paired_since()
    {
        var admin = app.Client(await app.AdminAsync());
        await app.LoadSequencesAsync();
        var revoked = await PairAsync(admin, "Caja revocada");
        var newer = await PairAsync(admin, "Caja nueva");
        (await admin.PostAsync($"/api/identity/devices/{revoked.Id}/revoke", null)).EnsureSuccessStatusCode();
        await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Sequences.Where(s => s.Type == EcfType.Consumer32)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Next, 40)));

        await app.InScopeAsync(async sp =>
        {
            var snapshots = new List<RestoreSnapshot>();
            foreach (var guard in sp.GetServices<IRestoreGuard>()) snapshots.Add(await guard.CaptureAsync(CancellationToken.None));
            // The backup: before the sales, the revocation and the newer register.
            var fiscal = sp.GetRequiredService<FiscalDbContext>();
            await fiscal.Sequences.Where(s => s.Type == EcfType.Consumer32).ExecuteUpdateAsync(s => s.SetProperty(x => x.Next, 3));
            var identity = sp.GetRequiredService<IdentityDbContext>();
            await identity.Devices.Where(d => d.Id == revoked.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, false));
            await identity.Devices.Where(d => d.Id == newer.Id).ExecuteDeleteAsync();
            foreach (var s in snapshots) await s.ReapplyAsync(CancellationToken.None);
            return 0;
        });

        (await NextAsync(EcfType.Consumer32)).ShouldBe(40);
        (await CanSignInAsync(revoked.Token)).ShouldBeFalse();
        (await CanSignInAsync(newer.Token)).ShouldBeTrue();
    }

}

/// <summary>R9 #3 through the endpoint. Own app: it replaces the database, and the license counts its registers.</summary>
public sealed class RestoreGuardEndpointTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private const string Passphrase = "la chuleta de los viernes 2026";

    private async Task<(Guid Id, string Token)> PairAsync(HttpClient admin, string name)
    {
        var code = await (await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = name })).Json();
        var paired = await (await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code.GetProperty("code").GetString() })).Json();
        return (paired.GetProperty("deviceId").GetGuid(), paired.GetProperty("token").GetString()!);
    }

    private Task<long> NextAsync(EcfType type) => app.InScopeAsync(sp =>
        sp.GetRequiredService<FiscalDbContext>().Sequences.Where(s => s.Type == type).Select(s => s.Next).SingleAsync());

    private async Task<bool> CanSignInAsync(string device) =>
        (await app.Anonymous(device).PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin = AbastaliaApp.AdminPin })).IsSuccessStatusCode;

    [Fact]
    public async Task A_restore_keeps_the_sequences_and_revocations_and_asks_before_losing_signed_documents()
    {
        if (TestSqlServer.IsEnabled) Assert.Skip("SQL Server writes the .bak on its own machine (the container); this test uses Sqlite.");
        var admin = app.Client(await app.AdminAsync());
        await app.LoadSequencesAsync();
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase })).EnsureSuccessStatusCode();
        var revoked = await PairAsync(admin, "Caja revocada");
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        var file = (await (await admin.PostAsync("/api/backups", null)).Json()).GetProperty("fileName").GetString()!;
        app.Clock.Advance(TimeSpan.FromSeconds(1));

        // After the backup: e-CF signed (the DGII has them), a register revoked, another paired, a range loaded.
        await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Sequences.Where(s => s.Type == EcfType.Consumer32)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Next, 40)));
        await app.PendingAsync("E320000000039", app.Clock.GetUtcNow().UtcDateTime.AddHours(72));
        (await admin.PostAsync($"/api/identity/devices/{revoked.Id}/revoke", null)).EnsureSuccessStatusCode();
        var newer = await PairAsync(admin, "Caja nueva");
        (await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = 33, From = 1, To = 100, ExpiresOn = "2030-12-31" })).EnsureSuccessStatusCode();

        var refused = await admin.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase });
        (await refused.ErrorCode()).ShouldBe("Backup.OlderThanLastDocument");
        (await admin.PostAsJsonAsync($"/api/backups/{file}/restore", new { Passphrase, ConfirmOlder = true })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await NextAsync(EcfType.Consumer32)).ShouldBe(40);
        (await NextAsync(EcfType.DebitNote33)).ShouldBe(1);
        (await CanSignInAsync(revoked.Token)).ShouldBeFalse();
        (await CanSignInAsync(newer.Token)).ShouldBeTrue();
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }
}
