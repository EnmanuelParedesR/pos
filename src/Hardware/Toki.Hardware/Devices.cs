namespace Toki.Hardware;

public enum ScaleStatus { Stable, Unstable, Overload, Disconnected }

/// <summary>One reading of the scale, always in pounds (a scale set to kilograms is converted).</summary>
public sealed record ScaleReading(decimal Pounds, ScaleStatus Status)
{
    public static readonly ScaleReading Disconnected = new(0m, ScaleStatus.Disconnected);

    /// <summary>A weight that can go on a sale line.</summary>
    public bool IsUsable => Status == ScaleStatus.Stable && Pounds > 0;
}

public interface IScale
{
    /// <summary>The weight right now. A missing, silent or garbled scale is <see cref="ScaleStatus.Disconnected"/>, never an exception.</summary>
    Task<ScaleReading> ReadAsync(CancellationToken ct);
}

public interface IReceiptPrinter
{
    /// <summary>Prints the receipt and cuts the paper; <paramref name="openDrawer"/> kicks the drawer in the same job. Throws <see cref="HardwareException"/>.</summary>
    Task PrintAsync(Receipt receipt, bool openDrawer, CancellationToken ct);

    /// <summary>The cash drawer hangs off the printer (RJ-11 port): opening it is a printer command. Throws <see cref="HardwareException"/>.</summary>
    Task OpenDrawerAsync(CancellationToken ct);

    /// <summary>TK-239: prints the shift's Z (or X) report and cuts the paper. Throws <see cref="HardwareException"/>.</summary>
    Task PrintReportAsync(ShiftReport report, CancellationToken ct);
}

public sealed class HardwareException(string message, Exception? inner) : Exception(message, inner);

public enum ScaleProtocol { Continuous, Cas, Toledo8217 }

/// <param name="Port">The COM port, e.g. COM3 (a USB scale shows up as one too).</param>
public sealed record ScaleSettings(string Port, ScaleProtocol Protocol, int BaudRate = 9600);

/// <param name="Port">A Windows printer name (sent raw through the spooler) or a COM port (COM4) for serial and USB-serial printers.</param>
/// <param name="Width">Paper width in mm: 58 or 80.</param>
public sealed record PrinterSettings(string Port, int Width = 80, int BaudRate = 9600)
{
    /// <summary>Characters per line in the printer's standard font (font A).</summary>
    public int Columns => Width == 58 ? 32 : 48;
}

/// <summary>This PC's peripherals; kept on the PC itself, not on the store server. Null: not connected.</summary>
public sealed record HardwareSettings(PrinterSettings? Printer = null, ScaleSettings? Scale = null, bool PrintAfterSale = true);

/// <summary>Each host gives the real drivers (the store's WPF app) or the simulated ones (development, the web host).</summary>
public interface IHardwareDrivers
{
    IScale CreateScale(ScaleSettings settings);
    IReceiptPrinter CreatePrinter(PrinterSettings settings);
}
