using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Assistant;
using Abastalia.Host.Cloud.Backups;
using Abastalia.Host.Cloud.Billing;
using Abastalia.Host.Cloud.Catalog;
using Abastalia.Host.Cloud.Fe;
using Abastalia.Host.Cloud.Ingest;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Panel;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Platform;
using Abastalia.Host.Cloud.Quotas;
using Abastalia.Host.Cloud.Relay;

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
builder.Services.AddSingleton(Abastalia.BuildingBlocks.ShiftedClock.From(config, builder.Environment)); // TK-307: E2E may move it

// Panel users (bearer tokens) and store nodes (signed with their own key, TK-241).
builder.Services.AddSingleton<NodeNonces>();
builder.Services.AddSingleton<FailedCodes>();
builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NodeAuthenticationHandler>(CloudAuth.NodeScheme, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(CloudAuth.NodePolicy, p => p.AddAuthenticationSchemes(CloudAuth.NodeScheme).RequireAuthenticatedUser())
    .AddPolicy(CloudAuth.PanelPolicy, p => p.RequireAuthenticatedUser().RequireClaim(TenantClaims.TenantId).RequireAssertion(PanelAccess.IsMemberAsync))
    .AddPolicy(CloudAuth.OwnerPolicy, p => p.RequireAuthenticatedUser().RequireClaim(TenantClaims.TenantId).RequireAssertion(PanelAccess.IsOwnerAsync))
    .AddPolicy(PlatformAuth.Policy, PlatformAuth.Require);
// R11 B-High1: only a confirmed e-mail signs in (an invite link that arrived by e-mail confirms it). Development may turn it off (no e-mail there).
var requireConfirmedEmail = builder.Environment.IsProduction() || config.GetValue("Auth:RequireConfirmedEmail", true);
builder.Services.AddIdentityApiEndpoints<CloudUser>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.SignIn.RequireConfirmedEmail = requireConfirmedEmail;
        // R11 B-Low6: wrong passwords count per IP and account (LoginThrottle); the per-account lockout is only a backstop against many IPs.
        o.Lockout.MaxFailedAccessAttempts = config.GetValue("Auth:AccountLockoutFailures", 50);
    })
    .AddEntityFrameworkStores<CloudDbContext>()
    .AddClaimsPrincipalFactory<CloudClaimsFactory>()
    .AddUserManager<CloudUserManager>()
    .AddSignInManager<CloudSignInManager>()
    .AddTokenProvider<OneTimeAuthenticatorTokenProvider>(TokenOptions.DefaultAuthenticatorProvider); // R12 #4: a code works once
