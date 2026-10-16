using Abastalia.Hardware;
using Abastalia.Hardware.EscPos;
using Abastalia.Hardware.Scales;

namespace Abastalia.Client.Wpf;

/// <summary>The store's peripherals: serial scales and ESC/POS printers, as set in Configuración → Equipos de la caja.</summary>
internal sealed class RealDrivers : IHardwareDrivers
{
    public IScale CreateScale(ScaleSettings settings) => new SerialScale(settings);
    public IReceiptPrinter CreatePrinter(PrinterSettings settings) => new EscPosPrinter(settings);
}
