using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Abastalia.Host.Cloud.Backups;
using Abastalia.Host.Cloud.Quotas;
using Abastalia.Sync.Contracts;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-238: the stores' encrypted backups, uploaded by the node and downloaded by the owner.</summary>
public sealed class StoreBackupTests(CloudApp app) : IClassFixture<CloudApp>
{
    /// <summary>What the store's BackupEncryption writes first; the rest is opaque to the cloud.</summary>
    public static byte[] Encrypted(string content = "cifrado") => Encoding.ASCII.GetBytes(StoreBackupEndpoints.FileMagic + content);

    public static Task<HttpResponseMessage> UploadAsync(StoreNode node, string fileName, byte[] bytes)
    {
        var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return node.Client.PostAsync($"/api/nodes/backups/{fileName}", body);
    }

    private static string Name(DateTime storeTime) => $"abastalia-{storeTime:yyyyMMdd-HHmmss}.abastalia-backup";

    [Fact]
    public async Task The_store_uploads_its_encrypted_backup_and_only_its_owners_list_and_download_it()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var name = Name(new DateTime(2026, 10, 14, 23, 30, 0));

        (await UploadAsync(node, name, Encrypted())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UploadAsync(node, name, Encrypted())).StatusCode.ShouldBe(HttpStatusCode.OK); // a retry: kept once, counted once
        (await app.InDbAsync(db => db.TenantUsage.SingleAsync(u => u.TenantId == owner.TenantId && u.Meter == Meter.CloudBackups))).Count.ShouldBe(1);

        var list = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/stores/{node.StoreId}/backups");
        var backup = list.EnumerateArray().Single();
        (backup.GetProperty("fileName").GetString(), backup.GetProperty("bytes").GetInt64()).ShouldBe((name, Encrypted().Length));
        backup.GetProperty("createdUtc").GetDateTime().ShouldBe(new DateTime(2026, 10, 15, 3, 30, 0)); // store time 23:30 = 03:30 UTC
        var id = backup.GetProperty("id").GetGuid();

        // R12 #9: a short-lived link the browser downloads by itself, without the session.
        var link = (await (await owner.Client.PostAsync($"/api/stores/{node.StoreId}/backups/{id}/link", null)).Json()).GetProperty("url").GetString()!;
        var download = await app.CreateClient().GetAsync(link);
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(Encrypted());
        download.Content.Headers.ContentDisposition!.FileName.ShouldBe(name);
        (await app.InDbAsync(db => db.StoreBackups.SingleAsync(b => b.Id == id))).LastDownloadedUtc.ShouldNotBeNull();
        (await (await app.CreateClient().GetAsync(link)).ErrorCode()).ShouldBe("Backup.LinkExpired"); // R13: one download per link
        app.Clock.Advance(TimeSpan.FromSeconds(1));
        var again = (await (await owner.Client.PostAsync($"/api/stores/{node.StoreId}/backups/{id}/link", null)).Json()).GetProperty("url").GetString()!;
        (await app.CreateClient().GetAsync(again)).StatusCode.ShouldBe(HttpStatusCode.OK); // a new link works

