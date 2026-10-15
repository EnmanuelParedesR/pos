using System.Security.Cryptography;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.Settings;

public sealed record BackupFile(string FileName, long Bytes, DateTime DateUtc);

/// <summary>What the last run could not do besides the backup itself (today: the copy off the PC), for the health check.</summary>
internal sealed class BackupStatus
{
    public string? CopyError { get; set; }
}

/// <summary>Backs up the whole database, encrypts it with the owner's passphrase and keeps the newest ones here and in the off-PC copy, if set.
/// The plain backup is written to <c>backups\work</c> (SQL Server's service account gets write access there at install) and deleted once encrypted;
/// anything a crash left in that folder is deleted at the next run. The encrypted file only gets its final name when it is complete.</summary>
internal sealed class Backups(SettingsDbContext db, DatabaseOptions options, IDataProtectionProvider protection, IConfiguration config, TimeProvider clock,
    BackupStatus status, ILogger<Backups> log)
{
    public const string Extension = ".toki-backup";
    public static readonly Error NoPassphrase = Error.Conflict("Backup.NoPassphrase", "Configure la frase de los respaldos antes de respaldar.");
    public static readonly Error UnreadablePassphrase = Error.Conflict("Backup.UnreadablePassphrase",
        "Vuelva a escribir la frase de los respaldos: la clave guardada no se puede leer en esta PC.");

    public string Folder => config["Backup:Folder"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "backups");
    public string WorkFolder => Path.Combine(Folder, "work");
    private int Keep => config.GetValue("Backup:Keep", 14);

    public IDataProtector Protector => protection.CreateProtector("Toki.Backup.Key");

    /// <summary>The stored key, or why it cannot be used (no phrase yet, or the key ring of this PC cannot open it: restored on a new PC, other account).</summary>
    public async Task<Result<(byte[] Key, byte[] Salt)>> KeyAsync(CancellationToken ct)
    {
        var business = await db.Business.AsNoTracking().SingleAsync(ct);
        if (business.BackupSalt is null || business.BackupKey is null) return NoPassphrase;
        try
        {
            return (Protector.Unprotect(Convert.FromBase64String(business.BackupKey)), business.BackupSalt);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return UnreadablePassphrase;
        }
    }

    public async Task<Result<BackupFile>> CreateAsync(CancellationToken ct)
    {
        var key = await KeyAsync(ct);
        if (key.IsFailure) return key.Error!;

        Directory.CreateDirectory(WorkFolder);
        foreach (var leftover in Directory.EnumerateFiles(WorkFolder)) File.Delete(leftover); // a plain copy left by a crash never stays
        var now = clock.GetUtcNow().UtcDateTime;
        var name = $"toki-{now + BusinessDay.Offset:yyyyMMdd-HHmmss}";
        var plain = Path.Combine(WorkFolder, name + (options.Provider == DbProvider.SqlServer ? ".bak" : ".tmp"));
        var target = Path.Combine(Folder, name + Extension);
        try
        {
            db.Database.SetCommandTimeout(TimeSpan.FromHours(1)); // a few GB on a store PC's disk takes minutes
            if (options.Provider == DbProvider.SqlServer)
                await db.Database.ExecuteSqlAsync($"BACKUP DATABASE {db.Database.GetDbConnection().Database} TO DISK = {plain} WITH INIT, CHECKSUM", ct);
            else await db.Database.ExecuteSqlAsync($"VACUUM INTO {plain}", ct);

            await using (var input = File.OpenRead(plain))
            await using (var output = File.Create(target + ".partial"))
                await BackupEncryption.EncryptAsync(input, output, key.Value.Key, key.Value.Salt, ct);
            File.Move(target + ".partial", target);
        }
        finally
        {
            File.Delete(plain);
            File.Delete(target + ".partial");
        }

        Prune(Folder);
        status.CopyError = null;
        if (config["Backup:CopyTo"] is { Length: > 0 } copy)
        {
            // A full USB stick or an offline share must not stop the local backups.
            try
            {
                Directory.CreateDirectory(copy);
                Prune(copy, makeRoom: true);
                File.Copy(target, Path.Combine(copy, name + Extension), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                status.CopyError = $"No se pudo copiar el respaldo a {copy}: {e.Message}";
                log.LogError(e, "Could not copy the backup to {Copy}", copy);
            }
        }
        return new BackupFile(Path.GetFileName(target), new FileInfo(target).Length, now);
    }

    /// <summary>Encrypted backups, newest first.</summary>
    public IEnumerable<FileInfo> List() => List(Folder);

    private static IEnumerable<FileInfo> List(string folder) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles("*" + Extension).OrderByDescending(f => f.Name) : [];

    private void Prune(string folder, bool makeRoom = false)
    {
        foreach (var old in List(folder).Skip(makeRoom ? Keep - 1 : Keep)) old.Delete();
    }
}

/// <summary>Sets the passphrase that encrypts the backups. Only its derived key is stored, protected for this machine;
/// the owner keeps the phrase (restoring on another PC needs it).</summary>
internal static class SetBackupPassphrase
{
    public sealed record Command(string Passphrase) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Passphrase).NotEmpty().MinimumLength(12).MaximumLength(200).WithMessage("La frase debe tener al menos 12 caracteres.");
    }

    internal sealed class Handler(SettingsDbContext db, Backups backups, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var business = await db.Business.SingleAsync(ct);
            business.BackupSalt = BackupEncryption.NewSalt();
            business.BackupKey = Convert.ToBase64String(backups.Protector.Protect(BackupEncryption.DeriveKey(c.Passphrase, business.BackupSalt)));
            await audit.RecordAsync("settings.backup-passphrase", "Business", Guid.Empty, new { }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/backups/passphrase", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) => (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Backups");
            // The host's token, not the request's: a back office that gives up waiting must not cut a backup halfway.
            app.MapPost("/api/backups", async (Backups backups, IHostApplicationLifetime lifetime) =>
                    (await backups.CreateAsync(lifetime.ApplicationStopping)).ToHttp(r => Results.Created($"/api/backups/{r.FileName}", r)))
                .RequirePermission(Permissions.SettingsManage).WithTags("Backups");
            app.MapGet("/api/backups", (Backups backups) =>
                    Results.Ok(backups.List().Select(f => new BackupFile(f.Name, f.Length, f.LastWriteTimeUtc))))
                .RequirePermission(Permissions.SettingsManage).WithTags("Backups");
        }
    }
}

