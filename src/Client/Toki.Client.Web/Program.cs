using Toki.Client.UI;
using Toki.Client.UI.State;
using Toki.Client.Web;

// The register screens in a browser, for development and the e2e checks. The store runs Toki.Client.Wpf (ADR-0007).
var builder = WebApplication.CreateBuilder(args);
// shortcut: one paired device token on this server, shared by every tab; so it only listens on this PC (even with --urls 0.0.0.0).
// The E2E environment may bind elsewhere. Give each browser its own device before using this host from a tablet.
if (!builder.Environment.IsEnvironment("E2E")) builder.WebHost.UseUrls("http://localhost:5280");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddTokiClient(new Uri(builder.Configuration["Api:Url"] ?? "http://localhost:5000"));
builder.Services.AddSingleton<ILocalStore>(new FileStore(
    builder.Configuration["Client:File"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki", "client-web.json")));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Routes).Assembly);
app.Run();
