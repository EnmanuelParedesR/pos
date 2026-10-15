using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Toki.Client.UI;
using Toki.Client.UI.Estado;

namespace Toki.Client.Wpf;

/// <summary>The register app: a WPF window hosting the Blazor screens (Blazor Hybrid, ADR-0007). It talks to the store's local API.</summary>
public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))).RootElement;
        var api = new Uri(config.GetProperty("Api").GetProperty("Url").GetString()!);

        var servicios = new ServiceCollection();
        servicios.AddWpfBlazorWebView();
        // shortcut: the store API's self-signed node certificate is not pinned yet (TK-091); until then use http on the store LAN or a trusted certificate.
        servicios.AddTokiCliente(api);
        servicios.AddSingleton<IAlmacenLocal, AlmacenProtegido>();
#if DEBUG
        servicios.AddBlazorWebViewDeveloperTools();
#endif
        Vista.Services = servicios.BuildServiceProvider();
    }
}
