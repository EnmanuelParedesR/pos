using Toki.Client.UI;
using Toki.Client.UI.Estado;
using Toki.Client.Web;

// The register screens in a browser, for development and the e2e checks. The store runs Toki.Client.Wpf (ADR-0007).
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddTokiCliente(new Uri(builder.Configuration["Api:Url"] ?? "http://localhost:5000"));
builder.Services.AddSingleton<IAlmacenLocal>(new AlmacenEnArchivo(
    builder.Configuration["Cliente:Archivo"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toki", "cliente-web.json")));

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(typeof(Routes).Assembly);
app.Run();
