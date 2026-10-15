using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Toki.Fiscal.Domain;

namespace Toki.Fiscal;

/// <summary>Unhealthy when an e-CF is past its 72-hour DGII deadline; degraded when one is close (12 h) or was rejected.</summary>
internal sealed class ColaEcfHealthCheck(FiscalDbContext db, TimeProvider reloj) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var pendientes = db.Documentos.Where(d => d.Estado == EstadoEnvio.Pendiente);
        if (await pendientes.AnyAsync(d => d.FechaLimiteEnvioUtc < ahora, cancellationToken))
            return HealthCheckResult.Unhealthy("Hay e-CF sin enviar después del plazo de 72 horas.");
        if (await pendientes.AnyAsync(d => d.FechaLimiteEnvioUtc < ahora.AddHours(12), cancellationToken)
            || await db.Documentos.AnyAsync(d => d.Estado == EstadoEnvio.Rechazado, cancellationToken))
            return HealthCheckResult.Degraded("Hay e-CF cerca del plazo o rechazados.");
        return HealthCheckResult.Healthy();
    }
}
