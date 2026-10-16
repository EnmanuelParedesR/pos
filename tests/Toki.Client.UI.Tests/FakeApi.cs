using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Toki.Client.UI;
using Toki.Client.UI.Api;
using Toki.Hardware;
using Toki.Client.UI.State;

namespace Toki.Client.UI.Tests;

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
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, TokiApi.Json), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail, object? errors = null) =>
        Response(status, new { status = (int)status, code, detail, errors });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = request.RequestUri!.PathAndQuery;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_requests) _requests.Add((request.Method.Method, route, body));
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
    public static (BunitContext Ctx, FakeApi Api) Create(params string[] permissions)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new FakeApi();
        ctx.Services.AddTokiClient(new Uri("http://toki.test"), api);
        ctx.Services.AddSingleton<ILocalStore>(new InMemoryStore());
        // No scale or printer until a test saves its settings (Peripherals.SaveAsync); TestDrivers lets it drive and watch them.
        var hardware = new TestDrivers();
        ctx.Services.AddSingleton(hardware);
        ctx.Services.AddTokiHardware(hardware);
        var session = ctx.Services.GetRequiredService<Session>();
        session.PairAsync(null, "device").GetAwaiter().GetResult();
        session.Open(new OpenedSession("token", Guid.NewGuid(), "Yudelka M.", DateTime.UtcNow.AddHours(12)), new Me(Guid.NewGuid(), "Yudelka M.", Guid.NewGuid(), permissions));
        session.Shift = Shift;
        return (ctx, api);
    }
}

/// <summary>A scale the test moves by hand and a printer that only records what it was sent.</summary>
internal sealed class TestDrivers : IHardwareDrivers
{
    public Toki.Hardware.Simulated.SimulatedScale Scale { get; } = new();
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
}
