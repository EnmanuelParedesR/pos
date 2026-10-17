using Abastalia.Client.UI;
using Abastalia.Client.UI.State;
using Abastalia.Client.Web;
using Abastalia.Hardware;
using Abastalia.Hardware.Simulated;

// The register screens in a browser, for development and the e2e checks. The store runs Abastalia.Client.Wpf (ADR-0007).
var builder = WebApplication.CreateBuilder(args);
// shortcut: one paired device token on this server, shared by every tab; so it only listens on this PC (even with --urls 0.0.0.0).
// The E2E environment may bind elsewhere. Give each browser its own device before using this host from a tablet.
if (!builder.Environment.IsEnvironment("E2E")) builder.WebHost.UseUrls("http://localhost:5280");
else builder.WebHost.UseStaticWebAssets(); // the UI tests run it from bin/, where only Development serves _content/ and _framework/ by default
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
// TK-307: the product video's store hour (ProductVideoTests), as BuildingBlocks' ShiftedClock: only in E2E, with Clock:Shift.
if (builder.Environment.IsEnvironment("E2E") && builder.Configuration.GetValue<TimeSpan?>("Clock:Shift") is { } shift)
    builder.Services.AddSingleton<TimeProvider>(new ShiftedClock(shift));
builder.Services.AddAbastaliaClient(new Uri(builder.Configuration["Api:Url"] ?? "http://localhost:5000"));
builder.Services.AddSingleton<ILocalStore>(new FileStore(
    builder.Configuration["Client:File"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Abastalia", "client-web.json")));
// Simulated scale and printer (development): receipts go to a text file; the scale weighs Hardware:Pounds. TK-361: like a store PC, none is
// set until someone types a port in Configuración → Equipos de la caja (any name works), or Hardware:Simulated=true (the UI tests).
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Abastalia");
var scale = new SimulatedScale { Reading = new(builder.Configuration.GetValue("Hardware:Pounds", 2.5m), ScaleStatus.Stable) };
builder.Services.AddAbastaliaHardware(new SimulatedDrivers(scale, builder.Configuration["Hardware:PrinterFile"] ?? Path.Combine(data, "receipts.txt")),
    builder.Configuration.GetValue<bool>("Hardware:Simulated")
        ? new HardwareSettings(new PrinterSettings("Simulada", 80), new ScaleSettings("Simulada", ScaleProtocol.Continuous))
        : null);

// TK-243: the Escuadra gallery (/dev/escuadra), for visual review while developing (and its screenshot test, Dev:Gallery); never in the store.
if (builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Dev:Gallery")) builder.Services.AddSingleton(new ExtraPages(typeof(App).Assembly));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Routes).Assembly);
app.Run();

internal sealed class ShiftedClock(TimeSpan shift) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + shift;
}
