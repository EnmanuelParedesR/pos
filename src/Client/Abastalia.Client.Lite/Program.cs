using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.JSInterop;
using Abastalia.Client.Lite;
using Abastalia.Client.UI;
using Abastalia.Client.UI.State;
using Abastalia.Hardware;

// TK-324 (ADR-0013 decision 3): Client.UI's register in the browser, for the Lite plan. Two roots: the register itself and the bar with the
// e-CF this device still has to sign or send.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<LiteBar>("#lite-bar");

var config = builder.Configuration;
var options = new LiteOptions(new Uri(config["Api:Url"] is { Length: > 0 } url ? url : builder.HostEnvironment.BaseAddress),
    config.GetValue("Fiscal:SendToDgii", false), TimeSpan.FromSeconds(config.GetValue("Fiscal:IntervalSeconds", 60)));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<LiteDevice>();
builder.Services.AddSingleton(new BrowserHost(LiteRoutes.FirstRun));
LiteDevice? device = null; // the handler is made before the container; it finds the device once the app runs
builder.Services.AddAbastaliaClient(options.Api, new DeviceFetchHandler(() => device!));
builder.Services.AddSingleton<ILocalStore, LiteStore>();
builder.Services.AddAbastaliaHardware(new NoHardware());
builder.Services.AddSingleton(new ExtraPages(typeof(LiteBar).Assembly));
builder.Services.TryAddSingleton(TimeProvider.System);

var host = builder.Build();
device = host.Services.GetRequiredService<LiteDevice>();
await host.RunAsync();

internal static class LiteRoutes
{
    public const string FirstRun = "lite/start";
}

/// <summary>ADR-0013 decision 5: no scale, drawer or ESC/POS printer on the Lite plan; the receipt is printed or shared by the browser.</summary>
internal sealed class NoHardware : IHardwareDrivers
{
    private static NotSupportedException None() => new("La versión en línea no usa balanza ni impresora de recibos: imprima la factura desde el navegador.");
    public IScale CreateScale(ScaleSettings settings) => throw None();
    public IReceiptPrinter CreatePrinter(PrinterSettings settings) => throw None();
}
