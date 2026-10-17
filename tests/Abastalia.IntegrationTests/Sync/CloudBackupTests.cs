using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Settings.Contracts;
using Abastalia.Sync;

namespace Abastalia.IntegrationTests.Sync;

/// <summary>TK-238: the copy of the backups in the cloud and the heartbeat's DGII mode. Own app: it writes backup files and moves the fiscal mode.</summary>
public sealed class CloudBackupTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private sealed class NoCloudBackup(IFeatureService inner) : IFeatureService
    {
        public LicenseStatus Status => inner.Status;
        public DateTime? EndsUtc => inner.EndsUtc;
        public bool IsEnabled(Feature feature) => feature != Feature.CloudBackup && inner.IsEnabled(feature);
        public int LimitOf(Limit limit) => inner.LimitOf(limit);
    }

    private Task<(CloudBackupUploader.Outcome Outcome, string? FileName)> UploadAsync(bool licensed = true) => app.InScopeAsync(sp =>
        new CloudBackupUploader(sp.GetRequiredService<CloudClient>(), sp.GetRequiredService<ICloudBackups>(),
            licensed ? sp.GetRequiredService<IFeatureService>() : new NoCloudBackup(sp.GetRequiredService<IFeatureService>())).RunAsync(CancellationToken.None));

    private static object Schedule(bool cloud) => new { CopyTo = (string?)null, Time = "23:30", Keep = 14, Automatic = false, Cloud = cloud };

    [Fact]
    public async Task The_newest_scheduled_backup_goes_to_the_cloud_encrypted_once_when_turned_on_and_licensed()
    {
        var admin = app.Client(await app.AdminAsync());
        if (!(await app.StatusAsync()).GetProperty("connected").GetBoolean()) await app.ConnectAsync(); // the other test may have connected it
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase = "la chuleta de los viernes 2026" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var folder = app.Services.GetRequiredService<IConfiguration>()["Backup:Folder"]!;
        Directory.CreateDirectory(folder);
        // The bytes are whatever the backup holds: the store sends the file as it is on disk (already encrypted with the owner's passphrase).
        await File.WriteAllBytesAsync(Path.Combine(folder, "abastalia-20261013-233000.abastalia-backup"), "TOKIRSP2 viejo"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(folder, "abastalia-20261014-233000.abastalia-backup"), "TOKIRSP2 nuevo"u8.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(folder, "abastalia-20261014-235900-antes-de-restaurar.abastalia-backup"), "TOKIRSP2 restaurar"u8.ToArray());
        var uploads = () => app.Cloud.Requests.Where(r => r.Path.StartsWith(CloudBackupUploader.PathPrefix, StringComparison.Ordinal)).ToList();

        (await UploadAsync()).Outcome.ShouldBe(CloudBackupUploader.Outcome.Nothing); // off by default (Backup:Cloud)
        (await admin.PutAsJsonAsync("/api/backups/settings", Schedule(cloud: true))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await UploadAsync(licensed: false)).Outcome.ShouldBe(CloudBackupUploader.Outcome.Nothing); // the license feature CloudBackup
        uploads().ShouldBeEmpty();

        (await UploadAsync()).ShouldBe((CloudBackupUploader.Outcome.Uploaded, "abastalia-20261014-233000.abastalia-backup"));
        var sent = uploads().Single();
        sent.Path.ShouldBe(CloudBackupUploader.PathPrefix + "abastalia-20261014-233000.abastalia-backup");
        sent.Raw.ShouldBe("TOKIRSP2 nuevo"u8.ToArray());
        app.Cloud.SignedByNode(sent).ShouldBeTrue(); // the node's key signs the file's hash too

        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/backups/settings");
        settings.GetProperty("cloud").GetBoolean().ShouldBeTrue();
        settings.GetProperty("cloudLast").GetProperty("fileName").GetString().ShouldBe("abastalia-20261014-233000.abastalia-backup");
        settings.GetProperty("cloudProblem").ValueKind.ShouldBe(JsonValueKind.Null);
        (await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.CountAsync(a => a.Action == "settings.backup-cloud-copy"))).ShouldBe(1);

        (await UploadAsync()).Outcome.ShouldBe(CloudBackupUploader.Outcome.Nothing); // already there
        uploads().Count.ShouldBe(1);

        // The cloud refuses the next one (its month is spent): its message shows in Respaldos and the health check, and the backup is not marked.
        await File.WriteAllBytesAsync(Path.Combine(folder, "abastalia-20261015-233000.abastalia-backup"), "TOKIRSP2 otro"u8.ToArray());
        const string spent = "Este mes ya se usó todo lo incluido en su plan de respaldos copiados a la nube (62).";
        app.Cloud.Respond = path => path.StartsWith(CloudBackupUploader.PathPrefix, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = JsonContent.Create(new { code = "Quota.Exceeded", detail = spent }) }
            : null;
        try
        {
            (await UploadAsync()).Outcome.ShouldBe(CloudBackupUploader.Outcome.Refused);
            settings = await admin.GetFromJsonAsync<JsonElement>("/api/backups/settings");
            settings.GetProperty("cloudProblem").GetString().ShouldBe(spent);
            settings.GetProperty("cloudLast").GetProperty("fileName").GetString().ShouldBe("abastalia-20261014-233000.abastalia-backup");
            var health = await app.InScopeAsync(sp => sp.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "backup"));
            health.Entries["backup"].Description.ShouldBe(spent);
        }
        finally { app.Cloud.Respond = null; }

        // R12 #9: the hourly limit (a 429 with Retry-After) is tried again later, not refused until the next backup.
        app.Cloud.Respond = path =>
        {
            if (!path.StartsWith(CloudBackupUploader.PathPrefix, StringComparison.Ordinal)) return null;
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new ByteArrayContent([]) };
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return limited;
        };
        try { (await UploadAsync()).Outcome.ShouldBe(CloudBackupUploader.Outcome.Retry); }
        finally { app.Cloud.Respond = null; }

        // Turned off: nothing more goes up and the problem is gone.
        (await admin.PutAsJsonAsync("/api/backups/settings", Schedule(cloud: false))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await UploadAsync()).Outcome.ShouldBe(CloudBackupUploader.Outcome.Nothing);
        (await admin.GetFromJsonAsync<JsonElement>("/api/backups/settings")).GetProperty("cloudProblem").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task The_heartbeat_reports_the_DGII_mode_chosen_on_screen_over_appsettings()
    {
        await app.AdminAsync();
        if (!(await app.StatusAsync()).GetProperty("connected").GetBoolean()) await app.ConnectAsync(); // the other test may have connected it
        var mode = app.Services.GetRequiredService<FiscalMode>();
        async Task<string?> ReportedAsync()
        {
            (await app.InScopeAsync(sp => sp.GetRequiredService<ICommandHandler<SendHeartbeat.Command, Ok>>().Handle(new SendHeartbeat.Command(), CancellationToken.None)))
                .IsSuccess.ShouldBeTrue();
            var payload = await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox
                .Where(m => m.Type == typeof(Abastalia.Sync.Contracts.NodeHeartbeatV1).FullName).OrderByDescending(m => m.Sequence).Select(m => m.Payload).FirstAsync());
            return JsonDocument.Parse(payload).RootElement.GetProperty("features").GetProperty("fiscalProvider").GetString();
        }

        (await ReportedAsync()).ShouldBe("Simulated"); // appsettings (Fiscal:Provider) while nothing is saved
        mode.Set(FiscalEnvironment.CerteCF);
        try { (await ReportedAsync()).ShouldBe("CerteCF"); }
        finally { mode.Set(null); }
    }
}
