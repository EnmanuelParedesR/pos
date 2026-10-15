using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;
using Toki.Sync.Contracts;

namespace Toki.Sync;

/// <summary>Connects the store to the cloud and uploads the outbox (ADR-0008, arquitectura-v1-vsa §5).</summary>
public static class SyncModule
{
    public static IServiceCollection AddSyncModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<SyncDbContext>();
        services.AddHttpClient(CloudClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<CloudClient>();
        services.AddScoped<ICloudClient>(sp => sp.GetRequiredService<CloudClient>());
        services.AddScoped<CloudUploader>();
        services.AddHostedService<CloudUploaderWorker>();
        services.AddHealthChecks().AddCheck<SyncHealthCheck>("sync");
        return services;
    }
}

/// <summary>Single row (Id 1) while the store is connected; disconnecting deletes it.</summary>
internal sealed class CloudConnection
{
    public int Id { get; set; } = 1;
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid NodeId { get; set; }
    /// <summary>Protected for this machine with ASP.NET Core Data Protection (the key ring Settings sets up). The cloud keeps only its hash.</summary>
    public string NodeSecret { get; set; } = "";
    public string CloudUrl { get; set; } = "";
    public DateTime ConnectedUtc { get; set; }
    public DateTime? LastUploadUtc { get; set; }
    public string? LastError { get; set; }
}

internal sealed class SyncDbContext(DbContextOptions<SyncDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "sync";
    public DbSet<CloudConnection> Connections => Set<CloudConnection>();
}

internal sealed class CloudConnectionConfiguration : IEntityTypeConfiguration<CloudConnection>
{
    public void Configure(EntityTypeBuilder<CloudConnection> b)
    {
        b.ToTable("CloudConnection");
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.NodeSecret).HasMaxLength(1000);
        b.Property(c => c.CloudUrl).HasMaxLength(500);
        b.Property(c => c.LastError).HasMaxLength(2000);
    }
}

internal static class SyncErrors
{
    public static readonly Error InvalidCode = Error.Validation("Sync.InvalidCode", "El código no es válido o ya venció. Pida uno nuevo en el panel de Toki.");
    public static readonly Error CloudUnreachable = Error.Conflict("Sync.CloudUnreachable", "No se pudo conectar con la nube. Revise el internet e intente de nuevo.");
    public static readonly Error AlreadyConnected = Error.Conflict("Sync.AlreadyConnected", "Esta tienda ya está conectada a la nube. Desconéctela antes de usar otro código.");
    public static readonly Error NotConfigured = Error.Conflict("Sync.NotConfigured", "Esta PC no tiene configurada la dirección de la nube.");
}

/// <summary>Calls the cloud as this node. Implements <see cref="ICloudClient"/> for other modules (the e-CF relay).</summary>
internal sealed class CloudClient(SyncDbContext db, IHttpClientFactory http, IDataProtectionProvider protection) : ICloudClient
{
    public const string HttpClientName = "Toki.Cloud";

    private IDataProtector Protector => protection.CreateProtector("Toki.Sync.NodeSecret");

    public string Protect(string secret) => Protector.Protect(secret);

    public Task<CloudConnection?> ConnectionAsync(CancellationToken ct) => db.Connections.AsNoTracking().SingleOrDefaultAsync(ct);

    public async Task<HttpResponseMessage> PostAsJsonAsync<T>(string path, T body, CancellationToken ct) =>
        await SendAsync(await ConnectionAsync(ct) ?? throw new InvalidOperationException("La tienda no está conectada a la nube."), path, JsonContent.Create(body), ct);

    public async Task<HttpResponseMessage> SendAsync(CloudConnection connection, string path, HttpContent content, CancellationToken ct)
    {
        string secret;
        try { secret = Protector.Unprotect(connection.NodeSecret); }
        catch (CryptographicException e)
        {
            throw new InvalidOperationException("La clave de la nube no se puede leer en esta PC: desconecte la tienda y conéctela con un código nuevo.", e);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.CloudUrl + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Node", $"{connection.NodeId}.{secret}");
        return await http.CreateClient(HttpClientName).SendAsync(request, ct);
    }
}

public sealed record SyncStatus(bool Connected, Guid? TenantId, Guid? StoreId, string? CloudUrl, DateTime? LastUploadUtc, int PendingEvents, string? LastError);

internal static class GetSyncStatus
{
    public static async Task<SyncStatus> RunAsync(SyncDbContext db, InfraDbContext infra, CancellationToken ct)
    {
        var c = await db.Connections.AsNoTracking().SingleOrDefaultAsync(ct);
        var pending = await infra.Outbox.CountAsync(m => m.UploadedUtc == null, ct);
        return new SyncStatus(c is not null, c?.TenantId, c?.StoreId, c?.CloudUrl, c?.LastUploadUtc, pending, c?.LastError);
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/sync/status", async (SyncDbContext db, InfraDbContext infra, CancellationToken ct) => Results.Ok(await RunAsync(db, infra, ct)))
                .RequirePermission(Permissions.SettingsManage).WithTags("Sync");
    }
}

/// <summary>The owner types the connection code from the panel; the cloud registers this PC as a node and answers its identity (ADR-0008).
/// The cloud call runs outside any transaction; only saving the answer is a command.</summary>
internal static class ConnectToCloud
{
    public sealed record Request(string? Code);

