using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Abastalia.Fiscal;

namespace Abastalia.UnitTests.Fiscal;

/// <summary>TK-156 (D17): this PC's clock against the DGII's, from the <c>Date</c> header of its answers.</summary>
public sealed class DgiiClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    private static (DgiiClock Clock, FakeTimeProvider Time) Make(int? maxSeconds = null)
    {
        var time = new FakeTimeProvider(Now);
        var config = new ConfigurationBuilder().AddInMemoryCollection(maxSeconds is { } s
            ? [new KeyValuePair<string, string?>("Fiscal:MaxClockSkewSeconds", s.ToString(System.Globalization.CultureInfo.InvariantCulture))] : []).Build();
        return (new DgiiClock(config, time), time);
    }

    [Fact]
    public void Unknown_never_blocks_and_past_two_minutes_either_way_it_does()
    {
        var (clock, _) = Make();
        (clock.Skew, clock.IsOff, clock.Problem, clock.Stale).ShouldBe((null, false, null, true));

        clock.Observe(Now - TimeSpan.FromSeconds(119)); // this PC 1:59 ahead
        (clock.IsOff, clock.Problem).ShouldBe((false, null));

        clock.Observe(Now - TimeSpan.FromMinutes(3));
        clock.Skew.ShouldBe(TimeSpan.FromMinutes(3));
        clock.Problem.ShouldBe("La hora de la PC principal está adelantada 3 minutos: corríjala en Windows");

        clock.Observe(Now + TimeSpan.FromSeconds(170));
        clock.Problem.ShouldBe("La hora de la PC principal está atrasada 3 minutos: corríjala en Windows");
    }

    [Fact]
    public void The_threshold_is_configurable_and_it_is_measured_again_sooner_while_off()
    {
        var (clock, time) = Make(maxSeconds: 30);
        clock.Observe(Now - TimeSpan.FromSeconds(60));
        clock.Problem.ShouldBe("La hora de la PC principal está adelantada 1 minuto: corríjala en Windows");
        clock.Stale.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(61));
        clock.Stale.ShouldBeTrue(); // every minute while off

        clock.Observe(time.GetUtcNow());
        time.Advance(TimeSpan.FromMinutes(5));
        clock.Stale.ShouldBeFalse(); // every 10 minutes while fine
        time.Advance(TimeSpan.FromMinutes(6));
        clock.Stale.ShouldBeTrue();
    }

    [Fact]
    public async Task The_handler_reads_the_Date_header_of_each_DGII_answer()
    {
        var (clock, _) = Make();
        using var handler = new DgiiClockHandler(clock) { InnerHandler = new Answer(Now - TimeSpan.FromMinutes(4)) };
        using var http = new HttpClient(handler);
        (await http.GetAsync("https://ecf.dgii.gov.do/testecf/autenticacion/api/autenticacion/semilla")).StatusCode.ShouldBe(HttpStatusCode.OK);
        clock.Skew.ShouldBe(TimeSpan.FromMinutes(4));
    }

    private sealed class Answer(DateTimeOffset date) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Headers = { Date = date } });
    }
}
