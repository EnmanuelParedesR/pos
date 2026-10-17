using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Abastalia.Client.UI;
using Abastalia.Client.UI.Api;
using Abastalia.Hardware;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.UI.Tests;

/// <summary>The local API as the screens see it: each test answers the routes it cares about and reads what was sent.</summary>
internal sealed class FakeApi : HttpMessageHandler
{
    private readonly List<(string Method, string Route, Func<string, Task<HttpResponseMessage>> Respond)> _routes = [];
    private readonly List<(string Method, string Route, string Body)> _requests = [];

    /// <summary>What the screens sent, in order (a copy: the frame's health check may add to it meanwhile).</summary>
    public List<(string Method, string Route, string Body)> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    /// <summary>TK-343: the requests sent as a Talia proposal (route and the X-Talia-Proposal id).</summary>
    public List<(string Route, string Id)> Proposals { get; } = [];

    /// <summary>A throwing answer (<c>throw new TaskCanceledException()</c>) is a timeout: the request may or may not have been saved.</summary>
    public FakeApi Respond(string method, string route, Func<string, HttpResponseMessage> respond) =>
        RespondAsync(method, route, body => Task.FromResult(respond(body)));

    public FakeApi RespondAsync(string method, string route, Func<string, Task<HttpResponseMessage>> respond)
    {
        lock (_routes) _routes.Insert(0, (method, route, respond)); // the latest wins, so a test can change an answer midway
        return this;
    }

    public FakeApi Json(string method, string route, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond(method, route, _ => Response(status, body));

    public static HttpResponseMessage Response(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, AbastaliaApi.Json), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail, object? errors = null) =>
        Response(status, new { status = (int)status, code, detail, errors });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.PathAndQuery;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_requests) _requests.Add((request.Method.Method, route, body));
        if (request.Headers.TryGetValues("X-Talia-Proposal", out var proposal)) lock (_requests) Proposals.Add((route, proposal.Single()));
        (string Method, string Route, Func<string, Task<HttpResponseMessage>> Respond)[] routes;
        lock (_routes) routes = [.. _routes];
        foreach (var r in routes)
            if (r.Method == request.Method.Method && (route == r.Route || route.StartsWith(r.Route + "?", StringComparison.Ordinal)))
                return await r.Respond(body);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

internal sealed class InMemoryStore : ILocalStore
{
    private readonly Dictionary<string, string> _data = [];
    public Task<string?> ReadAsync(string key) => Task.FromResult(_data.GetValueOrDefault(key));
    public Task SaveAsync(string key, string? value)
    {
        if (value is null) _data.Remove(key);
        else _data[key] = value;
        return Task.CompletedTask;
    }
}

internal static class ScreenContext
{
    public static readonly CurrentShift Shift = new(Guid.NewGuid(), Guid.NewGuid(), "Caja 1", 500m, DateTime.UtcNow);

    /// <summary>A bUnit context with the register's services on a fake API, a paired device and a logged-in cashier.</summary>
    public static Task<(BunitContext Ctx, FakeApi Api)> CreateAsync(params string[] permissions) => CreateAsync(_ => { }, permissions);

    /// <param name="services">Services a test adds before anything is resolved (an app updater, a fake clock).</param>
    public static async Task<(BunitContext Ctx, FakeApi Api)> CreateAsync(Action<IServiceCollection> services, params string[] permissions)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new FakeApi();
        // TK-305: screens tick only when a test advances this clock (the scale, the registry import); the frame's first check runs at once.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        ctx.Services.AddSingleton(clock);
        ctx.Services.AddSingleton<TimeProvider>(clock);
        ctx.Services.AddAbastaliaClient(new Uri("http://abastalia.test"), api);
        ctx.Services.AddSingleton<ILocalStore>(new InMemoryStore());
        // No scale or printer until a test saves its settings (Peripherals.SaveAsync); TestDrivers lets it drive and watch them.
        var hardware = new TestDrivers();
        ctx.Services.AddSingleton(hardware);
        ctx.Services.AddAbastaliaHardware(hardware);
        services(ctx.Services);
        var session = ctx.Services.GetRequiredService<Session>();
        await session.PairAsync(null, "device");
        session.Open(new OpenedSession("token", Guid.NewGuid(), "Yudelka M.", DateTime.UtcNow.AddHours(12)), new Me(Guid.NewGuid(), "Yudelka M.", Guid.NewGuid(), permissions));
        session.Shift = Shift;
        return (ctx, api);
    }
}

/// <summary>A scale the test moves by hand and a printer that only records what it was sent.</summary>
internal sealed class TestDrivers : IHardwareDrivers
{
    public Abastalia.Hardware.Simulated.SimulatedScale Scale { get; } = new();
    public RecordingPrinter Printer { get; } = new();
    public IScale CreateScale(ScaleSettings settings) => Scale;
    public IReceiptPrinter CreatePrinter(PrinterSettings settings) => Printer;
}

internal sealed class RecordingPrinter : IReceiptPrinter
{
    private readonly List<(Receipt Receipt, bool OpenDrawer)> _printed = [];
    private int _drawer;

    public bool Offline { get; set; }
    public List<(Receipt Receipt, bool OpenDrawer)> Printed { get { lock (_printed) return [.. _printed]; } }
    public int DrawerOpenings => Volatile.Read(ref _drawer);

    public Task PrintAsync(Receipt receipt, bool openDrawer, CancellationToken ct)
    {
        if (Offline) throw new HardwareException("offline", null);
        lock (_printed) _printed.Add((receipt, openDrawer));
        return Task.CompletedTask;
    }

    public Task OpenDrawerAsync(CancellationToken ct)
    {
        if (Offline) throw new HardwareException("offline", null);
        Interlocked.Increment(ref _drawer);
        return Task.CompletedTask;
    }

    private readonly List<ShiftReport> _reports = [];
    public List<ShiftReport> Reports { get { lock (_reports) return [.. _reports]; } }

    public Task PrintReportAsync(ShiftReport report, CancellationToken ct)
    {
        if (Offline) throw new HardwareException("offline", null);
        lock (_reports) _reports.Add(report);
        return Task.CompletedTask;
    }
}
