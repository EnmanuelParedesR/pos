using Microsoft.Extensions.Configuration;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal;

/// <summary>TK-156 (D17, docs/ecf-propio.md §11 #12): how far this PC's clock is from the DGII's, read from the HTTP <c>Date</c> header of every
/// DGII answer (<see cref="DgiiClockHandler"/>). The DGII rejects a <c>FechaHoraFirma</c> later than its own time, so past
/// <c>Fiscal:MaxClockSkewSeconds</c> (120) either way nothing new is signed: sales go on and their e-CF wait unsigned until the clock is fixed
/// (<see cref="FiscalDocuments.SignWaitingAsync"/>). Not measured yet (no DGII answer) never blocks.
/// The other clock rule is R11 A18 (<c>Clock.Behind</c>): behind the newest audit record by 5 minutes nothing is saved at all.</summary>
internal sealed class DgiiClock(IConfiguration config, TimeProvider clock) : Contracts.IDgiiTime
{
    public DateTime? LastDgiiUtc => _last is { } m ? (m.AtUtc - m.Skew).UtcDateTime : null;

    /// <summary>The worker asks the DGII (the public seed) when the last answer is older than this; every minute while the clock is off,
    /// so the warning clears soon after the owner fixes the time.</summary>
    public static readonly TimeSpan MeasureEvery = TimeSpan.FromMinutes(10), MeasureEveryWhileOff = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _max = TimeSpan.FromSeconds(config.GetValue("Fiscal:MaxClockSkewSeconds", 120));
    private sealed record Measure(TimeSpan Skew, DateTimeOffset AtUtc);
    private volatile Measure? _last;

    /// <summary>This PC minus the DGII: positive when this PC is ahead. Null until the DGII answers once.</summary>
    public TimeSpan? Skew => _last?.Skew;

    // shortcut: ignores the round trip and the header's 1-second resolution, both far under the 2-minute threshold.
    public void Observe(DateTimeOffset dgiiNow)
    {
        var now = clock.GetUtcNow();
        _last = new Measure(now - dgiiNow, now);
    }

    public bool IsOff => _last is { } m && m.Skew.Duration() > _max;

    /// <summary>Time to ask again. Duration(): fixing the clock moves it either way.</summary>
    public bool Stale => _last is not { } m || (clock.GetUtcNow() - m.AtUtc).Duration() > (IsOff ? MeasureEveryWhileOff : MeasureEvery);

    /// <summary>What the register and the owner read, or null while the clock is fine (or not measured yet).</summary>
    public string? Problem => _last is { } m && m.Skew.Duration() > _max
        ? $"La hora de la PC principal está {(m.Skew > TimeSpan.Zero ? "adelantada" : "atrasada")} {Minutes(m.Skew)}: corríjala en Windows"
        : null;

    private static string Minutes(TimeSpan skew) => (int)Math.Round(skew.Duration().TotalMinutes) is var m && m == 1 ? "1 minuto" : $"{m} minutos";
}

/// <summary>On the DGII HttpClient only (never on the buyers' receivers): every answer's <c>Date</c> header measures <see cref="DgiiClock"/>.</summary>
internal sealed class DgiiClockHandler(DgiiClock dgiiClock) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.Date is { } date) dgiiClock.Observe(date);
        return response;
    }
}

/// <summary>TK-156: signs the e-CF that waited for the clock (the worker runs it once the clock is right again). A command, so the
/// <see cref="Contracts.FiscalDocumentSignedV1"/> events that give the sales their security code and QR are saved with the signatures.</summary>
internal static class SignWaiting
{
    public sealed record Command : ICommand<int>;

    internal sealed class Handler(FiscalDocuments documents) : ICommandHandler<Command, int>
    {
        public async Task<Result<int>> Handle(Command c, CancellationToken ct) => await documents.SignWaitingAsync(ct);
    }
}
