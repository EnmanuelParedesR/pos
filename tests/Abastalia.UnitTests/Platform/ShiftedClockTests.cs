using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using Abastalia.BuildingBlocks;

namespace Abastalia.UnitTests.Platform;

/// <summary>R13: Clock:Shift (TK-307) only ever moves the clock in the E2E environment; in Production, Development or Staging it is ignored.</summary>
public sealed class ShiftedClockTests
{
    private static readonly IConfiguration Shift = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Clock:Shift"] = "-1.00:00:00" }).Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("e2e-not")]
    public void Outside_E2E_the_setting_is_ignored(string environment) =>
        ShiftedClock.From(Shift, new HostingEnvironment { EnvironmentName = environment }).ShouldBeSameAs(TimeProvider.System);

    [Fact]
    public void In_E2E_it_moves_the_clock_and_without_the_setting_it_does_not()
    {
        var e2e = new HostingEnvironment { EnvironmentName = "E2E" };
        var shifted = ShiftedClock.From(Shift, e2e);
        shifted.ShouldBeOfType<ShiftedClock>();
        (TimeProvider.System.GetUtcNow() - shifted.GetUtcNow()).TotalHours.ShouldBeInRange(23.9, 24.1);
        ShiftedClock.From(new ConfigurationBuilder().Build(), e2e).ShouldBeSameAs(TimeProvider.System);
    }
}
