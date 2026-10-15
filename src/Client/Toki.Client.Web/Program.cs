using Toki.Client.UI;
using Toki.Client.UI.State;
using Toki.Client.Web;

// The register screens in a browser, for development and the e2e checks. The store runs Toki.Client.Wpf (ADR-0007).
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddTokiClient(new Uri(builder.Configuration["Api:Url"] ?? "http://localhost:5000"));
builder.Services.AddSingleton<ILocalStore>(new FileStore(
    builder.Configuration["Client:File"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki", "client-web.json")));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Routes).Assembly);
app.Run();
