using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Toki.BuildingBlocks.Persistence;

/// <summary>Unhealthy when the server clock is behind the newest audit record: receipts, shifts and e-CF dates would go backwards.</summary>
internal sealed class RelojHealthCheck(InfraDbContext db, TimeProvider reloj) : IHealthCheck
{
    public static readonly TimeSpan Tolerancia = TimeSpan.FromMinutes(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var ultimo = await db.Auditoria.OrderByDescending(r => r.Secuencia).Select(r => (DateTime?)r.FechaUtc).FirstOrDefaultAsync(cancellationToken);
        return ultimo > reloj.GetUtcNow().UtcDateTime + Tolerancia
            ? HealthCheckResult.Unhealthy($"La hora del servidor está atrasada respecto del último registro ({ultimo:u}). Corrija la hora de Windows.")
            : HealthCheckResult.Healthy();
    }
}
