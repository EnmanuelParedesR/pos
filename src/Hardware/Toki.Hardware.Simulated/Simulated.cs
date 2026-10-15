using Toki.Hardware.EscPos;

namespace Toki.Hardware.Simulated;

/// <summary>A scale that answers whatever weight and status it is told (development, the web host, the screen tests).</summary>
public sealed class SimulatedScale : IScale
{
    public decimal Pounds { get; set; } = 2.5m;
    public ScaleStatus Status { get; set; } = ScaleStatus.Stable;

    public Task<ScaleReading> ReadAsync(CancellationToken ct) => Task.FromResult(new ScaleReading(Pounds, Status));
}

/// <summary>A printer that appends the receipt, laid out as the thermal printer would print it, to a text file.</summary>
public sealed class FilePrinter(string file, int columns) : IReceiptPrinter
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public Task PrintAsync(Receipt receipt, bool openDrawer, CancellationToken ct) =>
        AppendAsync(EscPos.EscPos.Text(receipt, columns) + (openDrawer ? "[gaveta abierta]\n" : "") + new string('=', columns) + "\n", ct);

    public Task OpenDrawerAsync(CancellationToken ct) => AppendAsync("[gaveta abierta]\n", ct);

    private async Task AppendAsync(string text, CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            await File.AppendAllTextAsync(file, text, ct);
        }
        catch (IOException e) { throw new HardwareException($"Simulated printer {file}: {e.Message}", e); }
        finally { Lock.Release(); }
    }
}

/// <summary>Simulated peripherals whatever the settings say: one scale shared by every screen, receipts to <paramref name="printerFile"/>.</summary>
public sealed class SimulatedDrivers(SimulatedScale scale, string printerFile) : IHardwareDrivers
{
    public IScale CreateScale(ScaleSettings settings) => scale;
    public IReceiptPrinter CreatePrinter(PrinterSettings settings) => new FilePrinter(printerFile, settings.Columns);
}
