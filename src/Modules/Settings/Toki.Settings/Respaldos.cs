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

public sealed record RespaldoHecho(string Archivo, long Bytes, DateTime FechaUtc);

/// <summary>What the last run could not do besides the backup itself (today: the copy off the PC), for the health check.</summary>
internal sealed class EstadoRespaldo
{
    public string? ErrorCopia { get; set; }
}

/// <summary>Backs up the whole database, encrypts it with the owner's passphrase and keeps the newest ones here and in the off-PC copy, if set.
/// The plain backup is written to <c>respaldos\trabajo</c> (SQL Server's service account gets write access there at install) and deleted once encrypted;
/// anything a crash left in that folder is deleted at the next run. The encrypted file only gets its final name when it is complete.</summary>
internal sealed class Respaldos(ConfiguracionDbContext db, DatabaseOptions opciones, IDataProtectionProvider proteccion, IConfiguration config, TimeProvider reloj,
    EstadoRespaldo estado, ILogger<Respaldos> log)
{
    public const string Extension = ".toki-respaldo";
    public static readonly Error SinFrase = Error.Conflict("Respaldo.SinFrase", "Configure la frase de los respaldos antes de respaldar.");
    public static readonly Error FraseIlegible = Error.Conflict("Respaldo.FraseIlegible",
        "Vuelva a escribir la frase de los respaldos: la clave guardada no se puede leer en esta PC.");

    public string Carpeta => config["Respaldo:Carpeta"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "respaldos");
    public string Trabajo => Path.Combine(Carpeta, "trabajo");
    private int Conservar => config.GetValue("Respaldo:Conservar", 14);

    public IDataProtector Protector => proteccion.CreateProtector("Toki.Respaldo.Clave");

    /// <summary>The stored key, or why it cannot be used (no phrase yet, or the key ring of this PC cannot open it: restored on a new PC, other account).</summary>
    public async Task<Result<(byte[] Clave, byte[] Sal)>> ClaveAsync(CancellationToken ct)
    {
        var negocio = await db.Negocio.AsNoTracking().SingleAsync(ct);
        if (negocio.RespaldoSal is null || negocio.RespaldoClave is null) return SinFrase;
        try
        {
            return (Protector.Unprotect(Convert.FromBase64String(negocio.RespaldoClave)), negocio.RespaldoSal);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return FraseIlegible;
        }
    }

    public async Task<Result<RespaldoHecho>> CrearAsync(CancellationToken ct)
    {
        var clave = await ClaveAsync(ct);
        if (clave.IsFailure) return clave.Error!;

        Directory.CreateDirectory(Trabajo);
        foreach (var resto in Directory.EnumerateFiles(Trabajo)) File.Delete(resto); // a plain copy left by a crash never stays
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var nombre = $"toki-{ahora + DiaNegocio.Desfase:yyyyMMdd-HHmmss}";
        var plano = Path.Combine(Trabajo, nombre + (opciones.Provider == DbProvider.SqlServer ? ".bak" : ".tmp"));
        var destino = Path.Combine(Carpeta, nombre + Extension);
        try
        {
            db.Database.SetCommandTimeout(TimeSpan.FromHours(1)); // a few GB on a store PC's disk takes minutes
            if (opciones.Provider == DbProvider.SqlServer)
                await db.Database.ExecuteSqlAsync($"BACKUP DATABASE {db.Database.GetDbConnection().Database} TO DISK = {plano} WITH INIT, CHECKSUM", ct);
            else await db.Database.ExecuteSqlAsync($"VACUUM INTO {plano}", ct);

            await using (var entrada = File.OpenRead(plano))
            await using (var salida = File.Create(destino + ".parcial"))
                await CifradoRespaldo.CifrarAsync(entrada, salida, clave.Value.Clave, clave.Value.Sal, ct);
            File.Move(destino + ".parcial", destino);
        }
        finally
        {
            File.Delete(plano);
            File.Delete(destino + ".parcial");
        }

        Podar(Carpeta);
        estado.ErrorCopia = null;
        if (config["Respaldo:CopiaEn"] is { Length: > 0 } copia)
        {
            // A full USB stick or an offline share must not stop the local backups.
            try
            {
                Directory.CreateDirectory(copia);
                Podar(copia, dejarLugar: true);
                File.Copy(destino, Path.Combine(copia, nombre + Extension), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                estado.ErrorCopia = $"No se pudo copiar el respaldo a {copia}: {e.Message}";
                log.LogError(e, "No se pudo copiar el respaldo a {Copia}", copia);
            }
        }
        return new RespaldoHecho(Path.GetFileName(destino), new FileInfo(destino).Length, ahora);
    }

    /// <summary>Encrypted backups, newest first.</summary>
    public IEnumerable<FileInfo> Listar() => Listar(Carpeta);

    private static IEnumerable<FileInfo> Listar(string carpeta) =>
        Directory.Exists(carpeta) ? new DirectoryInfo(carpeta).GetFiles("*" + Extension).OrderByDescending(f => f.Name) : [];

    private void Podar(string carpeta, bool dejarLugar = false)
    {
        foreach (var viejo in Listar(carpeta).Skip(dejarLugar ? Conservar - 1 : Conservar)) viejo.Delete();
    }
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
            // The host's token, not the request's: a back office that gives up waiting must not cut a backup halfway.
            app.MapPost("/api/respaldos", async (Respaldos respaldos, IHostApplicationLifetime vida) =>
                    (await respaldos.CrearAsync(vida.ApplicationStopping)).ToHttp(r => Results.Created($"/api/respaldos/{r.Archivo}", r)))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Respaldos");
            app.MapGet("/api/respaldos", (Respaldos respaldos) =>
                    Results.Ok(respaldos.Listar().Select(f => new RespaldoHecho(f.Name, f.Length, f.LastWriteTimeUtc))))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Respaldos");
        }
    }
}

