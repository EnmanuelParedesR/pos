using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.SharedKernel;

namespace Abastalia.Settings;

/// <summary>Checks the audit hash chain on demand: anyone who edits or deletes audit rows in the database breaks it.</summary>
internal static class VerifyAudit
{
    public sealed record Response(bool IsIntact, long? FirstBrokenRecord);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/audit/verify", async (InfraDbContext db, AuditKey key, CancellationToken ct) =>
                    await AuditChain.VerifyAsync(db, key, ct) is { } broken ? Results.Ok(new Response(false, broken)) : Results.Ok(new Response(true, null)))
                .RequirePermission(Permissions.AuditView).WithTags("Audit");
    }
}

/// <summary>Result of the last nightly check; the "audit" health check reports it.</summary>
internal sealed class LastAuditCheck : IHealthCheck
{
    public long? FirstBrokenRecord { get; set; }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(FirstBrokenRecord is { } broken
            ? HealthCheckResult.Unhealthy($"La cadena de auditoría está rota desde el registro {broken}.")
            : HealthCheckResult.Healthy());
}

/// <summary>Verifies the audit chain two minutes after every start and every night at 03:00 store time (many store PCs are off at night,
/// so the nightly run alone would never happen there); a broken chain is a critical log line and an unhealthy /health.</summary>
internal sealed class NightlyAuditCheck(IServiceScopeFactory scopes, LastAuditCheck last, TimeProvider clock,
    IConfiguration config, ILogger<NightlyAuditCheck> log) : BackgroundService
{
    private static readonly TimeSpan CheckTime = TimeSpan.FromHours(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Audit:NightlyCheck", true)) return; // off in the in-memory test host (fake clock)
        await Task.Delay(TimeSpan.FromMinutes(2), clock, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await VerifyAsync(stoppingToken);
            await Task.Delay(UntilNext(clock.GetUtcNow().UtcDateTime, CheckTime), clock, stoppingToken);
        }
    }

    public async Task VerifyAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            last.FirstBrokenRecord = await AuditChain.VerifyAsync(scope.ServiceProvider.GetRequiredService<InfraDbContext>(), scope.ServiceProvider.GetRequiredService<AuditKey>(), ct);
            if (last.FirstBrokenRecord is { } broken) log.LogCritical("The audit chain is broken from record {Sequence}", broken);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Could not verify the audit log");
        }
    }

    /// <summary>Time left until the next <paramref name="time"/> of the store's clock (UTC−4).</summary>
    public static TimeSpan UntilNext(DateTime nowUtc, TimeSpan time)
    {
        var local = nowUtc + BusinessDay.Offset;
        var next = local.Date + time;
        if (next <= local) next = next.AddDays(1);
        return next - local;
    }
}
