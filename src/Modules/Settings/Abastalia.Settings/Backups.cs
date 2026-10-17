using System.Security.Cryptography;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.CashRegister.Contracts;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Settings;

public sealed record BackupFile(string FileName, long Bytes, DateTime DateUtc);

/// <summary>What the last run could not do besides the backup itself (today: the copy off the PC), for the health check.
/// <see cref="Changed"/> wakes <see cref="NightlyBackup"/> when the schedule is saved (TK-238).</summary>
#pragma warning disable CA1001 // a singleton for the app's lifetime; each replaced source is already cancelled
internal sealed class BackupStatus
#pragma warning restore CA1001
{
    private CancellationTokenSource _changed = new();

    public string? CopyError { get; set; }
    /// <summary>Why the last upload to the cloud did not happen (the Sync module reports it through <see cref="CloudBackups"/>).</summary>
    public string? CloudError { get; set; }
    public CancellationToken Changed => _changed.Token;
    public void ScheduleChanged() => Interlocked.Exchange(ref _changed, new CancellationTokenSource()).Cancel();
}

/// <summary>TK-238: when and where the backups go. Each value saved on screen (<see cref="Business"/>) wins over appsettings
/// (<c>Backup:CopyTo</c>, <c>Backup:Time</c> 23:30, <c>Backup:Keep</c> 14, <c>Backup:Automatic</c> true, <c>Backup:Cloud</c> false).
/// An empty copy folder saved on screen means no copy. <see cref="Cloud"/>: the Sync module also uploads the newest backup, encrypted as it is (<see cref="CloudBackups"/>).</summary>
internal sealed record BackupSchedule(string? CopyTo, TimeSpan Time, int Keep, bool Automatic, bool Cloud = false)
{
    public static BackupSchedule Of(Business b, IConfiguration config) => new(
        (b.BackupCopyTo ?? config["Backup:CopyTo"]) is { Length: > 0 } copy ? copy : null,
        b.BackupTime ?? config.GetValue("Backup:Time", new TimeSpan(23, 30, 0)),
        b.BackupKeep ?? config.GetValue("Backup:Keep", 14),
        b.BackupAutomatic ?? config.GetValue("Backup:Automatic", true),
        b.BackupCloud ?? config.GetValue("Backup:Cloud", false));
}