/// <summary>Backs up three minutes after every start when the newest backup is older than 20 hours (many store PCs are off at night),
/// and every night at <c>Respaldo:Hora</c> (23:30 store time), unless <c>Respaldo:Automatico</c> is false.</summary>
internal sealed class RespaldoNocturno(IServiceScopeFactory scopes, IConfiguration config, TimeProvider reloj, ILogger<RespaldoNocturno> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Respaldo:Automatico", true)) return;
        var hora = config.GetValue("Respaldo:Hora", new TimeSpan(23, 30, 0));
        await Task.Delay(TimeSpan.FromMinutes(3), reloj, stoppingToken);
        await RespaldarAsync(soloSiHaceFalta: true, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(VerificacionNocturnaAuditoria.HastaLaProxima(reloj.GetUtcNow().UtcDateTime, hora), reloj, stoppingToken);
            await RespaldarAsync(soloSiHaceFalta: false, stoppingToken);
        }
    }

    /// <param name="soloSiHaceFalta">At startup: only when the newest backup is older than 20 hours.</param>
    public async Task RespaldarAsync(bool soloSiHaceFalta, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var respaldos = scope.ServiceProvider.GetRequiredService<Respaldos>();
            if (soloSiHaceFalta && respaldos.Listar().FirstOrDefault()?.LastWriteTimeUtc > reloj.GetUtcNow().UtcDateTime.AddHours(-20)) return;
            var r = await respaldos.CrearAsync(ct);
            if (r.IsFailure) log.LogError("No se hizo el respaldo: {Motivo}", r.Error!.Description);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Falló el respaldo");
        }
    }
}

/// <summary>Degraded when there is no usable passphrase, the newest backup is older than 36 hours, or the off-PC copy failed.</summary>
internal sealed class RespaldoHealthCheck(Respaldos respaldos, EstadoRespaldo estado, TimeProvider reloj) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (await respaldos.ClaveAsync(cancellationToken) is { IsFailure: true } clave) return HealthCheckResult.Degraded(clave.Error!.Description);
        var ultimo = respaldos.Listar().FirstOrDefault()?.LastWriteTimeUtc;
        if (ultimo is null || ultimo < reloj.GetUtcNow().UtcDateTime.AddHours(-36)) return HealthCheckResult.Degraded("El último respaldo tiene más de 36 horas.");
        return estado.ErrorCopia is { } error ? HealthCheckResult.Degraded(error) : HealthCheckResult.Healthy();
    }
}
