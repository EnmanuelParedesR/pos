using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using Serilog;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Endpoints;
using Toki.CashRegister;
using Toki.Customers;
using Toki.Purchasing;
using Toki.Settings;
using Toki.Fiscal;
using Toki.Sales;
using Toki.Identity;
using Toki.Inventory;
using Toki.Reports;
using Toki.Host.Local;

// Restoring on any PC: Toki.Host.Local descifrar-respaldo <archivo.toki-respaldo> <salida>, with the phrase in TOKI_FRASE_RESPALDO or typed.
if (args is ["descifrar-respaldo", var archivo, var salida])
{
    var frase = Environment.GetEnvironmentVariable("TOKI_FRASE_RESPALDO");
    if (frase is null)
    {
        // Typed without echo: the phrase opens every backup of the store.
        Console.Write("Frase del respaldo: ");
        var tecleada = new System.Text.StringBuilder();
        for (var tecla = Console.ReadKey(intercept: true); tecla.Key != ConsoleKey.Enter; tecla = Console.ReadKey(intercept: true))
            if (tecla.Key == ConsoleKey.Backspace) tecleada.Length = Math.Max(0, tecleada.Length - 1);
            else tecleada.Append(tecla.KeyChar);
        Console.WriteLine();
        frase = tecleada.ToString();
    }
    try
    {
        await using (var entrada = File.OpenRead(archivo))
        await using (var destino = File.Create(salida))
            await CifradoRespaldo.DescifrarAsync(entrada, destino, frase, CancellationToken.None);
        Console.WriteLine($"Listo: {salida}");
    }
    catch (System.Security.Cryptography.CryptographicException e)
    {
        File.Delete(salida); // a half-written file must never look like a usable backup
        Console.Error.WriteLine($"No se pudo abrir el respaldo: {e.Message} (¿frase equivocada?)");
        Environment.ExitCode = 1;
    }
    return;
}

// Installer step (administrator, Windows): Toki.Host.Local preparar-servicio "NT SERVICE\Toki"
if (args is ["preparar-servicio", var cuenta] && OperatingSystem.IsWindows())
{
    var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json").AddJsonFile("appsettings.Production.json", optional: true).Build();
    await PrepararServicio.EjecutarAsync(cuenta, config["Database:ConnectionString"]!);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Toki");
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());
// The store server listens on the LAN over HTTPS only (appsettings.Production.json); never plain HTTP between registers and server.
if (builder.Configuration.GetValue<bool>("Toki:HttpsConCertificadoNodo"))
{
    var certificado = CertificadoNodo.ObtenerOCrear(StoreLocation.LocalMachine, TimeProvider.System);
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = certificado));
}

Assembly[] modulos =
[
    typeof(IdentidadModule).Assembly,
    typeof(ConfiguracionModule).Assembly,
    typeof(InventarioModule).Assembly,
    typeof(CajaModule).Assembly,
    typeof(FiscalModule).Assembly,
    typeof(VentasModule).Assembly,
    typeof(ComprasModule).Assembly,
    typeof(ClientesModule).Assembly,
    typeof(ReportesModule).Assembly,
];

builder.Services.AddToki(builder.Configuration, modulos);
builder.Services.AddIdentidadModule();
builder.Services.AddConfiguracionModule(builder.Configuration);
builder.Services.AddInventarioModule();
builder.Services.AddCajaModule();
builder.Services.AddFiscalModule();
builder.Services.AddVentasModule();
builder.Services.AddComprasModule();
builder.Services.AddClientesModule();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}
app.MapHealthChecks("/health").AllowAnonymous();
app.MapEndpoints(modulos);


await app.RunAsync();

public partial class Program;
