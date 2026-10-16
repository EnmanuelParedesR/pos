using System.Text.Json;
using Shouldly;
using Toki.Host.Local;

namespace Toki.UnitTests.Platform;

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
              {"PackageId":"Toki","Version":"1.2.0","Type":"Full","FileName":"Toki-1.2.0-full.nupkg"},
              {"PackageId":"Toki","Version":"1.10.0-pilot","Type":"Full","FileName":"Toki-1.10.0-pilot-full.nupkg"},
              {"PackageId":"Toki","Version":"2.0.0","Type":"Delta","FileName":"Toki-2.0.0-delta.nupkg"}
            ]}
            """);
        UpdateWindow.Newest(feed.RootElement).ShouldBe(new Version(1, 10, 0));
        using var empty = JsonDocument.Parse("{}");
        UpdateWindow.Newest(empty.RootElement).ShouldBeNull();
    }
}
