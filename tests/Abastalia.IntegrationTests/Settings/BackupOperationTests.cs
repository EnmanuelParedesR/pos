using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Settings;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>R6: how backups behave on real store PCs (off at night, full USB sticks, a lost key ring). Own app: it moves the clock.</summary>
public sealed class BackupOperationTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private const string Passphrase = "la chuleta de los viernes 2026";

    [Fact]
    public async Task The_format_authenticates_the_header_and_each_file_uses_its_own_key()
    {
        var data = RandomNumberGenerator.GetBytes(10_000);
        var salt = BackupEncryption.NewSalt();
        var key = BackupEncryption.DeriveKey(Passphrase, salt);
        async Task<byte[]> Encrypt()
        {
            using var output = new MemoryStream();
            await BackupEncryption.EncryptAsync(new MemoryStream(data), output, key, salt, CancellationToken.None);
            return output.ToArray();
        }
        var a = await Encrypt();
        (await Encrypt()).ShouldNotBe(a); // same data and phrase, different file key

        foreach (var position in new[] { 30, 40 }) // the random bytes of the header: not part of the key derivation input before, now authenticated
        {
            var changed = a.ToArray();
            changed[position] ^= 1;
            await Should.ThrowAsync<CryptographicException>(() => BackupEncryption.DecryptAsync(new MemoryStream(changed), Stream.Null, Passphrase, CancellationToken.None));
        }
        var iterations = a.ToArray();
        iterations[24] = 0x7F; // ~2 billion iterations would take hours: refused at once
        await Should.ThrowAsync<CryptographicException>(() => BackupEncryption.DecryptAsync(new MemoryStream(iterations), Stream.Null, Passphrase, CancellationToken.None));
    }

    [Fact]
    public async Task On_start_it_backs_up_if_due_a_failed_copy_blocks_nothing_and_an_unreadable_key_is_explained()
    {
        if (!TestSqlServer.BacksUpOnThisMachine) Assert.Skip("SQL Server writes the .bak inside the container; use Sqlite or ABASTALIA_TEST_SQLSERVER.");
        var admin = app.Client(await app.AdminAsync());
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase })).EnsureSuccessStatusCode();

        // A PC that is off at 23:30: the backup happens after it starts, once.
        var worker = app.Services.GetServices<IHostedService>().OfType<NightlyBackup>().Single();
        await worker.BackUpAsync(onlyIfDue: true, CancellationToken.None);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await worker.BackUpAsync(onlyIfDue: true, CancellationToken.None);
        var folder = app.Services.GetRequiredService<IConfiguration>()["Backup:Folder"]!;
        Directory.GetFiles(folder, "*" + Backups.Extension).Length.ShouldBe(1);
        Directory.GetFiles(Path.Combine(folder, "work")).ShouldBeEmpty(); // no plain copy left

        // The off-PC copy fails (here: its path is a file): the local backups go on and keep their retention.
        var notAFolder = Path.GetTempFileName();
        var other = TestSqlServer.BackupFolder();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Folder"] = other, ["Backup:CopyTo"] = notAFolder, ["Backup:Keep"] = "2",
        }).Build();
        var status = new BackupStatus();
        for (var i = 0; i < 3; i++)
        {
            app.Clock.Advance(TimeSpan.FromMinutes(1));
            var r = await app.InScopeAsync(sp => new Backups(sp.GetRequiredService<SettingsDbContext>(), sp.GetRequiredService<DatabaseOptions>(),
                sp.GetRequiredService<IDataProtectionProvider>(), config, app.Clock, status, NullLogger<Backups>.Instance).CreateAsync(CancellationToken.None));
            r.IsSuccess.ShouldBeTrue();
        }
        Directory.GetFiles(other, "*" + Backups.Extension).Length.ShouldBe(2);
        status.CopyError!.ShouldContain(notAFolder);

        // The key ring of this PC cannot open the stored key (restored on a new PC): a clear message, not a 500.
        await app.InScopeAsync(sp => sp.GetRequiredService<SettingsDbContext>().Business.ExecuteUpdateAsync(s => s.SetProperty(n => n.BackupKey, "AAAA")));
        (await (await admin.PostAsync("/api/backups", null)).ErrorCode()).ShouldBe("Backup.UnreadablePassphrase");
        (await app.HealthAsync()).ShouldBe("Degraded");
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase })).StatusCode.ShouldBe(HttpStatusCode.NoContent); // typing it again fixes it
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        (await admin.PostAsync("/api/backups", null)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_cancelled_backup_leaves_no_files()
    {
        var admin = app.Client(await app.AdminAsync());
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase })).EnsureSuccessStatusCode();
        var folder = app.Services.GetRequiredService<IConfiguration>()["Backup:Folder"]!;
        Directory.CreateDirectory(Path.Combine(folder, "work"));
        var before = Directory.GetFiles(folder).Length;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            app.InScopeAsync(sp => sp.GetRequiredService<Backups>().CreateAsync(cancelled.Token)));
        Directory.GetFiles(folder).Length.ShouldBe(before);
        Directory.GetFiles(folder, "*.partial").ShouldBeEmpty();
        Directory.GetFiles(Path.Combine(folder, "work")).ShouldBeEmpty();
    }
}