/// <summary>Backs up three minutes after every start when the newest backup is older than 20 hours (many store PCs are off at night),
/// and every night at <c>Backup:Time</c> (23:30 store time), unless <c>Backup:Automatic</c> is false.</summary>
internal sealed class NightlyBackup(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<NightlyBackup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Backup:Automatic", true)) return;
        var time = config.GetValue("Backup:Time", new TimeSpan(23, 30, 0));
        await Task.Delay(TimeSpan.FromMinutes(3), clock, stoppingToken);
        await BackUpAsync(onlyIfDue: true, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(NightlyAuditCheck.UntilNext(clock.GetUtcNow().UtcDateTime, time), clock, stoppingToken);
            await BackUpAsync(onlyIfDue: false, stoppingToken);
        }
    }

    /// <param name="onlyIfDue">At startup: only when the newest backup is older than 20 hours.</param>
    public async Task BackUpAsync(bool onlyIfDue, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var backups = scope.ServiceProvider.GetRequiredService<Backups>();
            if (onlyIfDue && backups.List().FirstOrDefault()?.LastWriteTimeUtc > clock.GetUtcNow().UtcDateTime.AddHours(-20)) return;
            var r = await backups.CreateAsync(ct);
            if (r.IsFailure) log.LogError("The backup was not made: {Reason}", r.Error!.Description);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "The backup failed");
        }
    }
}

/// <summary>Degraded when there is no usable passphrase, the newest backup is older than 36 hours, or the off-PC copy failed.</summary>
internal sealed class BackupHealthCheck(Backups backups, BackupStatus status, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (await backups.KeyAsync(cancellationToken) is { IsFailure: true } key) return HealthCheckResult.Degraded(key.Error!.Description);
        var newest = backups.List().FirstOrDefault()?.LastWriteTimeUtc;
        if (newest is null || newest < clock.GetUtcNow().UtcDateTime.AddHours(-36)) return HealthCheckResult.Degraded("El último respaldo tiene más de 36 horas.");
        return status.CopyError is { } error ? HealthCheckResult.Degraded(error) : HealthCheckResult.Healthy();
    }
}
