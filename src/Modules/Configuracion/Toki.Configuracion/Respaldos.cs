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

namespace Toki.Configuracion;

public sealed record RespaldoHecho(string Archivo, long Bytes, DateTime FechaUtc);

/// <summary>Backs up the whole database, encrypts it with the owner's passphrase and keeps the newest ones (and a copy elsewhere, if set).
/// SQL Server writes the plain backup to its own default folder (its service account can always write there); it is deleted once encrypted.</summary>
internal sealed class Respaldos(ConfiguracionDbContext db, DatabaseOptions opciones, IDataProtectionProvider proteccion, IConfiguration config, TimeProvider reloj)
{
    public const string Extension = ".toki-respaldo";
    public static readonly Error SinFrase = Error.Conflict("Respaldo.SinFrase", "Configure la frase de los respaldos antes de respaldar.");

    public string Carpeta => config["Respaldo:Carpeta"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "respaldos");

    public IDataProtector Protector => proteccion.CreateProtector("Toki.Respaldo.Clave");

    public async Task<Result<RespaldoHecho>> CrearAsync(CancellationToken ct)
    {
        var negocio = await db.Negocio.AsNoTracking().SingleAsync(ct);
        if (negocio.RespaldoSal is null || negocio.RespaldoClave is null) return SinFrase;
        var clave = Protector.Unprotect(Convert.FromBase64String(negocio.RespaldoClave));

        Directory.CreateDirectory(Carpeta);
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var nombre = $"toki-{ahora + DiaNegocio.Desfase:yyyyMMdd-HHmmss}";
        var plano = opciones.Provider == DbProvider.SqlServer
            ? Path.Combine(await db.Database.SqlQuery<string>($"SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000)) AS Value").SingleAsync(ct), nombre + ".bak")
            : Path.Combine(Carpeta, nombre + ".tmp");
        try
        {
            if (opciones.Provider == DbProvider.SqlServer)
            {
                var baseDeDatos = db.Database.GetDbConnection().Database;
                await db.Database.ExecuteSqlAsync($"BACKUP DATABASE {baseDeDatos} TO DISK = {plano} WITH INIT, CHECKSUM", ct);
            }
            else await db.Database.ExecuteSqlAsync($"VACUUM INTO {plano}", ct);

            var destino = Path.Combine(Carpeta, nombre + Extension);
            await using (var entrada = File.OpenRead(plano))
            await using (var salida = File.Create(destino))
                await CifradoRespaldo.CifrarAsync(entrada, salida, clave, negocio.RespaldoSal, ct);

            if (config["Respaldo:CopiaEn"] is { Length: > 0 } copia)
            {
                Directory.CreateDirectory(copia);
                File.Copy(destino, Path.Combine(copia, nombre + Extension), overwrite: true);
            }
            foreach (var viejo in Listar().Skip(config.GetValue("Respaldo:Conservar", 14))) File.Delete(viejo.FullName);
            return new RespaldoHecho(Path.GetFileName(destino), new FileInfo(destino).Length, ahora);
        }
        finally
        {
            File.Delete(plano); // the unencrypted copy never stays on disk
        }
    }

    /// <summary>Encrypted backups, newest first.</summary>
    public IEnumerable<FileInfo> Listar() =>
        Directory.Exists(Carpeta) ? new DirectoryInfo(Carpeta).GetFiles("*" + Extension).OrderByDescending(f => f.Name) : [];
}

/// <summary>Sets the passphrase that encrypts the backups. Only its derived key is stored, protected for this machine;
/// the owner keeps the phrase (restoring on another PC needs it).</summary>
internal static class FraseRespaldo
{
    public sealed record Command(string Frase) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Frase).NotEmpty().MinimumLength(12).MaximumLength(200).WithMessage("La frase debe tener al menos 12 caracteres.");
    }

    internal sealed class Handler(ConfiguracionDbContext db, Respaldos respaldos, IAuditoria auditoria) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var negocio = await db.Negocio.SingleAsync(ct);
            negocio.RespaldoSal = CifradoRespaldo.NuevaSal();
            negocio.RespaldoClave = Convert.ToBase64String(respaldos.Protector.Protect(CifradoRespaldo.DerivarClave(c.Frase, negocio.RespaldoSal)));
            await auditoria.RegistrarAsync("configuracion.frase-respaldo", "Negocio", Guid.Empty, new { }, ct);
            return Ok.Valor;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/respaldos/frase", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) => (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Respaldos");
            app.MapPost("/api/respaldos", async (Respaldos respaldos, CancellationToken ct) => (await respaldos.CrearAsync(ct)).ToHttp(r => Results.Created($"/api/respaldos/{r.Archivo}", r)))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Respaldos");
            app.MapGet("/api/respaldos", (Respaldos respaldos) =>
                    Results.Ok(respaldos.Listar().Select(f => new RespaldoHecho(f.Name, f.Length, f.LastWriteTimeUtc))))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Respaldos");
        }
    }
}

/// <summary>Backs up every night at <c>Respaldo:Hora</c> (23:30 store time) unless <c>Respaldo:Automatico</c> is false.</summary>
internal sealed class RespaldoNocturno(IServiceScopeFactory scopes, IConfiguration config, TimeProvider reloj, ILogger<RespaldoNocturno> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Respaldo:Automatico", true)) return;
        var hora = config.GetValue("Respaldo:Hora", new TimeSpan(23, 30, 0));
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(VerificacionNocturnaAuditoria.HastaLaProxima(reloj.GetUtcNow().UtcDateTime, hora), reloj, stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var r = await scope.ServiceProvider.GetRequiredService<Respaldos>().CrearAsync(stoppingToken);
                if (r.IsFailure) log.LogError("No se hizo el respaldo: {Motivo}", r.Error!.Description);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Falló el respaldo nocturno");
            }
        }
    }
}

/// <summary>Degraded when there is no passphrase or the newest backup is older than 36 hours.</summary>
internal sealed class RespaldoHealthCheck(Respaldos respaldos, ConfiguracionDbContext db, TimeProvider reloj) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (await db.Negocio.AnyAsync(n => n.RespaldoClave == null, cancellationToken)) return HealthCheckResult.Degraded("No hay frase de respaldo: no se están haciendo respaldos.");
        var ultimo = respaldos.Listar().FirstOrDefault()?.LastWriteTimeUtc;
        return ultimo is null || ultimo < reloj.GetUtcNow().UtcDateTime.AddHours(-36)
            ? HealthCheckResult.Degraded("El último respaldo tiene más de 36 horas.")
            : HealthCheckResult.Healthy();
    }
}
