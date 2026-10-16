using System.Text.Json;
using Shouldly;
using Abastalia.Host.Local;

namespace Abastalia.UnitTests.Platform;

/// <summary>TK-242: the store server restarts to update only when no shift is open, or outside business hours.</summary>
public sealed class UpdateWindowTests
{
    private static readonly TimeOnly Opens = new(7, 0), Closes = new(22, 0);

    [Theory]
    [InlineData(0, "12:00", true)]  // no shift open: any time
    [InlineData(2, "12:00", false)] // selling: never in the middle of the day
    [InlineData(1, "07:00", false)] // opening time counts as business hours
    [InlineData(1, "22:00", true)]  // closed, a shift left open overnight does not block it
    [InlineData(1, "03:00", true)]
    public void The_server_updates_without_open_shifts_or_after_hours(int openShifts, string now, bool canApply) =>
        UpdateWindow.CanApply(openShifts, TimeOnly.Parse(now), Opens, Closes).ShouldBe(canApply);

    [Theory]
    [InlineData(false, true, 0, "12:00", false)] // on its own: only outside business hours
    [InlineData(false, true, 1, "23:00", true)]
    [InlineData(false, false, 0, "23:00", false)] // "Actualizar fuera de horario" off: never on its own
    [InlineData(true, false, 0, "12:00", true)]  // "Actualizar ahora": as soon as no shift is open
    [InlineData(true, false, 1, "12:00", false)]
    [InlineData(true, false, 1, "23:00", true)]  // or after closing time
    public void TK254_the_owner_decides_when_the_server_updates(bool requested, bool outsideHours, int openShifts, string now, bool canApply) =>
        UpdateWindow.CanApply(requested, outsideHours, openShifts, TimeOnly.Parse(now), Opens, Closes).ShouldBe(canApply);

    [Theory]
    [InlineData("1.5.0", null, "1.5.0")]  // the cloud does not manage it: the feed's newest
    [InlineData("1.5.0", "1.4.0", "1.4.0")] // staged rollout: capped
    [InlineData("1.5.0", "1.6.0", "1.5.0")]
    [InlineData("1.5.0", "", null)]        // the cloud says wait
    [InlineData("1.5.0", "1.4", null)]
    [InlineData(null, "1.4.0", null)]
    public void TK254_the_cloud_caps_the_version(string? newest, string? allowed, string? target) =>
        (UpdateWindow.Target(newest is null ? null : Version.Parse(newest), allowed)?.ToString(3)).ShouldBe(target);

    [Fact]
    public void Business_hours_may_cross_midnight()
    {
        var (opens, closes) = (new TimeOnly(18, 0), new TimeOnly(2, 0));
        UpdateWindow.CanApply(1, new TimeOnly(23, 30), opens, closes).ShouldBeFalse();
        UpdateWindow.CanApply(1, new TimeOnly(1, 0), opens, closes).ShouldBeFalse();
        UpdateWindow.CanApply(1, new TimeOnly(10, 0), opens, closes).ShouldBeTrue();
    }

    [Fact]
    public void The_newest_full_release_of_the_feed_wins_and_deltas_are_ignored()
    {
        using var feed = JsonDocument.Parse("""
            {"Assets":[
              {"PackageId":"Abastalia","Version":"1.2.0","Type":"Full","FileName":"Abastalia-1.2.0-full.nupkg"},
              {"PackageId":"Abastalia","Version":"1.10.0-pilot","Type":"Full","FileName":"Abastalia-1.10.0-pilot-full.nupkg"},
              {"PackageId":"Abastalia","Version":"2.0.0","Type":"Delta","FileName":"Abastalia-2.0.0-delta.nupkg"}
            ]}
            """);
        UpdateWindow.Newest(feed.RootElement).ShouldBe(new Version(1, 10, 0));
        using var empty = JsonDocument.Parse("{}");
        UpdateWindow.Newest(empty.RootElement).ShouldBeNull();
    }
}