        // Another business, a ReadOnly member, no session, or the node itself: nothing.
        var stranger = await app.OwnerAsync();
        (await stranger.Client.GetAsync($"/api/stores/{node.StoreId}/backups")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.Client.PostAsync($"/api/stores/{node.StoreId}/backups/{id}/link", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var readOnly = await ReadOnlyMemberAsync(owner);
        (await readOnly.Client.GetAsync($"/api/stores/{node.StoreId}/backups")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await readOnly.Client.PostAsync($"/api/stores/{node.StoreId}/backups/{id}/link", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.CreateClient().GetAsync("/api/backups/download/inventado")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        app.Clock.Advance(StoreBackupEndpoints.LinkLifetime + TimeSpan.FromSeconds(1));
        (await (await app.CreateClient().GetAsync(link)).ErrorCode()).ShouldBe("Backup.LinkExpired");
        (await app.CreateClient().GetAsync($"/api/stores/{node.StoreId}/backups")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await node.Client.GetAsync($"/api/stores/{node.StoreId}/backups")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Only_a_signed_node_uploads_and_only_encrypted_backups_with_the_store_name()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var name = Name(new DateTime(2026, 10, 14, 23, 30, 0));
        var anonymous = new ByteArrayContent(Encrypted());
        (await app.CreateClient().PostAsync($"/api/nodes/backups/{name}", anonymous)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var plain = await UploadAsync(node, name, "SQLite format 3\0 los datos sin cifrar"u8.ToArray());
        plain.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await plain.ErrorCode()).ShouldBe("Backup.InvalidFile");
        foreach (var bad in new[] { "otro.zip", "abastalia-20261014-233000-antes-de-restaurar.abastalia-backup", "abastalia-2026-10-14.abastalia-backup" })
            (await (await UploadAsync(node, bad, Encrypted())).ErrorCode()).ShouldBe("Backup.InvalidName");
        (Directory.Exists(app.BackupFolder)
            && Directory.EnumerateFiles(app.BackupFolder, "*", SearchOption.AllDirectories).Any(f => f.Contains(node.StoreId.ToString("N"), StringComparison.Ordinal)))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task The_cloud_keeps_seven_daily_and_four_weekly_backups_per_store()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var other = await app.StoreWithNodeAsync(await app.OwnerAsync()); // another store's backups are never pruned by this one's
        (await UploadAsync(other, Name(new DateTime(2026, 9, 1, 23, 30, 0)), Encrypted())).EnsureSuccessStatusCode();

        // Every night from 1 Sep to 14 Oct (45 uploads: inside the month's 62), plus a manual one on 14 Oct in the afternoon.
        for (var day = new DateTime(2026, 9, 1, 23, 30, 0); day <= new DateTime(2026, 10, 14, 23, 30, 0); day = day.AddDays(1))
            (await UploadAsync(node, Name(day), Encrypted())).EnsureSuccessStatusCode();
        (await UploadAsync(node, Name(new DateTime(2026, 10, 14, 15, 0, 0)), Encrypted())).EnsureSuccessStatusCode();

        var kept = await app.InDbAsync(db => db.StoreBackups.Where(b => b.StoreId == node.StoreId).OrderByDescending(b => b.CreatedUtc).Select(b => b.FileName).ToListAsync());
        kept.ShouldBe([
            // the newest of each of the last 7 days (14 Oct's is the 23:30 one)
            Name(new DateTime(2026, 10, 14, 23, 30, 0)), Name(new DateTime(2026, 10, 13, 23, 30, 0)), Name(new DateTime(2026, 10, 12, 23, 30, 0)),
            Name(new DateTime(2026, 10, 11, 23, 30, 0)), Name(new DateTime(2026, 10, 10, 23, 30, 0)), Name(new DateTime(2026, 10, 9, 23, 30, 0)),
            Name(new DateTime(2026, 10, 8, 23, 30, 0)),
            // then the newest of each of the 4 weeks before (weeks start on Monday): 7 Oct (Wed, the week of 5 Oct), 4 Oct, 27 Sep, 20 Sep
            Name(new DateTime(2026, 10, 7, 23, 30, 0)), Name(new DateTime(2026, 10, 4, 23, 30, 0)), Name(new DateTime(2026, 9, 27, 23, 30, 0)),
            Name(new DateTime(2026, 9, 20, 23, 30, 0)),
        ]);
        var files = Directory.EnumerateFiles(app.BackupFolder, "*.abastalia-backup", SearchOption.AllDirectories)
            .Where(f => f.Contains(node.StoreId.ToString("N"), StringComparison.Ordinal)).Select(f => Path.GetFileName(f)!).Order().ToList();
        files.ShouldBe(kept.Order().ToList());
        (await app.InDbAsync(db => db.StoreBackups.CountAsync(b => b.StoreId == other.StoreId))).ShouldBe(1);
    }

    [Fact]
    public async Task A_restricted_license_or_a_spent_month_stops_the_upload()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var now = app.Clock.GetUtcNow().UtcDateTime;

        // The month's uploads are spent (Estándar: 62).
        await app.InDbAsync(async db =>
        {
            db.TenantUsage.Add(new TenantUsage { TenantId = owner.TenantId, Month = new DateOnly(2026, 10, 1), Meter = Meter.CloudBackups, Count = 62 });
            return await db.SaveChangesAsync();
        });
        var spent = await UploadAsync(node, Name(new DateTime(2026, 10, 14, 23, 30, 0)), Encrypted());
        spent.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await spent.ErrorCode()).ShouldBe(Quota.ExceededCode);

        var restricted = await app.StoreWithNodeAsync(await app.OwnerAsync());
        (await restricted.SendAsync(Events.Envelope(PlatformTests.Heartbeat(license: new NodeLicenseV1("Restricted", now.AddDays(-30))), now))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        var refused = await UploadAsync(restricted, Name(new DateTime(2026, 10, 14, 23, 30, 0)), Encrypted());
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ErrorCode()).ShouldBe("Backup.NotLicensed");
        (await app.InDbAsync(db => db.StoreBackups.CountAsync(b => b.StoreId == node.StoreId || b.StoreId == restricted.StoreId))).ShouldBe(0);
    }

    [Fact]
    public void Retention_keeps_the_newest_of_each_day_then_of_each_week()
    {
        DateTime At(int month, int day, int hour = 15) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);
        StoreBackupEndpoints.Expired<DateTime>([], d => d, 7, 4).ShouldBeEmpty();
        // Two on one day: the older goes. Fewer days than kept: nothing else.
        StoreBackupEndpoints.Expired([At(10, 14, 20), At(10, 14, 22), At(10, 13)], d => d, 7, 4).ShouldBe([At(10, 14, 20)]);
        // KeepDaily 1, KeepWeekly 1: the newest (Wed 14 Oct), and the newest before its day (13 Oct, the same week).
        StoreBackupEndpoints.Expired([At(10, 14), At(10, 13), At(10, 6), At(10, 5)], d => d, 1, 1).ShouldBe([At(10, 6), At(10, 5)]);
    }

    private async Task<Owner> ReadOnlyMemberAsync(Owner owner)
    {
        var email = $"lectura{Guid.NewGuid():N}@example.com";
        var link = (await (await owner.Client.PostAsJsonAsync("/api/members/invites", new { email, role = "ReadOnly" })).Json()).GetProperty("link").GetString()!;
        (await app.CreateClient().PostAsJsonAsync("/api/invites/register", new { token = link[(link.IndexOf('#') + 1)..], password = CloudApp.Password }))
            .EnsureSuccessStatusCode();
        return await app.SignInAsync(email);
    }
}

/// <summary>TK-238: the per-file size and the per-node hourly limit, on their own app (small values).</summary>
public sealed class StoreBackupLimitsApp : CloudApp
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("CloudBackup:MaxMegabytes", "1");
        builder.UseSetting("CloudBackup:UploadsPerHourPerNode", "2");
    }
}

public sealed class StoreBackupLimitTests(StoreBackupLimitsApp app) : IClassFixture<StoreBackupLimitsApp>
{
    [Fact]
    public async Task A_backup_over_the_size_limit_or_past_the_hourly_limit_is_refused()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var big = await StoreBackupTests.UploadAsync(node, "abastalia-20261013-233000.abastalia-backup",
            [.. StoreBackupTests.Encrypted(), .. new byte[1024 * 1024]]);
        big.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await big.ErrorCode()).ShouldBe("Backup.TooLarge");

        (await StoreBackupTests.UploadAsync(node, "abastalia-20261014-233000.abastalia-backup", StoreBackupTests.Encrypted())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoreBackupTests.UploadAsync(node, "abastalia-20261014-233500.abastalia-backup", StoreBackupTests.Encrypted())).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await StoreBackupTests.UploadAsync(node, "abastalia-20261014-234000.abastalia-backup", StoreBackupTests.Encrypted())).StatusCode
            .ShouldBe(HttpStatusCode.TooManyRequests); // 2 an hour per PC (R12 #1: the one too large never got that far)
    }
}
