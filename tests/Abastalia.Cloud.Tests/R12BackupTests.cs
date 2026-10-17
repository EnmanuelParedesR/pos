using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Abastalia.Host.Cloud.Quotas;

namespace Abastalia.Cloud.Tests;

/// <summary>R12 (backlog/reviews/R12-nube-arreglos.md): the node gate and the cloud backups.</summary>
public sealed class R12BackupTests(CloudApp app) : IClassFixture<CloudApp>
{
    private static string Name(DateTime storeTime) => $"abastalia-{storeTime:yyyyMMdd-HHmmss}.abastalia-backup";

    [Fact]
    public async Task R12_1_the_gate_refuses_a_body_over_the_limit_or_compressed_before_the_node_is_checked()
    {
        // No node at all: the length alone refuses it, so nothing was buffered or hashed.
        var huge = new ByteArrayContent(StoreBackupTests.Encrypted());
        huge.Headers.ContentLength = 2L * 1024 * 1024 * 1024;
        var tooLarge = await app.CreateClient().PostAsync($"/api/nodes/backups/{Name(new DateTime(2026, 10, 14, 23, 30, 0))}", huge);
        tooLarge.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await tooLarge.ErrorCode()).ShouldBe("Backup.TooLarge");

        var gzip = new ByteArrayContent(StoreBackupTests.Encrypted());
        gzip.Headers.ContentEncoding.Add("gzip");
        var compressed = await app.CreateClient().PostAsync($"/api/nodes/backups/{Name(new DateTime(2026, 10, 14, 23, 30, 0))}", gzip);
        compressed.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task R12_1_the_signed_hash_header_is_required_on_a_backup_and_must_match_the_body()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var name = Name(new DateTime(2026, 10, 14, 23, 30, 0));

        // An older store (no header): small calls still work, a backup does not.
        var legacy = new HttpRequestMessage(HttpMethod.Post, "/api/nodes/updates") { Content = JsonContent.Create(new { version = "1.0.0" }) };
        legacy.Options.Set(CloudApp.LegacySignature, true);
        (await node.Client.SendAsync(legacy)).StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        var legacyBackup = new HttpRequestMessage(HttpMethod.Post, $"/api/nodes/backups/{name}") { Content = new ByteArrayContent(StoreBackupTests.Encrypted()) };
        legacyBackup.Options.Set(CloudApp.LegacySignature, true);
        (await node.Client.SendAsync(legacyBackup)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The header says one hash, the body is another: refused.
        var swapped = new HttpRequestMessage(HttpMethod.Post, $"/api/nodes/backups/{name}") { Content = new ByteArrayContent(StoreBackupTests.Encrypted()) };
        swapped.Options.Set(CloudApp.SwapBodyAfterSigning, StoreBackupTests.Encrypted("otro contenido"));
        (await node.Client.SendAsync(swapped)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.InDbAsync(db => db.StoreBackups.CountAsync(b => b.StoreId == node.StoreId))).ShouldBe(0);
    }

    [Fact]
    public async Task R12_6_a_name_with_a_trailing_newline_or_from_the_future_is_refused()
    {
        var node = await app.StoreWithNodeAsync(await app.OwnerAsync());
        var now = app.Clock.GetUtcNow().UtcDateTime;
        foreach (var bad in new[] { Uri.EscapeDataString(Name(new DateTime(2026, 10, 14, 23, 30, 0)) + "\n"), Name(now.AddDays(3)) })
            (await (await StoreBackupTests.UploadAsync(node, bad, StoreBackupTests.Encrypted())).ErrorCode()).ShouldBe("Backup.InvalidName");
        (await StoreBackupTests.UploadAsync(node, Name(now.AddHours(-4)), StoreBackupTests.Encrypted())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task R12_8_a_backup_whose_file_is_gone_answers_not_found_and_leaves_the_list()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        (await StoreBackupTests.UploadAsync(node, Name(new DateTime(2026, 10, 14, 23, 30, 0)), StoreBackupTests.Encrypted())).EnsureSuccessStatusCode();
        var row = await app.InDbAsync(db => db.StoreBackups.SingleAsync(b => b.StoreId == node.StoreId));
        File.Delete(Path.Combine(app.BackupFolder, row.BlobKey.Replace('/', Path.DirectorySeparatorChar))); // what the lifecycle rule does

        var link = (await (await owner.Client.PostAsync($"/api/stores/{node.StoreId}/backups/{row.Id}/link", null)).Json()).GetProperty("url").GetString()!;
        var gone = await app.CreateClient().GetAsync(link);
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await gone.ErrorCode()).ShouldBe("Backup.NotFound");
        (await app.InDbAsync(db => db.StoreBackups.AnyAsync(b => b.Id == row.Id))).ShouldBeFalse();
    }

    [Fact]
    public async Task R12_9_a_failed_store_gives_the_months_upload_back()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        Directory.CreateDirectory(app.BackupFolder);
        await File.WriteAllTextAsync(Path.Combine(app.BackupFolder, owner.TenantId.ToString("N")), "no es una carpeta"); // the file cannot be written

        (await StoreBackupTests.UploadAsync(node, Name(new DateTime(2026, 10, 14, 23, 30, 0)), StoreBackupTests.Encrypted())).StatusCode
            .ShouldBe(HttpStatusCode.InternalServerError);
        (await app.InDbAsync(db => db.TenantUsage.Where(u => u.TenantId == owner.TenantId && u.Meter == Meter.CloudBackups).Select(u => u.Count).SingleAsync()))
            .ShouldBe(0);
        (await app.InDbAsync(db => db.StoreBackups.CountAsync(b => b.StoreId == node.StoreId))).ShouldBe(0);
    }
}

/// <summary>R12 #1 and #6: the per-IP gate and the hourly download cap, with small values.</summary>
public sealed class R12LimitsApp : CloudApp
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("CloudBackup:DownloadsPerHourPerUser", "1");
    }
}

public sealed class R12LimitTests(R12LimitsApp app) : IClassFixture<R12LimitsApp>
{
    [Fact]
    public async Task R12_1_node_calls_are_limited_per_ip_before_they_are_authenticated()
    {
        using var strict = app.WithWebHostBuilder(b => b.UseSetting("Nodes:RequestsPerMinutePerIp", "2"));
        var client = strict.CreateClient();
        for (var i = 0; i < 2; i++) (await client.PostAsync("/api/nodes/revoke", new StringContent(""))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var limited = await client.PostAsync("/api/nodes/revoke", new StringContent(""));
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK); // anything else is not a node call
    }

    [Fact]
    public async Task R12_6_download_links_are_capped_per_hour()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        (await StoreBackupTests.UploadAsync(node, "abastalia-20261014-233000.abastalia-backup", StoreBackupTests.Encrypted())).EnsureSuccessStatusCode();
        var id = await app.InDbAsync(db => db.StoreBackups.Where(b => b.StoreId == node.StoreId).Select(b => b.Id).SingleAsync());
        (await owner.Client.PostAsync($"/api/stores/{node.StoreId}/backups/{id}/link", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.Client.PostAsync($"/api/stores/{node.StoreId}/backups/{id}/link", null)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
