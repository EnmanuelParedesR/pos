using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Abastalia.BuildingBlocks;

/// <summary>TK-307: the real clock moved by <c>Clock:Shift</c> (a TimeSpan), only in the E2E environment, so the product video
/// (ProductVideoTests) always shows the same store hour. Everywhere else, and with no setting, the system clock.</summary>
public sealed class ShiftedClock(TimeSpan shift) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + shift;

    public static TimeProvider From(IConfiguration config, IHostEnvironment env) =>
        env.IsEnvironment("E2E") && config.GetValue<TimeSpan?>("Clock:Shift") is { } shift ? new ShiftedClock(shift) : System;
}
