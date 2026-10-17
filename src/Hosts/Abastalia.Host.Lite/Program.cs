using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using Abastalia.BuildingBlocks;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.CashRegister;
using Abastalia.Customers;
using Abastalia.Purchasing;
using Abastalia.Settings;
using Abastalia.Fiscal;
using Abastalia.Sales;
using Abastalia.Identity;
using Abastalia.Inventory;
using Abastalia.Reports;
using Abastalia.Sync;
using Abastalia.Host.Lite;

// TK-320 (ADR-0013): the Lite plan's server. The same store modules, many businesses in one database (sqldb-abastalia-lite): each request runs as
// the business of its device (DeviceTenant), each worker pass once per business, and each business is its own node of Abastalia.Host.Cloud.
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());
// Without it there is no Row-Level Security and every connection would be the store's: never run Lite like that.
if (!builder.Configuration.GetValue<bool>("Database:MultiTenant")) throw new InvalidOperationException("Abastalia.Host.Lite needs Database:MultiTenant=true.");
// TK-323: a certificate on this server would sign for every business (ADR-0011: the cloud never holds one); each business's device signs.
if (builder.Configuration["Fiscal:Signer"] != "Device") throw new InvalidOperationException("Abastalia.Host.Lite needs Fiscal:Signer=Device.");

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
    typeof(LiteDbContext).Assembly, // provisioning
];

// Before AddAbastalia, which registers the store's fixed tenant only when none is.
builder.Services.AddScoped<ScopedTenant>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ScopedTenant>());
builder.Services.AddScoped<ITenantDirectory, LiteTenants>();
builder.Services.AddAbastalia(builder.Configuration, modules);
builder.Services.AddModuleDbContext<LiteDbContext>();
builder.Services.AddIdentityModule();
builder.Services.AddSettingsModule(builder.Configuration);
builder.Services.AddInventoryModule();
builder.Services.AddCashRegisterModule();
builder.Services.AddFiscalModule(builder.Configuration);
builder.Services.AddSalesModule();
builder.Services.AddPurchasingModule();
builder.Services.AddCustomersModule();
builder.Services.AddSyncModule();

// TK-321: the plan Lite's limits over each business's license (Settings registers it as a factory).
var license = builder.Services.Last(d => d.ServiceType == typeof(IFeatureService));
builder.Services.Remove(license);
builder.Services.AddScoped<IFeatureService>(sp => new LitePlan((IFeatureService)license.ImplementationFactory!(sp), sp.GetRequiredService<IConfiguration>()));
// shortcut: per IP only; a global cap on wrong codes (as the cloud's FailedCodes) comes with TK-328.
var pairPerMinute = builder.Configuration.GetValue("Lite:PairPerMinutePerIp", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(SellingDevices.PairRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = pairPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// Only the workers that go over every business. The store's others would run as no business (RLS shows them nothing) or do what Lite does
// elsewhere: backups to a folder and their cloud copy (Azure SQL keeps point-in-time backups), the nightly audit check, the server-signed e-CF
// queue and the receiver (the browser signs and sends: TK-323, TK-326) and the fiscal mode loaded once per process (each business reads its own).
// The device-signed e-CF still go to the cloud archive (DeviceSignedArchiveWorker, every business).
string[] liteWorkers = ["CreateDatabaseOnStartup", "OutboxProcessorWorker", "CloudUploaderWorker", "HeartbeatWorker", "LicenseClockKeeper", "DeviceSignedArchiveWorker"];
foreach (var worker in builder.Services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Namespace?.StartsWith("Abastalia.", StringComparison.Ordinal) == true
             && !liteWorkers.Contains(d.ImplementationType.Name)).ToList())
    builder.Services.Remove(worker);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.Use(DeviceTenant.ResolveAsync); // before authentication: Identity reads the devices and sessions of that business only
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapEndpoints(modules);

await app.RunAsync();

public partial class Program;
