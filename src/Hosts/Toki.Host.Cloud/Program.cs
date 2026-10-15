using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Assistant;
using Toki.Host.Cloud.Ingest;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Panel;
using Toki.Host.Cloud.Persistence;
using Toki.Host.Cloud.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());
var config = builder.Configuration;

// Azure SQL in production; Sqlite allowed for tests and development (ADR-0002).
var connectionString = config["Database:ConnectionString"] ?? throw new InvalidOperationException("Missing Database:ConnectionString.");
var sqlite = string.Equals(config["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase);
builder.Services.AddDbContext<CloudDbContext>(o =>
{
    if (sqlite) o.UseSqlite(connectionString);
    else o.UseSqlServer(connectionString, s => s.EnableRetryOnFailure());
});
builder.Services.AddHostedService<CreateCloudDatabaseOnStartup>();
builder.Services.AddSingleton(TimeProvider.System);

// Panel users (bearer tokens) and store nodes (shared secret).
builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NodeAuthenticationHandler>(CloudAuth.NodeScheme, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(CloudAuth.NodePolicy, p => p.AddAuthenticationSchemes(CloudAuth.NodeScheme).RequireAuthenticatedUser())
    .AddPolicy(CloudAuth.PanelPolicy, p => p.RequireAuthenticatedUser().RequireClaim(TenantClaims.TenantId));
builder.Services.AddIdentityApiEndpoints<CloudUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<CloudDbContext>()
    .AddClaimsPrincipalFactory<CloudClaimsFactory>();
builder.Services.AddScoped<IUserStore<CloudUser>, CloudUserStore>();

// Guessing an 8-digit code is the only way in without an account.
var registerPerMinute = config.GetValue("Nodes:RegisterPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(CloudAuth.RegisterNodeRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = registerPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddSingleton<InboxSignal>();
builder.Services.AddSingleton<InboxApplier>();
builder.Services.AddHostedService<InboxApplierWorker>();
builder.Services.AddScoped<PanelQueries>();
builder.Services.AddSingleton<ICloudEcfProvider, SimulatedCloudEcfProvider>();

var openAi = config.GetSection(AzureOpenAIOptions.Section).Get<AzureOpenAIOptions>() ?? new AzureOpenAIOptions();
if (openAi.IsConfigured)
{
    builder.Services.AddSingleton(openAi);
    builder.Services.AddHttpClient<IChatModel, AzureOpenAIChatModel>(c => c.Timeout = TimeSpan.FromSeconds(60));
}

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(config.GetSection("Panel:Origins").Get<string[]>() ?? []).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddRequestDecompression(); // nodes upload batches gzip-compressed
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseCors();
app.UseRequestDecompression();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapGroup("/api/auth").MapIdentityApi<CloudUser>();
app.MapStores();
app.MapNodes();
app.MapIngest();
app.MapPanel();
app.MapAssistant();
app.MapEcfRelay();

await app.RunAsync();

public partial class Program;
