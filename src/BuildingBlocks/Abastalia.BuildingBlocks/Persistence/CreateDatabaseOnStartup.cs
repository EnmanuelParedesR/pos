using Microsoft.Extensions.Hosting;

namespace Abastalia.BuildingBlocks.Persistence;

/// <summary>Creates or migrates the database in the host's StartingAsync: after the Windows service has told Windows it started
/// (a slow migration does not hit the 30-second start limit) and before Kestrel listens or any worker runs (no request sees a half-migrated database).</summary>
internal sealed class CreateDatabaseOnStartup(IServiceProvider services) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => AbastaliaServices.CreateDatabaseAsync(services, cancellationToken);
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
