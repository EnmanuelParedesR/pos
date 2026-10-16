using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Https;
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
using Toki.Sync;
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
// TK-091 (ADR-0009): its certificate comes from the store CA, and registers prove who they are with a client certificate from the same CA.
var https = builder.Configuration.GetValue<bool>("Toki:HttpsWithNodeCertificate");
var legacySecrets = https && builder.Configuration.GetValue<bool>("Toki:LegacyDeviceSecrets");
if (https)
{
    // Registers paired before TK-091 pinned the old self-signed certificate: while they are kept (dual mode), the server keeps showing it.
    var legacy = legacySecrets ? NodeCertificate.GetOrCreate(StoreLocation.LocalMachine, TimeProvider.System) : null;
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
    {
        StoreCertificateAuthority Ca() => k.ApplicationServices.GetRequiredService<StoreCertificateAuthority>();
        if (legacy is not null) h.ServerCertificate = legacy;
        else h.ServerCertificateSelector = (_, _) => Ca().ServerCertificate;
        // Allow, not Require: pairing and /health come before the register has a certificate. A certificate that is sent must be one of
        // ours (else the handshake fails); the requirement for everything else is the middleware below.
        h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        h.ClientCertificateValidation = (certificate, _, _) => Ca().IsDeviceCertificate(certificate);
    }));
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
    typeof(SyncModule).Assembly,
];

builder.Services.AddToki(builder.Configuration, modules);
builder.Services.AddIdentityModule();
builder.Services.AddSettingsModule(builder.Configuration);
builder.Services.AddInventoryModule();
builder.Services.AddCashRegisterModule();
builder.Services.AddFiscalModule(builder.Configuration);
builder.Services.AddSalesModule();
builder.Services.AddPurchasingModule();
builder.Services.AddCustomersModule();
builder.Services.AddSyncModule();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
// TK-242: only stores whose appsettings name a release feed look for updates.
builder.Services.Configure<UpdateSettings>(builder.Configuration.GetSection("Updates"));
if (!string.IsNullOrWhiteSpace(builder.Configuration["Updates:FeedUrl"]))
{
    builder.Services.AddHttpClient();
    builder.Services.AddHostedService<ServerUpdateCheck>();
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
if (https && !legacySecrets)
{
    // Over the LAN every API call comes from a paired register's certificate; only what comes before pairing goes without one.
    app.Use((context, next) => context.Connection.ClientCertificate is not null || DeviceCertificates.IsOptional(context.Request.Path)
        ? next(context)
        : EndpointExtensions.Problem(DeviceCertificates.Missing).ExecuteAsync(context));
    var ca = app.Services.GetRequiredService<StoreCertificateAuthority>();
    app.Logger.LogInformation("Huella del servidor para unir cajas: {Fingerprint}", DeviceCertificates.Fingerprint(ca.ServerCertificate));
}
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
