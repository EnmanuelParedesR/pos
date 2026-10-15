using System.Reflection;
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

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Toki");
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

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
