using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Toki.Fiscal.Domain;

namespace Toki.Fiscal;

/// <summary>As in arquitectura-v1-vsa §7.3: an e-CF pending for more than 24 h is Degraded, for more than 60 h Unhealthy (the DGII deadline is 72 h).
/// A rejection in the last 30 days is Degraded too (it needs a corrected document); older ones no longer count.</summary>
internal sealed class EcfQueueHealthCheck(FiscalDbContext db, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var pending = db.Documents.Where(d => d.Status == SubmissionStatus.Pending);
        if (await pending.AnyAsync(d => d.SignedUtc < now.AddHours(-60), cancellationToken))
            return HealthCheckResult.Unhealthy("Some e-CF have not been sent for more than 60 hours (the DGII deadline is 72).");
        if (await pending.AnyAsync(d => d.SignedUtc < now.AddHours(-24), cancellationToken)
            || await db.Documents.AnyAsync(d => d.Status == SubmissionStatus.Rejected && d.SignedUtc > now.AddDays(-30), cancellationToken))
            return HealthCheckResult.Degraded("Some e-CF have been pending for more than 24 hours or were rejected.");
        return HealthCheckResult.Healthy();
    }
}