    internal sealed record Registration(Guid TenantId, Guid StoreId, Guid NodeId, string NodeSecret);

    internal sealed record Command(string CloudUrl, Registration Registration) : ICommand<Ok>;

    public static async Task<Result<SyncStatus>> RunAsync(string? code, string? cloudUrl, SyncDbContext db, InfraDbContext infra, IHttpClientFactory http,
        ICommandHandler<Command, Ok> save, CancellationToken ct)
    {
        code = code?.Trim();
        if (code is not { Length: 8 } || !code.All(char.IsAsciiDigit)) return SyncErrors.InvalidCode;
        if (string.IsNullOrWhiteSpace(cloudUrl)) return SyncErrors.NotConfigured;
        if (await db.Connections.AnyAsync(ct)) return SyncErrors.AlreadyConnected;

        cloudUrl = cloudUrl.Trim().TrimEnd('/');
        Registration? registration;
        try
        {
            using var response = await http.CreateClient(CloudClient.HttpClientName)
                .PostAsJsonAsync($"{cloudUrl}/api/nodes/register", new { code, nodeName = Environment.MachineName }, ct);
            // 400/404 per ADR-0008; 409/410 too, in case the cloud tells a used or expired code apart.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.Gone) return SyncErrors.InvalidCode;
            if (!response.IsSuccessStatusCode) return SyncErrors.CloudUnreachable;
            registration = await response.Content.ReadFromJsonAsync<Registration>(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException && !ct.IsCancellationRequested)
        {
            return SyncErrors.CloudUnreachable;
        }
        if (registration is null || registration.NodeId == Guid.Empty || string.IsNullOrEmpty(registration.NodeSecret)) return SyncErrors.CloudUnreachable;

        var saved = await save.Handle(new Command(cloudUrl, registration), ct);
        if (saved.IsFailure) return saved.Error!;
        return await GetSyncStatus.RunAsync(db, infra, ct);
    }

    internal sealed class Handler(SyncDbContext db, CloudClient cloud, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            if (await db.Connections.AnyAsync(ct)) return SyncErrors.AlreadyConnected;
            var r = c.Registration;
            db.Connections.Add(new CloudConnection
            {
                TenantId = r.TenantId, StoreId = r.StoreId, NodeId = r.NodeId, NodeSecret = cloud.Protect(r.NodeSecret), CloudUrl = c.CloudUrl,
                ConnectedUtc = clock.GetUtcNow().UtcDateTime,
            });
            await audit.RecordAsync("sync.connect", "CloudConnection", r.NodeId, new { r.TenantId, r.StoreId, r.NodeId, c.CloudUrl }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/sync/connect", async (Request r, IConfiguration config, SyncDbContext db, InfraDbContext infra, IHttpClientFactory http,
                    ICommandHandler<Command, Ok> save, CancellationToken ct) => (await RunAsync(r.Code, config["Cloud:Url"], db, infra, http, save, ct)).ToHttp())
                .RequirePermission(Permissions.SettingsManage).WithTags("Sync");
    }
}

/// <summary>Stops uploading. Pending events stay in the outbox and go up when the store connects again.</summary>
/// <remarks>shortcut: the cloud is not told; the node stays registered there until the panel removes it. Add a call when the panel can list nodes.</remarks>
internal static class DisconnectFromCloud
{
    internal sealed record Command : ICommand<Ok>;

    internal sealed class Handler(SyncDbContext db, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var connection = await db.Connections.SingleOrDefaultAsync(ct);
            if (connection is null) return Ok.Value;
            db.Connections.Remove(connection);
            await audit.RecordAsync("sync.disconnect", "CloudConnection", connection.NodeId, new { connection.TenantId, connection.StoreId, connection.NodeId }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/sync/disconnect", async (ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Sync");
    }
}

/// <summary>Degraded when the store is connected and an event has waited more than an hour to go up.</summary>
internal sealed class SyncHealthCheck(SyncDbContext db, InfraDbContext infra, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (connection is null) return HealthCheckResult.Healthy("La tienda no está conectada a la nube.");
        var limit = clock.GetUtcNow().UtcDateTime.AddHours(-1);
        if (!await infra.Outbox.AnyAsync(m => m.UploadedUtc == null && m.OccurredUtc < limit, cancellationToken)) return HealthCheckResult.Healthy();
        return HealthCheckResult.Degraded("Hay datos de la tienda sin subir a la nube desde hace más de una hora. Revise el internet."
            + (connection.LastError is { } error ? $" Último error: {error}" : ""));
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class SyncDesignTimeFactory : DesignTimeFactory<SyncDbContext>;
