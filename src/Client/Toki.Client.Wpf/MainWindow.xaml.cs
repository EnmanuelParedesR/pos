using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Toki.Client.UI;
using Toki.Client.UI.State;
using Toki.Hardware;
using Toki.Hardware.Simulated;

namespace Toki.Client.Wpf;

/// <summary>The register app: a WPF window hosting the Blazor screens (Blazor Hybrid, ADR-0007). It talks to the store's local API.</summary>
public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))).RootElement;
        var api = new Uri(config.GetProperty("Api").GetProperty("Url").GetString()!);

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        // Over https the store server's self-signed certificate is pinned when this PC pairs (TK-122, ADR-0009).
        services.AddTokiClient(api);
        services.AddSingleton<ILocalStore, ProtectedStore>();
        services.AddSingleton<IDeviceKeys, CngDeviceKeys>();
        // Simulated scale and printer in Debug builds, the real drivers in Release; appsettings "Hardware": { "Simulated": true|false } overrides it.
#if DEBUG
        var simulate = true;
#else
        var simulate = false;
#endif
        if (config.TryGetProperty("Hardware", out var hw) && hw.TryGetProperty("Simulated", out var simulated)) simulate = simulated.GetBoolean();
        if (simulate)
            services.AddTokiHardware(new SimulatedDrivers(new SimulatedScale(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki", "receipts.txt")),
                new HardwareSettings(new PrinterSettings("Simulada"), new ScaleSettings("Simulada", ScaleProtocol.Continuous)));
        else
            services.AddTokiHardware(new RealDrivers());
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        WebView.Services = services.BuildServiceProvider();
        // R11 C6: the app lives in Program Files (read-only for the cashier): WebView2's data goes to the user's profile, not next to Toki.exe.
        WebView.BlazorWebViewInitializing += (_, e) =>
            e.UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki", "WebView2");
        WebView.BlazorWebViewInitialized += (_, e) =>
        {
            LockDown(e.WebView.CoreWebView2.Settings);
            e.WebView.CoreWebView2.DownloadStarting += AskWhereToSave;
        };
    }

    /// <summary>A report download (escuadra.download): the owner picks the folder, like in any Windows program, instead of a silent save to Downloads.</summary>
    private void AskWhereToSave(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2DownloadStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(() =>
        {
            using (deferral)
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(e.ResultFilePath), Title = "Guardar reporte" };
                if (dialog.ShowDialog(this) == true)
                {
                    e.ResultFilePath = dialog.FileName;
                    e.Handled = true; // no download bubble: the file is where the owner chose
                }
                else
                {
                    e.Cancel = true;
                }
            }
        });
    }

    /// <summary>The register is an app, not a browser: a reload (Ctrl+R, F5, swipe back) would start a new DI scope and lose the
    /// session and the cart mid-sale; zoom would break the 48/72 px touch targets.</summary>
    private static void LockDown(Microsoft.Web.WebView2.Core.CoreWebView2Settings s)
    {
#if !DEBUG
        s.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+R, Ctrl+F5, Ctrl+P, Ctrl+F, Alt+←; kept in Debug for the dev tools (F12)
#endif
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
    }
}
