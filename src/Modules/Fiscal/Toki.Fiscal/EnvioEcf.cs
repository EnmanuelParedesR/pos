using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.Fiscal.Domain;

namespace Toki.Fiscal;

/// <summary>The contracted e-CF provider (it signs nothing: it receives the signed XML and talks to the DGII).
/// Throws on a transient failure (network, provider down): the document is retried. Returns Rechazado when the DGII refuses it.</summary>
/// <remarks>shortcut: one call returns the final state; the real provider (TK-055) answers with a TrackId and the state is queried later.</remarks>
internal interface IEcfProveedor
{
    Task<ResultadoEnvio> EnviarAsync(string encf, byte[] xmlFirmadoGzip, CancellationToken ct);
}

internal sealed record ResultadoEnvio(EstadoEnvio Estado, string? TrackId, string? Mensaje = null);

/// <summary>Stand-in until a provider is contracted (e-CF on hold). Nothing reaches DGII, so nothing is marked accepted.</summary>
internal sealed class ProveedorSimulado : IEcfProveedor
{
    public Task<ResultadoEnvio> EnviarAsync(string encf, byte[] xmlFirmadoGzip, CancellationToken ct) =>
        Task.FromResult(new ResultadoEnvio(EstadoEnvio.Simulado, null));
}

/// <summary>Sends pending e-CF, closest DGII deadline first. A failure waits 1, 2, 4… up to 60 minutes before the next try.
/// The sale never waits for this: it was committed with the document as Pendiente (contingency up to 72 hours).</summary>
internal sealed class EnvioEcf(FiscalDbContext db, IEcfProveedor proveedor, TimeProvider reloj, ILogger<EnvioEcf> log)
{
    public const int Lote = 20;

    /// <returns>How many documents were tried.</returns>
    public async Task<int> ProcesarAsync(CancellationToken ct)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var documentos = await db.Documentos
            .Where(d => d.Estado == EstadoEnvio.Pendiente && (d.ProximoIntentoUtc == null || d.ProximoIntentoUtc <= ahora))
            .OrderBy(d => d.FechaLimiteEnvioUtc).Take(Lote).ToListAsync(ct);
        foreach (var d in documentos)
        {
            try
            {
                var r = await proveedor.EnviarAsync(d.ENcf, d.XmlFirmado, ct);
                (d.Estado, d.TrackId, d.UltimoError, d.ProximoIntentoUtc) = (r.Estado, r.TrackId, r.Mensaje, null);
            }
            // An HTTP timeout is a TaskCanceledException too: only our own shutdown token ends the pass.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                d.Intentos++;
                d.ProximoIntentoUtc = ahora + Espera(d.Intentos);
                d.UltimoError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                log.LogWarning(ex, "No se pudo enviar el e-CF {ENcf} (intento {Intentos})", d.ENcf, d.Intentos);
            }
            // One save per document: a crash after the provider answered resends at most one (the provider dedupes by e-NCF).
            await db.SaveChangesAsync(ct);
        }
        return documentos.Count;
    }

    public static TimeSpan Espera(int intentos) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(intentos, 7) - 1)));
}

/// <summary>Runs <see cref="EnvioEcf"/> every <c>Fiscal:IntervaloEnvioSegundos</c> (30) when <c>Fiscal:EnvioAutomatico</c> is true.</summary>
internal sealed class EnvioEcfWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider reloj, ILogger<EnvioEcfWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue<bool>("Fiscal:EnvioAutomatico")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue("Fiscal:IntervaloEnvioSegundos", 30)), reloj);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var envio = scope.ServiceProvider.GetRequiredService<EnvioEcf>();
                while (await envio.ProcesarAsync(stoppingToken) == EnvioEcf.Lote) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Falló la cola de envío de e-CF");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
