using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Identity.Contracts;
using Abastalia.SharedKernel;
using Abastalia.Sync.Contracts;

namespace Abastalia.Host.Lite;

/// <summary>TK-320 (ADR-0013): a new Lite business. In order, each step safe to run again with the same tenant id:
/// 1. the business's own rows (<see cref="Command"/>, one transaction: the tenant and what a store's migrations seed);
/// 2. the owner and the first device (Identity's setup, <see cref="IFirstRun"/>), and the device's entry in <see cref="TenantDevice"/>;
/// 3. with a connection code from the panel, the business's own node in the cloud (<see cref="ICloudConnector"/>: its own key, ADR-0008).</summary>
/// <remarks>TODO TK-325: the cloud calls this when an owner picks the plan Lite (it creates the tenant and the code). Until then only Abastalia's
/// staff call it, with <c>Lite:ProvisioningKey</c>.</remarks>
internal static class ProvisionBusiness
{
    public const string Path = "/internal/tenants";
    public const string KeyHeader = "X-Provisioning-Key";

    /// <param name="TenantId">The cloud tenant's id (never chosen here: it is the business everywhere).</param>
    /// <param name="ConnectionCode">Optional: a code from the panel's Tiendas, to register the business as a node.</param>
    public sealed record Request(Guid TenantId, string OwnerName, string OwnerCode, string Pin, string DeviceName, string? ConnectionCode = null);

    /// <param name="DeviceSecret">Proves the first device on every request; shown once.</param>
    /// <param name="CloudError">Why the node could not be registered (the business works anyway; retry with a new code).</param>
    public sealed record Response(Guid TenantId, Guid OwnerId, Guid DeviceId, string DeviceSecret, string? CloudError);

    internal sealed record Command(Guid TenantId) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.TenantId).NotEmpty();
    }

    internal sealed class Handler(LiteDbContext lite, IEnumerable<ModuleDbContext> contexts, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            if (await lite.Tenants.AnyAsync(t => t.Id == c.TenantId, ct)) return Ok.Value; // provisioned before: its rows are there
            lite.Tenants.Add(new Tenant { Id = c.TenantId, CreatedUtc = clock.GetUtcNow().UtcDateTime });
            TenantSeed.Add(contexts);
            // Saved before the audit record: the record moves the business's own audit head, one of the seeded rows.
            foreach (var db in contexts.Where(d => d.ChangeTracker.HasChanges())) await db.SaveChangesAsync(ct);
            await audit.RecordAsync("lite.provision", "Tenant", c.TenantId, new { c.TenantId }, ct);
            return Ok.Value;
        }
    }

    public static async Task<Result<Response>> RunAsync(Request r, IServiceScopeFactory scopes, CancellationToken ct)
    {
        await using var scope = scopes.CreateTenantScope(r.TenantId);
        var sp = scope.ServiceProvider;
        var rows = await sp.GetRequiredService<ICommandHandler<Command, Ok>>().Handle(new Command(r.TenantId), ct);
        if (rows.IsFailure) return rows.Error!;

        var first = await sp.GetRequiredService<IFirstRun>().SetUpAsync(r.OwnerName, r.OwnerCode, r.Pin, r.DeviceName, ct);
        if (first.IsFailure) return first.Error!;
        var device = first.Value;
        var lite = sp.GetRequiredService<LiteDbContext>();
        lite.Devices.Add(new TenantDevice
        {
            SecretHash = TenantDevice.Hash(device.DeviceSecret), BusinessId = r.TenantId, DeviceId = device.DeviceId,
            CreatedUtc = sp.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
        });
        await lite.SaveChangesAsync(ct); // one row, on its own: the device exists in Identity already

        var cloud = string.IsNullOrWhiteSpace(r.ConnectionCode) ? null : await sp.GetRequiredService<ICloudConnector>().ConnectAsync(r.ConnectionCode, ct);
        return new Response(r.TenantId, device.OwnerId, device.DeviceId, device.DeviceSecret, cloud?.Code);
    }

    /// <summary>The key never compared as text (timing); no key configured = the route does not exist.</summary>
    private static bool Allowed(IConfiguration config, HttpContext http) =>
        config["Lite:ProvisioningKey"] is { Length: >= 32 } key
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)),
            SHA256.HashData(Encoding.UTF8.GetBytes(http.Request.Headers[KeyHeader].ToString())));

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost(Path, async (Request r, HttpContext http, IConfiguration config, IServiceScopeFactory scopes, CancellationToken ct) =>
                    Allowed(config, http) ? (await RunAsync(r, scopes, ct)).ToHttp(v => Results.Created($"{Path}/{v.TenantId}", v)) : Results.NotFound())
                .AllowAnonymous().ExcludeFromDescription();
    }
}
