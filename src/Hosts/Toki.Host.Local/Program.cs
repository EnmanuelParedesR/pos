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

// Restoring on any PC: Toki.Host.Local decrypt-backup <file.toki-backup> <output>, with the passphrase in TOKI_BACKUP_PASSPHRASE or typed.
if (args is ["decrypt-backup", var file, var output])
{
    var passphrase = Environment.GetEnvironmentVariable("TOKI_BACKUP_PASSPHRASE");
    if (passphrase is null)
    {
        // Typed without echo: the passphrase opens every backup of the store.
        Console.Write("Frase del respaldo: ");
        var typed = new System.Text.StringBuilder();
        for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
            if (key.Key == ConsoleKey.Backspace) typed.Length = Math.Max(0, typed.Length - 1);
            else typed.Append(key.KeyChar);
        Console.WriteLine();
        passphrase = typed.ToString();
    }
    try
    {
        await using (var input = File.OpenRead(file))
        await using (var destination = File.Create(output))
            await BackupEncryption.DecryptAsync(input, destination, passphrase, CancellationToken.None);
        Console.WriteLine($"Listo: {output}");
    }
    catch (System.Security.Cryptography.CryptographicException e)
    {
        File.Delete(output); // a half-written file must never look like a usable backup
        Console.Error.WriteLine($"No se pudo abrir el respaldo: {e.Message} (¿frase equivocada?)");
        Environment.ExitCode = 1;
    }
    return;
}

// Installer step (administrator, Windows): Toki.Host.Local prepare-service "NT SERVICE\Toki"
if (args is ["prepare-service", var account] && OperatingSystem.IsWindows())
{
    var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json").AddJsonFile("appsettings.Production.json", optional: true).Build();
    await PrepareService.RunAsync(account, config["Database:ConnectionString"]!);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "Toki");
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());
// The store server listens on the LAN over HTTPS only (appsettings.Production.json); never plain HTTP between registers and server.
if (builder.Configuration.GetValue<bool>("Toki:HttpsWithNodeCertificate"))
{
    var certificate = NodeCertificate.GetOrCreate(StoreLocation.LocalMachine, TimeProvider.System);
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = certificate));
}

Assembly[] modules =
[
    typeof(IdentityModule).Assembly,
    typeof(SettingsModule).Assembly,
    typeof(InventoryModule).Assembly,
    typeof(CashRegisterModule).Assembly,
    typeof(FiscalModule).Assembly,
    typeof(SalesModule).Assembly,
    typeof(PurchasingModule).Assembly,
    typeof(CustomersModule).Assembly,
    typeof(ReportsModule).Assembly,
];

builder.Services.AddToki(builder.Configuration, modules);
builder.Services.AddIdentityModule();
builder.Services.AddSettingsModule(builder.Configuration);
builder.Services.AddInventoryModule();
builder.Services.AddCashRegisterModule();
builder.Services.AddFiscalModule();
builder.Services.AddSalesModule();
builder.Services.AddPurchasingModule();
builder.Services.AddCustomersModule();

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
app.MapEndpoints(modules);


await app.RunAsync();

public partial class Program;
