using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;

namespace Abastalia.Lite.Tests;

public sealed class WorkingLiteApp : LiteApp
{
    protected override bool Workers => true;
}

/// <summary>TK-320: each business is its own node of the cloud (its own code, key and node id). The workers go over every business: the outbox
/// is delivered and uploaded by the business's own node, and the heartbeat and the license pull go once per node.</summary>
public sealed class WorkerTests(WorkingLiteApp app) : IClassFixture<WorkingLiteApp>
{
    [Fact]
    public async Task The_workers_go_over_every_business_each_as_its_own_node()
    {
        var a = await app.BusinessAsync("Dueña de A", "11111111");
        var b = await app.BusinessAsync("Dueño de B", "22222222");
        (a.CloudError, b.CloudError).ShouldBe((null, null));
        var nodeA = app.Cloud.Nodes["11111111"];
        var nodeB = app.Cloud.Nodes["22222222"];

        // Connecting queued each business's snapshot (its owner's name) in its own outbox.
        await UntilAsync(async () => await DoneAsync(a) && await DoneAsync(b));
        Uploaded(nodeA).ShouldBe(await IdsAsync(a), ignoreOrder: true);
        Uploaded(nodeB).ShouldBe(await IdsAsync(b), ignoreOrder: true);

        app.Clock.Advance(TimeSpan.FromMinutes(240)); // the heartbeat's interval
        await UntilAsync(() => Task.FromResult(new[] { nodeA, nodeB }.All(n => app.Cloud.Requests.Any(r => r.Node == n && r.Path == "/api/nodes/license"))));
        await UntilAsync(async () => await DoneAsync(a) && await DoneAsync(b) && Uploaded(nodeA).Count == (await IdsAsync(a)).Count);
        foreach (var (owner, node) in new[] { (a, nodeA), (b, nodeB) })
        {
            var heartbeats = await InScopeAsync(owner, db => db.Outbox.Where(m => m.Type.EndsWith("NodeHeartbeatV1")).Select(m => m.Id).ToListAsync());
            heartbeats.Count.ShouldBe(1);
            Uploaded(node).ShouldContain(heartbeats[0]);
        }
    }

    private List<Guid> Uploaded(Guid node) =>
        [.. app.Cloud.Requests.Where(r => r.Node == node && r.Path == "/ingest/v1/events")
            .SelectMany(r => r.Body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetGuid())).Distinct()];

    private Task<List<Guid>> IdsAsync(Owner o) => InScopeAsync(o, db => db.Outbox.Select(m => m.Id).ToListAsync());

    /// <summary>Every event of the business delivered in process and confirmed by the cloud.</summary>
    private Task<bool> DoneAsync(Owner o) => InScopeAsync(o, async db => await db.Outbox.AnyAsync() && !await db.Outbox.AnyAsync(m => m.ProcessedUtc == null || m.UploadedUtc == null));

    private async Task<T> InScopeAsync<T>(Owner o, Func<InfraDbContext, Task<T>> read)
    {
        await using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(o.TenantId);
        return await read(scope.ServiceProvider.GetRequiredService<InfraDbContext>());
    }

    /// <summary>Moves the fake clock a few seconds at a time (the workers' timers run on it) until the condition holds.</summary>
    private async Task UntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition()) return;
            app.Clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
        }
        throw new TimeoutException("The workers did not get there.");
    }
}
