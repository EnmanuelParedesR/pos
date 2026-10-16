using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Assistant;
using Toki.Host.Cloud.Billing;
using Toki.Host.Cloud.Catalog;
using Toki.Host.Cloud.Fe;
using Toki.Host.Cloud.Ingest;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Panel;
using Toki.Host.Cloud.Persistence;
using Toki.Host.Cloud.Platform;
using Toki.Host.Cloud.Quotas;
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

// Panel users (bearer tokens) and store nodes (signed with their own key, TK-241).
builder.Services.AddSingleton<NodeNonces>();
builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NodeAuthenticationHandler>(CloudAuth.NodeScheme, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(CloudAuth.NodePolicy, p => p.AddAuthenticationSchemes(CloudAuth.NodeScheme).RequireAuthenticatedUser())
    .AddPolicy(CloudAuth.PanelPolicy, p => p.RequireAuthenticatedUser().RequireClaim(TenantClaims.TenantId).RequireAssertion(PanelAccess.IsMemberAsync))
    .AddPolicy(CloudAuth.OwnerPolicy, p => p.RequireAuthenticatedUser().RequireClaim(TenantClaims.TenantId).RequireAssertion(PanelAccess.IsOwnerAsync))
    .AddPolicy(PlatformAuth.Policy, p => p.RequireAuthenticatedUser().RequireClaim(PlatformAuth.RoleClaim, PlatformAuth.Admin));
builder.Services.AddIdentityApiEndpoints<CloudUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<CloudDbContext>()
    .AddClaimsPrincipalFactory<CloudClaimsFactory>()
    .AddSignInManager<CloudSignInManager>();
builder.Services.AddScoped<IUserStore<CloudUser>, CloudUserStore>();
builder.Services.AddTransient<IEmailSender<CloudUser>, IdentityEmails>(); // TK-230: confirm and reset e-mails, in Spanish

// Guessing an 8-digit code is the only way in without an account.
var registerPerMinute = config.GetValue("Nodes:RegisterPerMinute", 10);
// R8 #17: the public catalog needs no account either; a customer opens it a few times, a scraper many.
var catalogPerMinute = config.GetValue("Catalog:RequestsPerMinute", 60);
// TK-231: each resend is an e-mail to any address; a person needs one or two.
var resendPerMinute = config.GetValue("Auth:ResendConfirmationPerMinute", 3);
var invoicesPerNodeMinute = config.GetValue("Ai:SupplierInvoicesPerMinutePerNode", 10);
// TK-248: sign-in, registration and password reset per IP (Identity's lockout also stops 5 wrong passwords per account); the panel per user.
var authPerMinute = config.GetValue("Auth:RequestsPerMinute", 10);
var panelPerUserMinute = config.GetValue("Panel:RequestsPerMinutePerUser", 120);
var assistantPerUserMinute = config.GetValue("Ai:AssistantQuestionsPerMinutePerUser", 10);
// The e-CF receiver's limits (R9 #2) are the global limiter, set by AddFe.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // TK-249: the node's, the panel's and the assistant's per-minute limits follow the business's plan (Paddle custom_data > Quotas:<Plan>:RateLimits > the setting).
    o.AddPolicy(SupplierInvoiceEndpoint.NodeRateLimit, http =>
        QuotaRateLimits.PerPlan(http, http.User.FindFirst(TenantClaims.NodeId)?.Value ?? "", PlanLimits.SupplierInvoicesPerMinute, invoicesPerNodeMinute));
    o.AddPolicy(CloudAuth.RegisterNodeRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = registerPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(PublicCatalogEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = catalogPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(IdentityEmails.ResendRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = resendPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(QuotaRateLimits.Auth, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(QuotaRateLimits.PanelUser, http => QuotaRateLimits.PerPlan(http, QuotaRateLimits.UserOrIp(http), PlanLimits.PanelPerMinute, panelPerUserMinute));
    o.AddPolicy(QuotaRateLimits.AssistantUser, http => QuotaRateLimits.PerPlan(http, QuotaRateLimits.UserOrIp(http), PlanLimits.AssistantPerMinute, assistantPerUserMinute));
});
builder.Services.AddScoped<IQuota, Quota>();

builder.Services.AddSingleton<InboxSignal>();
builder.Services.AddSingleton<InboxApplier>();
builder.Services.AddHostedService<InboxApplierWorker>();
builder.Services.AddScoped<PanelQueries>();
builder.Services.AddScoped<PlatformQueries>();
AcsEmailSender.AddEmail(builder.Services, config);
builder.Services.AddScoped<Toki.Host.Cloud.Alerts.AlertNotifier>();
builder.Services.AddColdArchive(config);
builder.Services.AddFe(config, builder.Environment);
builder.Services.AddBilling(config, builder.Environment); // TK-249: off until Billing:Paddle is set

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

// Toki staff only (TK-189), never self-service: dotnet run --project src/Hosts/Toki.Host.Cloud -- grant-platform-admin <email>
if (args is [PlatformCli.Grant or PlatformCli.Revoke, var email])
{
    Environment.ExitCode = await PlatformCli.RunAsync(app.Services, args[0], email);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseCors();
app.UseRequestDecompression();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // after authorization: the per-PC limit reads the node identity, which the node policy authenticates

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.MapGroup("/api/auth").MapIdentityApi<CloudUser>().Add(e =>
{
    if (e is not RouteEndpointBuilder { RoutePattern.RawText: { } route }) return;
    if (route.EndsWith("/resendConfirmationEmail", StringComparison.Ordinal)) e.Metadata.Add(new EnableRateLimitingAttribute(IdentityEmails.ResendRateLimit));
    else if (QuotaRateLimits.AuthRoutes.Any(r => route.EndsWith(r, StringComparison.Ordinal))) e.Metadata.Add(new EnableRateLimitingAttribute(QuotaRateLimits.Auth));
});
app.MapQuotas();
app.MapBilling();
app.MapStores();
app.MapMembers();
app.MapNodes();
app.MapNodeUpdates();
app.MapIngest();
app.MapPanel();
app.MapAssistant();
app.MapSupplierInvoiceReader();
app.MapEcfArchive();
app.MapColdArchive();
app.MapPlatform();
app.MapPublicCatalog();
app.MapFe();
Toki.Host.Cloud.Certification.AccompanimentEndpoints.MapAccompaniment(app);
Toki.Host.Cloud.Alerts.AlertEndpoints.MapAlerts(app);

await app.RunAsync();

public partial class Program;
