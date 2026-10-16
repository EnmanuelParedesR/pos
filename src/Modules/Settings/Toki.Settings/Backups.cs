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
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.CashRegister.Contracts;
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
    public static readonly Error NotFound = Error.NotFound("Backup.NotFound", "Ese respaldo ya no está en esta PC.");
    public static readonly Error WrongPassphrase = Error.Validation("Backup.WrongPassphrase", "La frase no abre este respaldo, o el archivo está dañado.");
    public static readonly Error OtherDatabase = Error.Validation("Backup.OtherDatabase", "Este respaldo es de otro tipo de instalación de Toki y no se puede restaurar aquí.");

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

    /// <param name="label">Added to the name of a backup made for a reason ("antes-de-restaurar"); such a backup prunes nothing and is not copied off the PC.</param>
    public async Task<Result<BackupFile>> CreateAsync(CancellationToken ct, string? label = null)
    {
        var key = await KeyAsync(ct);
        if (key.IsFailure) return key.Error!;

        Directory.CreateDirectory(WorkFolder);
        foreach (var leftover in Directory.EnumerateFiles(WorkFolder)) File.Delete(leftover); // a plain copy left by a crash never stays
        var now = clock.GetUtcNow().UtcDateTime;
        var name = $"toki-{now + BusinessDay.Offset:yyyyMMdd-HHmmss}" + (label is null ? "" : "-" + label);
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

    /// <summary>When a backup of this folder was made: from its name (store time), else the file's date.</summary>
    public DateTime CreatedUtc(string fileName) =>
        fileName.Length >= 20 && DateTime.TryParseExact(fileName[5..20], "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var local) && fileName.StartsWith("toki-", StringComparison.Ordinal)
            ? DateTime.SpecifyKind(local - BusinessDay.Offset, DateTimeKind.Utc)
            : File.GetLastWriteTimeUtc(Path.Combine(Folder, fileName));

    /// <summary>Encrypted backups, newest first.</summary>
    public IEnumerable<FileInfo> List() => List(Folder);

    /// <summary>Replaces the whole database with a backup of this folder. The current data is backed up first (when the stored key works) as
    /// "…-antes-de-restaurar", so a wrong choice can be undone by restoring that file. The caller checks that the registers are closed.</summary>
    /// <returns>The name of that backup of the current data, or null when there is no usable key to make it.</returns>
    public async Task<Result<string?>> RestoreAsync(string fileName, string passphrase, CancellationToken ct)
    {
        var source = Path.Combine(Folder, fileName);
        if (Path.GetFileName(fileName) != fileName || !fileName.EndsWith(Extension, StringComparison.Ordinal) || !File.Exists(source)) return NotFound;

        string? before = null;
        if ((await KeyAsync(ct)).IsSuccess)
        {
            var made = await CreateAsync(ct, "antes-de-restaurar");
            if (made.IsFailure) return made.Error!;
            before = made.Value.FileName;
        }

        Directory.CreateDirectory(WorkFolder);
        var plain = Path.Combine(WorkFolder, "restore" + (options.Provider == DbProvider.SqlServer ? ".bak" : ".tmp"));
        try
        {
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

            // From here on nothing is cancelled: a restore cut halfway would leave the store without data.
            if (options.Provider == DbProvider.SqlServer) await RestoreSqlServerAsync(plain);
            else RestoreSqlite(plain);
            log.LogWarning("The database was restored from {Backup}; the data before it is in {Before}", fileName, before ?? "(no backup: no usable key)");
            return before;
        }
        finally
        {
            File.Delete(plain);
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
                    await TokiServices.CreateDatabaseAsync(http.RequestServices, CancellationToken.None);
                    foreach (var g in guards) await g.ReapplyAsync(CancellationToken.None);
                    await record.Handle(new Restored(fileName, restored.Value), CancellationToken.None);
                    return Results.Ok(new Response(restored.Value));
                })
                .RequirePermission(Permissions.BackupsManage).WithTags("Backups");
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
