using System.Text.Json;
using Toki.Client.UI.Api;
using Toki.Hardware;

namespace Toki.Client.UI.State;

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
        if (json is not null) Apply(JsonSerializer.Deserialize<HardwareSettings>(json, TokiApi.Json) ?? _defaults);
    }

    public async Task SaveAsync(HardwareSettings settings)
    {
        await _store.SaveAsync(Key, JsonSerializer.Serialize(settings, TokiApi.Json));
        _loaded = true;
        Apply(settings);
    }

    /// <summary>Prints and opens the drawer as asked; false when it failed or there is no printer. A failed print never undoes the sale.</summary>
    public async Task<bool> TryAsync(Func<IReceiptPrinter, Task> job)
    {
        if (Printer is null) return false;
        try
        {
            await job(Printer);
            return true;
        }
        catch (HardwareException) { return false; }
    }

    private void Apply(HardwareSettings s)
    {
        Release();
        Settings = s;
        Scale = s.Scale is { } scale ? _drivers.CreateScale(scale) : null;
        Printer = s.Printer is { } printer ? _drivers.CreatePrinter(printer) : null;
    }

    private void Release()
    {
        (Scale as IDisposable)?.Dispose(); // the serial port, so the new settings can open it
        (Printer as IDisposable)?.Dispose();
    }

    public void Dispose() => Release();
}