// R11 B-Med5: short-lived tokens. The panel keeps the refresh token in localStorage (the API and the panel are on different sites, so an HttpOnly
// cookie would be a third-party cookie); a strict CSP on the panel (swa-config.mjs) is what keeps scripts from reading it.
builder.Services.Configure<Microsoft.AspNetCore.Authentication.BearerToken.BearerTokenOptions>(IdentityConstants.BearerScheme, o =>
{
    o.BearerTokenExpiration = TimeSpan.FromMinutes(config.GetValue("Auth:AccessTokenMinutes", 30));
    o.RefreshTokenExpiration = TimeSpan.FromDays(config.GetValue("Auth:RefreshTokenDays", 7));
});
builder.Services.AddScoped<IUserStore<CloudUser>, CloudUserStore>();
builder.Services.AddTransient<IEmailSender<CloudUser>, IdentityEmails>(); // TK-230: confirm and reset e-mails, in Spanish
builder.Services.AddSingleton<IdentityEmailCap>();
builder.Services.AddSingleton<LoginThrottle>();

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
// R11 B-Med2/B-Med3: account management per user; uploads per store PC (a compromised one cannot flood the database).
var managePerUserMinute = config.GetValue("Auth:ManagePerMinutePerUser", 10);
var ingestPerNodeMinute = config.GetValue("Ingest:BatchesPerMinutePerNode", 60);
var archivePerNodeMinute = config.GetValue("Archive:UploadsPerMinutePerNode", 120);
// TK-238: a backup is hundreds of MB; a store makes one a night (and the odd manual one).
var backupsPerNodeHour = config.GetValue("CloudBackup:UploadsPerHourPerNode", 4);
var backupDownloadsPerUserHour = config.GetValue("CloudBackup:DownloadsPerHourPerUser", 10); // R12 #6
// The e-CF receiver's limits (R9 #2) are the global limiter, set by AddFe.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // TK-248: say when the window reopens, so the store's uploads wait that long instead of retrying blind.
    o.OnRejected = (rejected, _) =>
    {
        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after))
            rejected.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(after.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // TK-311: which limit refused, for Salud de la plataforma ("global" is the e-CF receiver's).
        rejected.HttpContext.RequestServices.GetRequiredService<LimitWatch>().Record(rejected.HttpContext, LimitReasons.RateLimit,
            rejected.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "global",
            rejected.HttpContext.Response.Headers.RetryAfter.Count > 0);
        return ValueTask.CompletedTask;
    };
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
    o.AddPolicy(AccountSecurityEndpoints.ManageRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(QuotaRateLimits.UserOrIp(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = managePerUserMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(IngestEndpoint.NodeRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirst(TenantClaims.NodeId)?.Value ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = ingestPerNodeMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(EcfArchiveEndpoint.NodeRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirst(TenantClaims.NodeId)?.Value ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = archivePerNodeMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(StoreBackupEndpoints.NodeRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirst(TenantClaims.NodeId)?.Value ?? "",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = backupsPerNodeHour, Window = TimeSpan.FromHours(1) }));
    o.AddPolicy(StoreBackupEndpoints.DownloadRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(QuotaRateLimits.UserOrIp(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = backupDownloadsPerUserHour, Window = TimeSpan.FromHours(1) }));
});
builder.Services.AddScoped<IQuota, Quota>();
builder.Services.AddSingleton<LicenseCheck>();

builder.Services.AddSingleton<InboxSignal>();
builder.Services.AddSingleton<InboxApplier>();
builder.Services.AddHostedService<InboxApplierWorker>();
builder.Services.AddScoped<PanelQueries>();
builder.Services.AddScoped<PlatformQueries>();
builder.Services.AddSingleton<LimitWatch>(); // TK-311
builder.Services.AddHostedService<LimitWatchWorker>();
builder.Services.AddScoped<PlatformHealth>();
AcsEmailSender.AddEmail(builder.Services, config);
builder.Services.AddScoped<Abastalia.Host.Cloud.Alerts.AlertNotifier>();
builder.Services.AddColdArchive(config);
builder.Services.AddStoreBackups(config);
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

// Abastalia staff only (TK-189), never self-service: dotnet run --project src/Hosts/Abastalia.Host.Cloud -- grant-platform-admin <email>
// R12 #5: reset-2fa <email> --reason "<who asked, how it was checked>" [--force for a staff account]
if (args is [PlatformCli.Grant or PlatformCli.Revoke or PlatformCli.ResetTwoFactor, var email, .. var options])
{
    var reasonAt = Array.IndexOf(options, "--reason");
    Environment.ExitCode = await PlatformCli.RunAsync(app.Services, args[0], email,
        reasonAt >= 0 && reasonAt + 1 < options.Length ? options[reasonAt + 1] : null, options.Contains("--force"));
    return;
}

// TK-307: a moved clock (E2E) moves the Date header too: the store sets its cloud skew by it (SyncModule) and would undo the move.
if (app.Services.GetRequiredService<TimeProvider>() is Abastalia.BuildingBlocks.ShiftedClock shifted)
    app.Use((context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Date = shifted.GetUtcNow().ToString("R");
            return Task.CompletedTask;
        });
        return next(context);
    });
app.UseExceptionHandler();
app.UseStatusCodePages();
LimitWatch.UseServerErrorWatch(app); // TK-311: 5xx by route
app.UseSerilogRequestLogging();
app.UseCors();
app.UseNodeGate(); // R12 #1: per IP and the body's length, before decompression and the node check read the body
app.UseRequestDecompression();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // after authorization: the per-PC limit reads the node identity, which the node policy authenticates (NodeGate limits per IP before)

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
// R11 B-Low4: it wakes the serverless database, so only Abastalia staff ask it (docs/despliegue-azure.md).
app.MapHealthChecks("/health/ready").RequireAuthorization(PlatformAuth.Policy);

var auth = app.MapGroup("/api/auth");
auth.MapIdentityApi<CloudUser>().Add(e =>
{
    if (e is not RouteEndpointBuilder { RoutePattern.RawText: { } route } endpoint) return;
    if (route.EndsWith("/resendConfirmationEmail", StringComparison.Ordinal)) e.Metadata.Add(new EnableRateLimitingAttribute(IdentityEmails.ResendRateLimit));
    else if (route.Contains("/manage/", StringComparison.Ordinal)) e.Metadata.Add(new EnableRateLimitingAttribute(AccountSecurityEndpoints.ManageRateLimit));
    else if (QuotaRateLimits.AuthRoutes.Any(r => route.EndsWith(r, StringComparison.Ordinal))) e.Metadata.Add(new EnableRateLimitingAttribute(QuotaRateLimits.Auth));
    if (route.EndsWith("/register", StringComparison.Ordinal)) endpoint.FilterFactories.Add((_, next) => c => AccountSecurityEndpoints.SameAnswerForTakenEmail(c, next));
    if (route.EndsWith("/manage/2fa", StringComparison.Ordinal)) endpoint.FilterFactories.Add((_, next) => c => AccountSecurityEndpoints.CodeToWeakenTwoFactor(c, next));
});
app.MapAccountSecurity();
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
app.MapStoreBackups();
app.MapPlatform();
app.MapPublicCatalog();
app.MapFe();
Abastalia.Host.Cloud.Certification.AccompanimentEndpoints.MapAccompaniment(app);
Abastalia.Host.Cloud.Alerts.AlertEndpoints.MapAlerts(app);

await app.RunAsync();

public partial class Program;
