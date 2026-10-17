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
    public void An_outside_time_ahead_of_the_clock_counts_the_interval_once()
    {
        // R12: a store offline from the cloud, left on overnight: the DGII's time raises the clock without adding the night twice.
        var pc = new PcClock(Start);
        var clock = new LicenseClock(pc, null);
        clock.UtcNow.ShouldBe(Start);
        pc.Run(TimeSpan.FromHours(12));
        clock.Observe(Start.AddHours(12));
        clock.UtcNow.ShouldBe(Start.AddHours(12));
        clock.Behind.ShouldBeFalse();
        clock.Observe(Start.AddHours(1)); // older than the clock: changes nothing
        clock.UtcNow.ShouldBe(Start.AddHours(12));
    }

    [Fact]
    public void A_clock_set_back_a_little_again_and_again_does_not_freeze_the_time()
    {
        // R12: a script turning the clock back 4 minutes every 4 minutes (within the tolerance each time).
        var pc = new PcClock(Start);
        var clock = new LicenseClock(pc, null);
        clock.UtcNow.ShouldBe(Start);
        for (var i = 0; i < 100; i++)
        {
            pc.Run(TimeSpan.FromMinutes(4));
            pc.Wall -= TimeSpan.FromMinutes(4);
            _ = clock.UtcNow;
        }
        clock.UtcNow.ShouldBeGreaterThanOrEqualTo(Start.AddMinutes(400) - LicenseClock.Tolerance);
        clock.Behind.ShouldBeTrue();
    }

    [Fact]
    public void A_trusted_time_brings_back_down_a_clock_far_ahead_and_raises_one_behind()
    {
        // R12: the PC once booted in 2030; the store saw that date. The DGII says it is now.
        var pc = new PcClock(Start);
        var clock = new LicenseClock(pc, Start.AddYears(4));
        clock.UtcNow.ShouldBe(Start.AddYears(4));
        clock.Correct(Start);
        clock.UtcNow.ShouldBe(Start);
        clock.Behind.ShouldBeFalse();

        clock.Correct(Start.AddHours(-12)); // within a day of it (the DGII's last answer is a little old): kept
        clock.UtcNow.ShouldBe(Start);
        clock.Correct(Start.AddDays(3)); // ahead: raised, like Observe
        clock.UtcNow.ShouldBe(Start.AddDays(3));
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
