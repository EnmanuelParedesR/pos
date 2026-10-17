using System.Data.Common;
using Microsoft.Extensions.Hosting;

namespace Abastalia.BuildingBlocks.Persistence;

/// <summary>Creates or migrates the database in the host's StartingAsync: after the Windows service has told Windows it started
/// (a slow migration does not hit the 30-second start limit) and before Kestrel listens or any worker runs (no request sees a half-migrated database).</summary>
internal sealed class CreateDatabaseOnStartup(IServiceProvider services) : IHostedLifecycleService
{
    /// <summary>Azure SQL with a managed identity (Host.Lite): on a cold start the identity's token can arrive after the server gave up on
    /// the login (a reset in SendFedAuthToken) and the host aborted. The token is cached after it first arrives, so a few tries cover the start.
    /// Each module's migrations are recorded as they apply, so a retry resumes where the last try stopped.</summary>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await AbastaliaServices.CreateDatabaseAsync(services, cancellationToken);
                return;
            }
            catch (DbException) when (attempt < 6 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
