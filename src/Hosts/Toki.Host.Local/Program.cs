using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using Serilog;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Endpoints;
using Toki.Caja;
using Toki.Clientes;
using Toki.Compras;
using Toki.Configuracion;
using Toki.Fiscal;
using Toki.Ventas;
using Toki.Identidad;
using Toki.Inventario;
using Toki.Reportes;
using Toki.Host.Local;

// Restoring on any PC: Toki.Host.Local descifrar-respaldo <archivo.toki-respaldo> <salida>, with the phrase in TOKI_FRASE_RESPALDO or typed.
if (args is ["descifrar-respaldo", var archivo, var salida])
{
    var frase = Environment.GetEnvironmentVariable("TOKI_FRASE_RESPALDO");
    if (frase is null)
    {
        Console.Write("Frase del respaldo: ");
        frase = Console.ReadLine() ?? "";
    }
    await using (var entrada = File.OpenRead(archivo))
    await using (var destino = File.Create(salida))
        await CifradoRespaldo.DescifrarAsync(entrada, destino, frase, CancellationToken.None);
    Console.WriteLine($"Listo: {salida}");
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
builder.Services.AddConfiguracionModule();
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

if (app.Configuration.GetValue<bool>("Database:CrearAlIniciar")) await TokiServices.CrearBaseDeDatosAsync(app.Services);

await app.RunAsync();

public partial class Program;
