using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Toki.Fiscal.Domain;

namespace Toki.Fiscal;

/// <summary>As in arquitectura-v1-vsa §7.3: an e-CF pending for more than 24 h is Degraded, for more than 60 h Unhealthy (the DGII deadline is 72 h).
/// A rejection in the last 30 days is Degraded too (it needs a corrected document); older ones no longer count.</summary>
internal sealed class ColaEcfHealthCheck(FiscalDbContext db, TimeProvider reloj) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var pendientes = db.Documentos.Where(d => d.Estado == EstadoEnvio.Pendiente);
        if (await pendientes.AnyAsync(d => d.FechaFirmaUtc < ahora.AddHours(-60), cancellationToken))
            return HealthCheckResult.Unhealthy("Hay e-CF sin enviar desde hace más de 60 horas (el plazo de la DGII es 72).");
        if (await pendientes.AnyAsync(d => d.FechaFirmaUtc < ahora.AddHours(-24), cancellationToken)
            || await db.Documentos.AnyAsync(d => d.Estado == EstadoEnvio.Rechazado && d.FechaFirmaUtc > ahora.AddDays(-30), cancellationToken))
            return HealthCheckResult.Degraded("Hay e-CF pendientes desde hace más de 24 horas o rechazados.");
        return HealthCheckResult.Healthy();
    }
}
