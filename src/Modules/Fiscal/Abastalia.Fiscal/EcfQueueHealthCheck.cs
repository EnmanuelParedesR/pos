using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Abastalia.Fiscal.Domain;

namespace Abastalia.Fiscal;

/// <summary>As in arquitectura-v1-vsa §7.3: an e-CF not yet delivered to the DGII after 24 h is Degraded, after 60 h Unhealthy (the deadline is 72 h).
/// A rejection of the last 30 days without a replacement (TK-147) is Degraded too; one sent more than an hour ago still without a result as well.</summary>
internal sealed class EcfQueueHealthCheck(FiscalDbContext db, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var pending = db.Documents.Where(d => d.Status == SubmissionStatus.Pending);
        if (await pending.AnyAsync(d => d.SignedUtc < now.AddHours(-60), cancellationToken))
            return HealthCheckResult.Unhealthy("Some e-CF have not been sent for more than 60 hours (the DGII deadline is 72).");
        if (await pending.AnyAsync(d => d.SignedUtc < now.AddHours(-24), cancellationToken))
            return HealthCheckResult.Degraded("Some e-CF have been pending for more than 24 hours.");
        if (await db.Documents.AnyAsync(d => d.Status == SubmissionStatus.Rejected && d.ReplacedByENcf == null && d.SignedUtc > now.AddDays(-30), cancellationToken))
            return HealthCheckResult.Degraded("Some e-CF were rejected by the DGII and have no replacement.");
        if (await db.Documents.AnyAsync(d => d.Status == SubmissionStatus.Sent && d.SentUtc < now.AddHours(-1), cancellationToken))
            return HealthCheckResult.Degraded("Some e-CF were sent more than an hour ago and the DGII has not answered.");
        return HealthCheckResult.Healthy();
    }
}
