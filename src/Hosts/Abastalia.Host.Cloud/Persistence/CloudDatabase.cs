using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Abastalia.Host.Cloud.Persistence;

/// <summary>SQL Server / Azure SQL: applies pending migrations. Sqlite (tests, development): creates the tables when the file is new.
/// Runs in StartingAsync: before Kestrel listens or the inbox applier runs.</summary>
internal sealed class CreateCloudDatabaseOnStartup(IServiceProvider services, IConfiguration config) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (!config.GetValue("Database:CreateOnStartup", true)) return;
        await using var scope = services.CreateAsyncScope();
        await PrepareAsync(scope.ServiceProvider.GetRequiredService<CloudDbContext>(), cancellationToken);
    }

    /// <summary>Also used by the console commands (grant-platform-admin), which run without starting the host.</summary>
    public static async Task PrepareAsync(CloudDbContext db, CancellationToken ct)
    {
        if (db.Database.IsSqlServer())
        {
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5)); // a paused serverless database takes up to a minute to resume
            await db.Database.MigrateAsync(ct);
        }
        else await db.Database.EnsureCreatedAsync(ct);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Only on /health/ready: App Service pings /health every minute, and touching the database there would keep it from ever pausing.</summary>
internal sealed class DatabaseHealthCheck(CloudDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Cannot reach the database.");
}
