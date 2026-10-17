using System.Security.Cryptography;
using Shouldly;
using Abastalia.Settings;

namespace Abastalia.UnitTests.Settings;

/// <summary>TK-279: turning the PC's clock back never stretches a license.</summary>
public sealed class LicenseClockTests
{
    /// <summary>A PC clock the owner can turn back; the uptime counter only moves forward.</summary>
    private sealed class PcClock(DateTime start) : TimeProvider
    {
        private long _ticks;
        public DateTime Wall { get; set; } = start;

        public void Run(TimeSpan t)
        {
            Wall += t;
            _ticks += t.Ticks;
        }

        public override DateTimeOffset GetUtcNow() => new(Wall, TimeSpan.Zero);
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private static readonly DateTime Start = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Turning_the_clock_back_does_not_turn_the_license_time_back_and_time_keeps_running()
    {
        var pc = new PcClock(Start);
        var clock = new LicenseClock(pc, null);
        clock.UtcNow.ShouldBe(Start);
        pc.Run(TimeSpan.FromDays(10));
        clock.UtcNow.ShouldBe(Start.AddDays(10));
        clock.Behind.ShouldBeFalse();

        pc.Wall = Start; // back 10 days
        clock.UtcNow.ShouldBe(Start.AddDays(10));
        clock.Behind.ShouldBeTrue();
        pc.Run(TimeSpan.FromDays(3)); // the store keeps working on the wrong date: its license still ages
        clock.UtcNow.ShouldBe(Start.AddDays(13));
        clock.Behind.ShouldBeTrue();

        pc.Wall = Start.AddDays(13) + TimeSpan.FromMinutes(1); // fixed
        clock.UtcNow.ShouldBe(pc.Wall);
        clock.Behind.ShouldBeFalse();
    }

    [Fact]
    public void Small_corrections_within_the_tolerance_follow_the_PC_and_never_pile_up()
    {
        var pc = new PcClock(Start);
        var clock = new LicenseClock(pc, null);
        for (var i = 0; i < 100; i++)
        {
            pc.Run(TimeSpan.FromHours(1));
            pc.Wall -= TimeSpan.FromMinutes(2); // time sync corrects a fast clock every hour
            clock.UtcNow.ShouldBe(pc.Wall);
            clock.Behind.ShouldBeFalse();
        }
    }

    [Fact]
    public void The_saved_time_after_a_restart_and_a_known_outside_time_hold_the_floor()
    {
        var pc = new PcClock(Start);
        var restarted = new LicenseClock(pc, Start.AddDays(40)); // the server saw day 40 before the clock was turned back and restarted
        restarted.UtcNow.ShouldBe(Start.AddDays(40));
        restarted.Behind.ShouldBeTrue();

        var dgii = new LicenseClock(pc, null);
        dgii.Observe(Start.AddDays(5)); // the DGII's HTTP Date, or a signed license's issue date
        dgii.UtcNow.ShouldBe(Start.AddDays(5));
        dgii.Behind.ShouldBeTrue();
    }

    [Fact]
    public void A_saved_time_edited_in_the_database_is_ignored()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var seal = LicenseClock.Seal(Start, key);
        LicenseClock.Unseal(Start, seal, key).ShouldBe(Start);
        LicenseClock.Unseal(Start.AddDays(-30), seal, key).ShouldBeNull(); // moved back by hand
        LicenseClock.Unseal(Start, seal, RandomNumberGenerator.GetBytes(32)).ShouldBeNull(); // another server's secret
        LicenseClock.Unseal(Start, null, key).ShouldBeNull();
        LicenseClock.Unseal(null, seal, key).ShouldBeNull();
    }
}
