using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Abastalia.BuildingBlocks.Persistence;

/// <summary>Unhealthy when the server clock is behind the newest audit record: receipts, shifts and e-CF dates would go backwards.</summary>
internal sealed class ClockHealthCheck(InfraDbContext db, TimeProvider clock) : IHealthCheck
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var last = await db.AuditRecords.OrderByDescending(r => r.Sequence).Select(r => (DateTime?)r.DateUtc).FirstOrDefaultAsync(cancellationToken);
        return last > clock.GetUtcNow().UtcDateTime + Tolerance
            ? HealthCheckResult.Unhealthy($"The server clock is behind the newest audit record ({last:u}). Fix the Windows time.")
            : HealthCheckResult.Healthy();
    }
}