/// <summary>Backs up the whole database, encrypts it with the owner's passphrase and keeps the newest ones here and in the off-PC copy, if set.
/// The plain backup is written to <c>backups\work</c> (SQL Server's service account gets write access there at install) and deleted once encrypted;
/// anything a crash left in that folder is deleted at the next run. The encrypted file only gets its final name when it is complete.</summary>
internal sealed class Backups(SettingsDbContext db, DatabaseOptions options, IDataProtectionProvider protection, IConfiguration config, TimeProvider clock,
    BackupStatus status, ILogger<Backups> log)
{
    public const string Extension = ".abastalia-backup";
    public const string FilePrefix = "abastalia-";
    public static readonly Error NoPassphrase = Error.Conflict("Backup.NoPassphrase", "Configure la frase de los respaldos antes de respaldar.");
    public static readonly Error UnreadablePassphrase = Error.Conflict("Backup.UnreadablePassphrase",
        "Vuelva a escribir la frase de los respaldos: la clave guardada no se puede leer en esta PC.");
    public static readonly Error NotFound = Error.NotFound("Backup.NotFound", "Ese respaldo ya no está en esta PC.");
    public static readonly Error WrongPassphrase = Error.Validation("Backup.WrongPassphrase", "La frase no abre este respaldo, o el archivo está dañado.");
    public static readonly Error OtherDatabase = Error.Validation("Backup.OtherDatabase", "Este respaldo es de otro tipo de instalación de Abastalia y no se puede restaurar aquí.");

    public string Folder => config["Backup:Folder"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Abastalia", "backups");
    public string WorkFolder => Path.Combine(Folder, "work");

    public async Task<BackupSchedule> ScheduleAsync(CancellationToken ct) => BackupSchedule.Of(await db.Business.AsNoTracking().SingleAsync(ct), config);

    public IDataProtector Protector => protection.CreateProtector("Abastalia.Backup.Key");

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

    /// <param name="label">Added to the name of a backup made for a reason ("antes-de-restaurar"); such a backup prunes nothing and is not copied off the PC.</param>
    public async Task<Result<BackupFile>> CreateAsync(CancellationToken ct, string? label = null)
    {
        await Gate.WaitAsync(ct);
        try
        {
            ClearWork();
            return await CreateCoreAsync(label, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>R12: one backup or restore at a time on this server (the nightly one, the button, a restore's own backup first): they share
    /// <see cref="WorkFolder"/>, which each one clears.</summary>
    private static readonly SemaphoreSlim Gate = new(1);

    private void ClearWork()
    {
        Directory.CreateDirectory(WorkFolder);
        foreach (var leftover in Directory.EnumerateFiles(WorkFolder)) File.Delete(leftover); // a plain copy left by a crash never stays
    }

    private async Task<Result<BackupFile>> CreateCoreAsync(string? label, CancellationToken ct)
    {
        var key = await KeyAsync(ct);
        if (key.IsFailure) return key.Error!;

        Directory.CreateDirectory(WorkFolder);
        var now = clock.GetUtcNow().UtcDateTime;
        var name = $"{FilePrefix}{now + BusinessDay.Offset:yyyyMMdd-HHmmss}" + (label is null ? "" : "-" + label);
        if (File.Exists(Path.Combine(Folder, name + Extension))) name += $"-{Guid.NewGuid().ToString()[..4]}"; // two in the same second
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
        if (label is not null) return new BackupFile(Path.GetFileName(target), new FileInfo(target).Length, now);

        var schedule = await ScheduleAsync(ct);
        Prune(Folder, schedule.Keep);
        status.CopyError = null;
        if (schedule.CopyTo is { } copy)
        {
            // A full USB stick or an offline share must not stop the local backups.
            try
            {
                Directory.CreateDirectory(copy);
                Prune(copy, schedule.Keep, makeRoom: true);
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

    /// <summary>When a backup of this folder was made: from its name (store time), else the file's date.</summary>
    public DateTime CreatedUtc(string fileName) =>
        fileName.StartsWith(FilePrefix, StringComparison.Ordinal) && fileName.Length >= FilePrefix.Length + 15
        && DateTime.TryParseExact(fileName.AsSpan(FilePrefix.Length, 15), "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var local)
            ? DateTime.SpecifyKind(local - BusinessDay.Offset, DateTimeKind.Utc)
            : File.GetLastWriteTimeUtc(Path.Combine(Folder, fileName));

    /// <summary>Encrypted backups, newest first.</summary>
    public IEnumerable<FileInfo> List() => List(Folder);

    /// <summary>The newest scheduled backup: one made before a restore is not copied off the PC, so the copies are judged on these.</summary>
    public FileInfo? Newest() => List().FirstOrDefault(f => !f.Name.EndsWith("-antes-de-restaurar" + Extension, StringComparison.Ordinal));

    /// <summary>Replaces the whole database with a backup of this folder. R12: the backup is opened first (a wrong phrase or another kind of
    /// installation stops here, with nothing made); then the current data is backed up (when the stored key works) as "…-antes-de-restaurar",
    /// so a wrong choice can be undone by restoring that file. The caller checks that the registers are closed.</summary>
    /// <returns>The name of that backup of the current data, or null when there is no usable key to make it.</returns>
    public async Task<Result<string?>> RestoreAsync(string fileName, string passphrase, CancellationToken ct)
    {
        var source = Path.Combine(Folder, fileName);
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(Extension, StringComparison.Ordinal) || !File.Exists(source)) return NotFound;

        await Gate.WaitAsync(ct);
        var plain = Path.Combine(WorkFolder, "restore" + (options.Provider == DbProvider.SqlServer ? ".bak" : ".tmp"));
        try
        {
            ClearWork();
            try
            {
                await using var input = File.OpenRead(source);
                await using var output = File.Create(plain);
                await BackupEncryption.DecryptAsync(input, output, passphrase, ct);
            }
            catch (CryptographicException)
            {
                return WrongPassphrase;
            }
            if (IsSqlite(plain) != (options.Provider == DbProvider.Sqlite)) return OtherDatabase;

            string? before = null;
            if ((await KeyAsync(ct)).IsSuccess)
            {
                var made = await CreateCoreAsync("antes-de-restaurar", ct);
                if (made.IsFailure) return made.Error!;
                before = made.Value.FileName;
            }

            // From here on nothing is cancelled: a restore cut halfway would leave the store without data.
            if (options.Provider == DbProvider.SqlServer) await RestoreSqlServerAsync(plain);
            else RestoreSqlite(plain);
            log.LogWarning("The database was restored from {Backup}; the data before it is in {Before}", fileName, before ?? "(no backup: no usable key)");
            return before;
        }
        finally
        {
            File.Delete(plain);
            Gate.Release();
        }
    }

    private static bool IsSqlite(string file)
    {
        var header = new byte[16];
        using var f = File.OpenRead(file);
        return f.ReadAtLeast(header, 16, throwOnEndOfStream: false) == 16 && header.AsSpan().SequenceEqual("SQLite format 3\0"u8);
    }

    private void RestoreSqlite(string plain)
    {
        using (var from = new SqliteConnection($"Data Source={plain};Mode=ReadOnly;Pooling=False"))
        using (var to = new SqliteConnection(options.ConnectionString))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>The service's login owns the database (dbcreator, see PrepareService), so it may restore over it. SINGLE_USER closes the other connections.</summary>
    private async Task RestoreSqlServerAsync(string plain)
    {
        var server = new SqlConnectionStringBuilder(options.ConnectionString);
        var database = "[" + server.InitialCatalog.Replace("]", "]]", StringComparison.Ordinal) + "]";
        server.InitialCatalog = "master";
        server.Pooling = false;
        await using (var sql = new SqlConnection(server.ConnectionString))
        {
            await sql.OpenAsync();
            await using var command = sql.CreateCommand();
            command.CommandTimeout = 3600;
            command.CommandText = $"""
                ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                BEGIN TRY
                    RESTORE DATABASE {database} FROM DISK = @file WITH REPLACE, CHECKSUM;
                END TRY
                BEGIN CATCH
                    ALTER DATABASE {database} SET MULTI_USER;
                    THROW;
                END CATCH;
                ALTER DATABASE {database} SET MULTI_USER;
                """;
            command.Parameters.AddWithValue("@file", plain);
            await command.ExecuteNonQueryAsync();
        }
        SqlConnection.ClearAllPools(); // the pooled connections were closed by SINGLE_USER
    }

    private static IEnumerable<FileInfo> List(string folder) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles("*" + Extension).OrderByDescending(f => f.Name) : [];

    /// <summary>R13: the backups made before a restore kept on the PC (the newest); each is the whole database.</summary>
    public const int KeepBeforeRestore = 3;

    /// <summary>R12: a backup made before a restore is not counted with the daily ones: several restores in a row never push out the daily history.
    /// R13: they have their own count (<see cref="KeepBeforeRestore"/>).</summary>
    private static void Prune(string folder, int keep, bool makeRoom = false)
    {
        var all = List(folder).ToList();
        static bool BeforeRestore(FileInfo f) => f.Name.EndsWith("-antes-de-restaurar" + Extension, StringComparison.Ordinal);
        foreach (var old in all.Where(f => !BeforeRestore(f)).Skip(makeRoom ? keep - 1 : keep).Concat(all.Where(BeforeRestore).Skip(KeepBeforeRestore)))
            old.Delete();
    }

    /// <summary>TK-238: whether the newest backup is in the off-PC folder, or why not; null when there is no copy to make.</summary>
    public string? CopyProblem(BackupSchedule schedule, FileInfo? newest)
    {
        if (schedule.CopyTo is not { } copy || newest is null) return null;
        if (status.CopyError is { } error) return error;
        return File.Exists(Path.Combine(copy, newest.Name)) ? null : $"El último respaldo no está en {copy}. Revise que la memoria o la carpeta compartida esté conectada.";
    }
}

/// <summary>Sets the passphrase that encrypts the backups. Only its derived key is stored, protected for this machine;
/// the owner keeps the phrase (restoring on another PC needs it).</summary>
internal static class SetBackupPassphrase
{
    /// <param name="CurrentPassphrase">R11 A3: the phrase in use. Without it only an Administrador replaces a working phrase (otherwise whoever
    /// has backups.manage could set their own, copy a backup and read it at home).</param>
    public sealed record Command(string Passphrase, string? CurrentPassphrase = null) : ICommand<Ok>;

    public static readonly Error CurrentRequired = Error.Forbidden("Backup.CurrentPassphraseRequired",
        "Escriba la frase actual de los respaldos para cambiarla, o pídale el cambio a un administrador.");

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Passphrase).NotEmpty().MinimumLength(12).MaximumLength(200).WithMessage("La frase debe tener al menos 12 caracteres.");
            RuleFor(c => c.CurrentPassphrase).MaximumLength(200);
        }
    }

    internal sealed class Handler(SettingsDbContext db, Backups backups, IUserContext user, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            // A phrase this PC cannot read any more (restored on a new PC) is typed again by whoever restores: nothing to protect there.
            var current = await backups.KeyAsync(ct);
            var replacing = current.IsSuccess;
            if (replacing && !user.IsAdministrator() && (c.CurrentPassphrase is not { Length: > 0 } typed
                    || !CryptographicOperations.FixedTimeEquals(BackupEncryption.DeriveKey(typed, current.Value.Salt), current.Value.Key)))
                return CurrentRequired;
            var business = await db.Business.SingleAsync(ct);
            business.BackupSalt = BackupEncryption.NewSalt();
            business.BackupKey = Convert.ToBase64String(backups.Protector.Protect(BackupEncryption.DeriveKey(c.Passphrase, business.BackupSalt)));
            await audit.RecordAsync("settings.backup-passphrase", "Business", Guid.Empty,
                new { Replaced = replacing, WithCurrentPassphrase = c.CurrentPassphrase is { Length: > 0 } }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/backups/passphrase", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) => (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.BackupsManage).WithTags("Backups");
            // The host's token, not the request's: a back office that gives up waiting must not cut a backup halfway.
            app.MapPost("/api/backups", async (Backups backups, IHostApplicationLifetime lifetime) =>
                    (await backups.CreateAsync(lifetime.ApplicationStopping)).ToHttp(r => Results.Created($"/api/backups/{r.FileName}", r)))
                .RequirePermission(Permissions.BackupsManage).WithTags("Backups");
            app.MapGet("/api/backups", (Backups backups) =>
                    Results.Ok(backups.List().Select(f => new BackupFile(f.Name, f.Length, f.LastWriteTimeUtc))))
                .RequirePermission(Permissions.BackupsManage).WithTags("Backups");
        }
    }
}

/// <summary>TK-213: replaces the store's data with one of the backups listed, from the back office. Only with every register closed (a sale halfway
/// would be lost or half-restored); the data before it is backed up first. The restore itself is audited in the restored database.</summary>
internal static class RestoreBackup
{
    /// <param name="ConfirmOlder">The owner accepted that the backup is older than the last e-CF signed (or B receipt issued): those
    /// documents will not be in the restored data (their numbers are not used again).</param>
    public sealed record Request(string Passphrase, bool ConfirmOlder = false);
    public sealed record Response(string? BackupBefore);

    public static Error OlderThanLastDocument(DateTime backupUtc, DateTime lastUtc) => Error.Conflict("Backup.OlderThanLastDocument",
        $"Este respaldo es del {backupUtc + BusinessDay.Offset:dd/MM/yyyy HH:mm} y el último comprobante se emitió el {lastUtc + BusinessDay.Offset:dd/MM/yyyy HH:mm}. " +
        "Los comprobantes emitidos después no estarán en los datos restaurados, aunque la DGII ya los tenga (sus números no se vuelven a usar). Confirme solo si está seguro.");

    /// <summary>Only records the restore: it runs after it, in the restored database's audit chain.</summary>
    internal sealed record Restored(string FileName, string? BackupBefore) : ICommand<Ok>;

    internal sealed class Handler(IAuditLog audit) : ICommandHandler<Restored, Ok>
    {
        public async Task<Result<Ok>> Handle(Restored c, CancellationToken ct)
        {
            await audit.RecordAsync("settings.restore-backup", "Business", Guid.Empty, c, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/backups/{fileName}/restore", async (string fileName, Request r, Backups backups, ICashRegisterReports cash,
                    ICommandHandler<Restored, Ok> record, HttpContext http, IHostApplicationLifetime lifetime) =>
                {
                    if (string.IsNullOrEmpty(r.Passphrase)) return EndpointExtensions.Problem(Backups.WrongPassphrase);
                    if (await cash.OpenRegistersAsync(http.RequestAborted) is { Count: > 0 } open)
                        return EndpointExtensions.Problem(Error.Conflict("Backup.RegistersOpen", $"Cierre las cajas antes de restaurar: {string.Join(", ", open)}."));

                    // R9 #3: what already left the store is read now and put back on the restored data.
                    var guards = new List<RestoreSnapshot>();
                    foreach (var guard in http.RequestServices.GetServices<IRestoreGuard>()) guards.Add(await guard.CaptureAsync(http.RequestAborted));
                    var backupUtc = backups.CreatedUtc(fileName);
                    if (guards.Max(g => g.LastIrreversibleUtc) is { } last && last > backupUtc && !r.ConfirmOlder)
                        return EndpointExtensions.Problem(OlderThanLastDocument(backupUtc, last));

                    var restored = await backups.RestoreAsync(fileName, r.Passphrase, lifetime.ApplicationStopping);
                    if (restored.IsFailure) return EndpointExtensions.Problem(restored.Error!);
                    // An older backup gets the migrations of this version, as at startup.
                    await AbastaliaServices.CreateDatabaseAsync(http.RequestServices, CancellationToken.None);
                    foreach (var g in guards) await g.ReapplyAsync(CancellationToken.None);
                    await record.Handle(new Restored(fileName, restored.Value), CancellationToken.None);
                    return Results.Ok(new Response(restored.Value));
                })
                .RequirePermission(Permissions.BackupsManage).WithTags("Backups");
    }
}

/// <summary>Backs up three minutes after every start when the newest backup is older than 20 hours (many store PCs are off at night),
/// and every night at the <see cref="BackupSchedule"/> time (23:30 store time), unless it is not automatic. A schedule saved on screen wakes it.</summary>
internal sealed class NightlyBackup(IServiceScopeFactory scopes, BackupStatus status, IConfiguration config, TimeProvider clock, ILogger<NightlyBackup> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var changed = status.Changed; // taken before reading, so a save in between is not missed
            var schedule = await ScheduleAsync(stoppingToken);
            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, changed);
            try
            {
                if (!schedule.Automatic) await Task.Delay(Timeout.InfiniteTimeSpan, wake.Token);
                else if (!started)
                {
                    await Task.Delay(TimeSpan.FromMinutes(3), clock, wake.Token);
                    started = true;
                    await BackUpAsync(onlyIfDue: true, stoppingToken);
                }
                else
                {
                    await Task.Delay(NightlyAuditCheck.UntilNext(clock.GetUtcNow().UtcDateTime, schedule.Time), clock, wake.Token);
                    await BackUpAsync(onlyIfDue: false, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The schedule changed: read it again.
            }
        }
    }

    private async Task<BackupSchedule> ScheduleAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<Backups>().ScheduleAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Could not read the backup schedule; using the application settings");
            return BackupSchedule.Of(new Business(), config);
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

/// <summary>TK-238: what the Sync module uploads to the cloud. Only scheduled backups, encrypted as they are on disk: the passphrase never leaves the PC.
/// The license check (<c>CloudBackup</c>) is the uploader's: the local backups never depend on the license.</summary>
internal sealed class CloudBackups(SettingsDbContext db, Backups backups, BackupStatus status, IConfiguration config,
    ICommandHandler<CloudBackups.Uploaded, Ok> uploaded) : ICloudBackups
{
    public async Task<CloudBackupFile?> PendingAsync(CancellationToken ct)
    {
        var b = await db.Business.AsNoTracking().SingleAsync(ct);
        if (!BackupSchedule.Of(b, config).Cloud || backups.Newest() is not { } newest || newest.Name == b.BackupCloudFile) return null;
        return new CloudBackupFile(newest.Name, newest.FullName);
    }

    public async Task UploadedAsync(string fileName, CancellationToken ct)
    {
        var r = await uploaded.Handle(new Uploaded(fileName), ct);
        if (r.IsFailure) throw new InvalidOperationException(r.Error!.Description);
        status.CloudError = null;
    }

    public void Failed(string? problem) => status.CloudError = problem;

    /// <summary>Records (and audits) the newest backup the cloud keeps.</summary>
    internal sealed record Uploaded(string FileName) : ICommand<Ok>;

    internal sealed class Handler(SettingsDbContext db, IAuditLog audit, TimeProvider clock) : ICommandHandler<Uploaded, Ok>
    {
        public async Task<Result<Ok>> Handle(Uploaded c, CancellationToken ct)
        {
            var b = await db.Business.SingleAsync(ct);
            (b.BackupCloudFile, b.BackupCloudUtc) = (c.FileName, clock.GetUtcNow().UtcDateTime);
            await audit.RecordAsync("settings.backup-cloud-copy", "Business", Guid.Empty, c, ct);
            return Ok.Value;
        }
    }
}

/// <summary>Degraded when there is no usable passphrase, the newest backup is older than 36 hours, or the off-PC or cloud copy failed.</summary>
internal sealed class BackupHealthCheck(Backups backups, BackupStatus status, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (await backups.KeyAsync(cancellationToken) is { IsFailure: true } key) return HealthCheckResult.Degraded(key.Error!.Description);
        var newest = backups.List().FirstOrDefault()?.LastWriteTimeUtc;
        if (newest is null || newest < clock.GetUtcNow().UtcDateTime.AddHours(-36)) return HealthCheckResult.Degraded("El último respaldo tiene más de 36 horas.");
        return (status.CopyError ?? status.CloudError) is { } error ? HealthCheckResult.Degraded(error) : HealthCheckResult.Healthy();
    }
}

/// <summary>TK-238: the backup schedule on screen (Configuración → Respaldos), with what the owner needs to trust it: whether the passphrase works,
/// the newest backup and whether it reached the off-PC folder. Saved values win over appsettings and apply at once.</summary>
internal static class BackupSettings
{
    /// <param name="Time">Store time, "HH:mm".</param>
    /// <param name="Cloud">TK-238: also copy each scheduled backup, encrypted, to the cloud (license feature <c>CloudBackup</c>, store connected).</param>
    public sealed record Command(string? CopyTo, string Time, int Keep, bool Automatic, bool Cloud = false) : ICommand<Ok>;

    /// <param name="Saved">False while every value still comes from appsettings.</param>
    /// <param name="PassphraseProblem">Why backups cannot be made (no phrase, or a key this PC cannot read); null when they can.</param>
    /// <param name="CloudLast">The newest backup the cloud confirmed it keeps.</param>
    /// <param name="CloudProblem">Why the copy to the cloud is not happening (license, last upload); null when it works or is off.</param>
    public sealed record Response(string? CopyTo, string Time, int Keep, bool Automatic, bool Saved, string Folder, string? PassphraseProblem,
        BackupFile? Last, string? CopyProblem, bool Cloud, BackupFile? CloudLast, string? CloudProblem);

    public const string CloudNotLicensed = "Su licencia no incluye el respaldo en la nube.";

    /// <summary>For the register's status bar and the Resumen: only the passphrase, cheap enough to ask every 15 seconds.</summary>
    public sealed record Warning(string? PassphraseProblem);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.CopyTo).MaximumLength(260).Must(p => Path.IsPathFullyQualified(p!)).When(c => !string.IsNullOrWhiteSpace(c.CopyTo))
                .WithMessage("Escriba la carpeta completa, por ejemplo E:\\Respaldos.");
            RuleFor(c => c.Time).Must(t => TimeOnly.TryParseExact(t, "HH:mm", out _)).WithMessage("Escriba la hora como 23:30.");
            RuleFor(c => c.Keep).InclusiveBetween(1, 365).WithMessage("Guarde entre 1 y 365 respaldos.");
        }
    }

    public static readonly Error CopyInsideData = Error.Validation("Backup.CopyInsideData",
        "La copia tiene que ir fuera de esta PC o a otro disco: no a la carpeta de respaldos ni a la de datos de Abastalia.");

    /// <summary>R10 #10: a copy inside the backup folder (or Abastalia's data folder) is no copy at all, and would be listed and pruned as a backup.</summary>
    public static bool InsideData(string copyTo, string backupFolder)
    {
        static string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        var copy = Full(copyTo);
        return new[] { backupFolder, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Abastalia") }.Select(Full)
            .Any(f => copy.Equals(f, StringComparison.OrdinalIgnoreCase) || copy.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public static readonly Error CopyNotAllowed = Error.Validation("Backup.CopyNotAllowed",
        "La copia va a un disco de esta PC (otro disco o una memoria USB). Una carpeta de red la agrega el técnico en la configuración de Abastalia.");

    /// <summary>R11 C14: from the screen (backups.manage) the copy goes to a local drive (another disk, a USB stick), never to a share someone
    /// could own. A network folder (\\server\share or a mapped drive) only when an administrator listed it in appsettings: <c>Backup:CopyTo</c>
    /// or <c>Backup:AllowedCopyFolders</c>.</summary>
    public static bool CopyAllowed(string copyTo, IConfiguration config)
    {
        static string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        var copy = Full(copyTo);
        var listed = (config.GetSection("Backup:AllowedCopyFolders").Get<string[]>() ?? []).Append(config["Backup:CopyTo"])
            .Where(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f)).Select(f => Full(f!));
        if (listed.Any(f => copy.Equals(f, StringComparison.OrdinalIgnoreCase) || copy.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (copy.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(copy) is not { Length: > 0 } root) return false;
        try { return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable; }
        catch (ArgumentException) { return false; }
    }

    internal sealed class Handler(SettingsDbContext db, BackupStatus status, Backups backups, IAuditLog audit, IConfiguration config) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            if (c.CopyTo is { } copy && !string.IsNullOrWhiteSpace(copy) && InsideData(copy.Trim(), backups.Folder)) return CopyInsideData;
            if (c.CopyTo is { } target && !string.IsNullOrWhiteSpace(target) && !CopyAllowed(target.Trim(), config)) return CopyNotAllowed;
            var b = await db.Business.SingleAsync(ct);
            var newCopy = c.CopyTo?.Trim() ?? "";
            var before = new { CopyTo = b.BackupCopyTo, Time = b.BackupTime, Keep = b.BackupKeep, Automatic = b.BackupAutomatic, Cloud = b.BackupCloud };
            b.BackupCopyTo = newCopy;
            b.BackupTime = TimeOnly.ParseExact(c.Time, "HH:mm").ToTimeSpan();
            b.BackupKeep = c.Keep;
            b.BackupAutomatic = c.Automatic;
            b.BackupCloud = c.Cloud;
            if (!c.Cloud) status.CloudError = null;
            await audit.RecordAsync("settings.backup-schedule", "Business", Guid.Empty,
                new { before, after = new { CopyTo = b.BackupCopyTo, Time = b.BackupTime, Keep = b.BackupKeep, Automatic = b.BackupAutomatic, Cloud = b.BackupCloud } }, ct);
            status.ScheduleChanged();
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/backups/settings", async (Backups backups, SettingsDbContext db, IConfiguration config, IFeatureService license, BackupStatus status,
                    CancellationToken ct) =>
                {
                    var b = await db.Business.AsNoTracking().SingleAsync(ct);
                    var s = BackupSchedule.Of(b, config);
                    var key = await backups.KeyAsync(ct);
                    var newest = backups.Newest();
                    var cloudProblem = !s.Cloud ? null : !license.IsEnabled(Feature.CloudBackup) ? CloudNotLicensed : status.CloudError;
                    return Results.Ok(new Response(s.CopyTo, $"{s.Time:hh\\:mm}", s.Keep, s.Automatic, b.BackupAutomatic is not null, backups.Folder,
                        key.Error?.Description, newest is null ? null : new BackupFile(newest.Name, newest.Length, backups.CreatedUtc(newest.Name)),
                        backups.CopyProblem(s, newest), s.Cloud,
                        b.BackupCloudFile is { } cloud ? new BackupFile(cloud, 0, b.BackupCloudUtc ?? backups.CreatedUtc(cloud)) : null, cloudProblem));
                })
                .RequirePermission(Permissions.BackupsManage).WithTags("Backups");
            app.MapPut("/api/backups/settings", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) => (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.BackupsManage).WithTags("Backups");
            app.MapGet("/api/backups/warning", async (Backups backups, CancellationToken ct) => Results.Ok(new Warning((await backups.KeyAsync(ct)).Error?.Description)))
                .RequirePermission(Permissions.BackupsManage).WithTags("Backups");
        }
    }
}
