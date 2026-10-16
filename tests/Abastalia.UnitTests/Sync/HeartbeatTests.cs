using Microsoft.Extensions.Configuration;
using Shouldly;
using Abastalia.Sync;

namespace Abastalia.UnitTests.Sync;

public sealed class HeartbeatTests
{
    /// <summary>R8 #21: an hourly heartbeat kept the cloud's serverless SQL awake all day; every 4 hours lets it pause.</summary>
    [Fact]
    public void The_heartbeat_goes_every_4_hours_unless_configured()
    {
        HeartbeatWorker.Interval(new ConfigurationBuilder().Build()).ShouldBe(TimeSpan.FromHours(4));
        HeartbeatWorker.Interval(new ConfigurationBuilder().AddInMemoryCollection([new("Sync:HeartbeatMinutes", "30")]).Build()).ShouldBe(TimeSpan.FromMinutes(30));
    }
}
