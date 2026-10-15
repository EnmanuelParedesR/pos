using Toki.Client.UI;
using Toki.Client.UI.State;
using Toki.Client.Web;
using Toki.Hardware;
using Toki.Hardware.Simulated;

// The register screens in a browser, for development and the e2e checks. The store runs Toki.Client.Wpf (ADR-0007).
var builder = WebApplication.CreateBuilder(args);
// shortcut: one paired device token on this server, shared by every tab; so it only listens on this PC (even with --urls 0.0.0.0).
// The E2E environment may bind elsewhere. Give each browser its own device before using this host from a tablet.
if (!builder.Environment.IsEnvironment("E2E")) builder.WebHost.UseUrls("http://localhost:5280");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddTokiClient(new Uri(builder.Configuration["Api:Url"] ?? "http://localhost:5000"));
builder.Services.AddSingleton<ILocalStore>(new FileStore(
    builder.Configuration["Client:File"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki", "client-web.json")));
// Simulated scale and printer (development): receipts go to a text file; the scale weighs Hardware:Pounds.
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki");
var scale = new SimulatedScale { Pounds = builder.Configuration.GetValue("Hardware:Pounds", 2.5m) };
builder.Services.AddTokiHardware(new SimulatedDrivers(scale, builder.Configuration["Hardware:PrinterFile"] ?? Path.Combine(data, "receipts.txt")),
    new HardwareSettings(new PrinterSettings("Simulada", 80), new ScaleSettings("Simulada", ScaleProtocol.Continuous)));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Routes).Assembly);
app.Run();
