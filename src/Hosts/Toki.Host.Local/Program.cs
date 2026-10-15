using System.Reflection;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using Serilog;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Endpoints;
using Toki.Configuracion;
using Toki.Identidad;
using Toki.Inventario;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Toki");
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

Assembly[] modulos =
[
    typeof(IdentidadModule).Assembly,
    typeof(ConfiguracionModule).Assembly,
    typeof(InventarioModule).Assembly,
];

builder.Services.AddToki(builder.Configuration, modulos);
builder.Services.AddIdentidadModule();
builder.Services.AddConfiguracionModule();
builder.Services.AddInventarioModule();

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

app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health");
app.MapEndpoints(modulos);

if (app.Configuration.GetValue<bool>("Database:CrearAlIniciar")) await TokiServices.CrearBaseDeDatosAsync(app.Services);

await app.RunAsync();

public partial class Program;
