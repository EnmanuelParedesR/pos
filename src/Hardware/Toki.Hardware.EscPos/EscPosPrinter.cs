using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace Toki.Hardware.EscPos;

/// <summary>An ESC/POS thermal printer: installed in Windows (any port: USB, network, LPT) and fed raw through the spooler,
/// or on a COM port (serial, or USB with a virtual COM port).</summary>
public sealed class EscPosPrinter(PrinterSettings settings) : IReceiptPrinter
{
    public Task PrintAsync(Receipt receipt, bool openDrawer, CancellationToken ct) =>
        SendAsync(EscPos.Render(receipt, settings.Columns, openDrawer), ct);

    public Task OpenDrawerAsync(CancellationToken ct) => SendAsync(EscPos.DrawerKick, ct);

    private Task SendAsync(byte[] data, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            if (IsComPort(settings.Port)) SendSerial(data);
            else RawSpooler.Send(settings.Port, data);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception)
        {
            throw new HardwareException($"Printer {settings.Port}: {e.Message}", e);
        }
    }, ct);

    internal static bool IsComPort(string port) =>
        port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && port.Length > 3 && port[3..].All(char.IsAsciiDigit);

    private void SendSerial(byte[] data)
    {
        using var port = new SerialPort(settings.Port, settings.BaudRate) { WriteTimeout = 5000 }; // no flow control: a receipt fits the printer's buffer
        port.Open();
        port.Write(data, 0, data.Length);
    }
}

/// <summary>Bytes straight to a Windows printer (datatype RAW): the driver does not render them, the printer reads the ESC/POS commands.</summary>
internal static class RawSpooler
{
    public static void Send(string printer, byte[] data)
    {
        if (!OpenPrinter(printer, out var handle, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (StartDocPrinter(handle, 1, new DocInfo { Name = "Toki", DataType = "RAW" }) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                if (!StartPagePrinter(handle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (!WritePrinter(handle, data, data.Length, out var written) || written != data.Length) throw new Win32Exception(Marshal.GetLastPInvokeError());
                EndPagePrinter(handle);
            }
            finally { EndDocPrinter(handle); }
        }
        finally { ClosePrinter(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        public string? Name;
        public string? OutputFile;
        public string? DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr handle, int level, [In] DocInfo info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, byte[] data, int count, out int written);
}
