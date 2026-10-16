using System.Text.Json;
using Abastalia.Client.UI.Api;
using Abastalia.Hardware;

namespace Abastalia.Client.UI.State;

/// <summary>This PC's scale and printer (Configuración → Equipos de la caja). The settings live in the PC's local store, not on the
/// server: each register has its own devices. Null scale or printer: not connected (the weight is typed, the receipt only shown).</summary>
public sealed class Peripherals : IDisposable
{
    private const string Key = "hardware";
    private readonly ILocalStore _store;
    private readonly IHardwareDrivers _drivers;
    private readonly HardwareSettings _defaults;
    private bool _loaded;

    public Peripherals(ILocalStore store, IHardwareDrivers drivers, HardwareSettings defaults)
    {
        (_store, _drivers, _defaults) = (store, drivers, defaults);
        Apply(defaults);
    }

    public HardwareSettings Settings { get; private set; } = new();
    public IScale? Scale { get; private set; }
    public IReceiptPrinter? Printer { get; private set; }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var json = await _store.ReadAsync(Key);
        if (json is not null) Apply(JsonSerializer.Deserialize<HardwareSettings>(json, AbastaliaApi.Json) ?? _defaults);
    }

    public async Task SaveAsync(HardwareSettings settings)
    {
        await _store.SaveAsync(Key, JsonSerializer.Serialize(settings, AbastaliaApi.Json));
        _loaded = true;
        Apply(settings);
    }

    private (string?, int?)? _reported;

    /// <summary>TK-190: tells the store server whether this register has a scale and a printer (only the kind, never the port), once per
    /// change; the heartbeat counts them. Called while a session is open; a failure is simply retried on the next call.</summary>
    public async Task ReportAsync(AbastaliaApi api)
    {
        (string?, int?) now = (Settings.Scale?.Protocol.ToString(), Settings.Printer is { } p ? (p.Width == 58 ? 58 : 80) : null);
        if (_reported == now) return;
        if ((await api.PutAsync<Empty>("/api/identity/devices/current/peripherals", new { ScaleProtocol = now.Item1, PrinterWidth = now.Item2 })).Problem is null)
            _reported = now;
    }

    /// <summary>Prints and opens the drawer as asked; false when it failed or there is no printer. A failed print never undoes the sale.</summary>
    public async Task<bool> TryAsync(Func<IReceiptPrinter, Task> job)
    {
        if (Printer is null) return false;
        bool ok;
        try
        {
            await job(Printer);
            ok = true;
        }
        catch (HardwareException) { ok = false; }
        Report(ref _printerWorked, ok);
        return ok;
    }

    /// <summary>Reads the scale (every screen goes through here, so the status bar knows whether it answers).</summary>
    public async Task<ScaleReading> ReadScaleAsync(CancellationToken ct)
    {
        var r = Scale is { } scale ? await scale.ReadAsync(ct) : ScaleReading.Disconnected;
        Report(ref _scaleAnswers, r.Status != ScaleStatus.Disconnected);
        return r;
    }

    public enum DeviceState { Configured, Connected, Error }

    /// <summary>TK-243, for the status bar: null when there is none; Configured until it is first used (a print, a reading).</summary>
    public DeviceState? ScaleState => Scale is null ? null : State(_scaleAnswers);
    public DeviceState? PrinterState => Printer is null ? null : State(_printerWorked);

    /// <summary>A device was set, started answering or stopped answering.</summary>
    public event Action? Changed;

    private bool? _scaleAnswers, _printerWorked;

    private static DeviceState State(bool? worked) => worked switch { null => DeviceState.Configured, true => DeviceState.Connected, false => DeviceState.Error };

    private void Report(ref bool? field, bool worked)
    {
        if (field == worked) return;
        field = worked;
        Changed?.Invoke();
    }

    private void Apply(HardwareSettings s)
    {
        Release();
        (_scaleAnswers, _printerWorked) = (null, null);
        Settings = s;
        Scale = s.Scale is { } scale ? _drivers.CreateScale(scale) : null;
        Printer = s.Printer is { } printer ? _drivers.CreatePrinter(printer) : null;
        Changed?.Invoke();
    }

    private void Release()
    {
        (Scale as IDisposable)?.Dispose(); // the serial port, so the new settings can open it
        (Printer as IDisposable)?.Dispose();
    }

    public void Dispose() => Release();
}
