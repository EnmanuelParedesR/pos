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
        // shortcut: the store API's self-signed node certificate is not pinned yet (TK-091); until then use http on the store LAN or a trusted certificate.
        services.AddTokiClient(api);
        services.AddSingleton<ILocalStore, ProtectedStore>();
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
        WebView.BlazorWebViewInitialized += (_, e) => LockDown(e.WebView.CoreWebView2.Settings);
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
