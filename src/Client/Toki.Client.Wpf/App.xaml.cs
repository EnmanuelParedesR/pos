using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Toki.Client.Wpf;

public partial class App : Application
{
    private static readonly string Log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki", "errors.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // An unexpected failure is written down and explained; the register keeps running instead of closing with the sale on screen.
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
            File.AppendAllText(Log, $"{DateTime.UtcNow:O} {e.Exception}{Environment.NewLine}");
        }
        catch (IOException) { } // the log is a help, not a reason to fail again
        MessageBox.Show("Algo falló en Toki. La venta en curso sigue en pantalla; si algo no responde, cierre y abra Toki de nuevo.",
            "Toki", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
