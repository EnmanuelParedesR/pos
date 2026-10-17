using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Identity.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Lite;

/// <summary>TK-321 (ADR-0013): the selling device of a Lite business. The owner asks for a code in the panel (the cloud calls
/// <see cref="CreateCode"/> with the provisioning key); the browser makes its own non-extractable ECDSA P-256 key and sends the code with the
/// public key to <see cref="PairPath"/>; from then on it signs every request (<see cref="DeviceTenant"/>). As many selling devices at a time as
/// the plan allows (<see cref="PlanLimits"/>, 1 on Lite): another one waits until the owner revokes one in the panel (<see cref="Revoke"/>).</summary>
/// <remarks>Why the cloud calls the host and not the other way: the devices live only here, so pairing and revoking cannot drift apart, revoking
/// works at once (no polling) and no new secret is added: the cloud already holds <c>Lite:ProvisioningKey</c> to provision (TK-325).
/// The tenant always comes from the panel's token, never from the browser.</remarks>
internal static class SellingDevices
{
    public const string PairPath = "/lite/devices/pair", PairRateLimit = "lite-pair";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    public static readonly Error InvalidCode = Error.Validation("Lite.InvalidCode", "El código no es válido o ya venció. Pida uno nuevo en el panel, en Tiendas.");
    public static readonly Error InvalidKey = Error.Validation("Lite.InvalidKey", "Este navegador no pudo crear su llave. Use Chrome o Edge actualizados.");
    public static Error DeviceLimit(int allowed) => Error.Conflict("Lite.DeviceLimit",
        $"Su plan permite {(allowed == 1 ? "1 dispositivo de venta" : $"{allowed} dispositivos de venta")} a la vez y ya los tiene. Revoque uno en el panel, " +
        "en Tiendas, o pase a un plan mayor (en Plan y facturación) o a la instalación en una PC.");
    public static readonly Error NotFound = Error.NotFound("Lite.NotFound", "No encontramos ese negocio o ese dispositivo.");

    public sealed record CodeResponse(string Code, DateTime ExpiresUtc);
    /// <param name="PublicKey">SubjectPublicKeyInfo of the browser's P-256 key, Base64.</param>
    /// <param name="Name">What the owner sees in the panel; "Dispositivo de venta" when empty.</param>
    public sealed record PairRequest(string? Code, string? PublicKey, string? Name);
    public sealed record PairResponse(Guid DeviceId);
    public sealed record Item(Guid Id, string Name, DateTime CreatedUtc);
    /// <param name="Allowed">The plan's selling devices at a time: the panel hides "Unir" once they are all in use.</param>
    public sealed record Listing(int Allowed, IReadOnlyList<Item> Devices);

    private static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    /// <summary>A new code ends the business's other one.</summary>
    public static async Task<Result<CodeResponse>> CreateCode(Guid businessId, LiteDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == businessId, ct)) return NotFound;
        var now = clock.GetUtcNow().UtcDateTime;
        await db.DeviceCodes.Where(c => c.BusinessId == businessId || c.ExpiresUtc < now).ExecuteDeleteAsync(ct);
        string code;
        byte[] hash;
        // Two live codes are never equal: the code alone picks the business.
        do hash = Hash(code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("00000000", CultureInfo.InvariantCulture));
        while (await db.DeviceCodes.AnyAsync(c => c.CodeHash == hash, ct));
        db.DeviceCodes.Add(new DeviceCode { CodeHash = hash, BusinessId = businessId, ExpiresUtc = now + CodeLifetime });
        await db.SaveChangesAsync(ct);
        return new CodeResponse(code, now + CodeLifetime);
    }

    public static async Task<Result<PairResponse>> Pair(PairRequest r, IServiceScopeFactory scopes, LiteDbContext db, IConfiguration config, TimeProvider clock,
        CancellationToken ct)
    {
        if (NodeSignature.PublicKey(r.PublicKey) is not { } publicKey) return InvalidKey;
        var code = r.Code?.Replace(" ", "", StringComparison.Ordinal) ?? "";
        if (code.Length != 8) return InvalidCode;
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(code);
        var valid = db.DeviceCodes.Where(c => c.CodeHash == hash && c.ExpiresUtc > now);
        if (await valid.Select(c => (Guid?)c.BusinessId).FirstOrDefaultAsync(ct) is not { } businessId) return InvalidCode;
        // Before the code is spent: the owner revokes the old device and uses the same code.
        var allowed = PlanLimits.SellingDevices(config);
        if (await db.Devices.CountAsync(d => d.BusinessId == businessId, ct) >= allowed) return DeviceLimit(allowed);
        // Spending it is one conditional delete: two browsers racing with the same code cannot both pair.
        if (await valid.ExecuteDeleteAsync(ct) != 1) return InvalidCode;

        await using var scope = scopes.CreateTenantScope(businessId);
        var sp = scope.ServiceProvider;
        var id = Guid.CreateVersion7();
        var name = string.IsNullOrWhiteSpace(r.Name) ? "Dispositivo de venta" : r.Name.Trim();
        if (name.Length > 100) name = name[..100];
        // Tracked before the command: Identity's transaction saves every module context's changes, this row with its device or neither.
        sp.GetRequiredService<LiteDbContext>().Devices.Add(new TenantDevice { DeviceId = id, BusinessId = businessId, Name = name, PublicKey = publicKey, CreatedUtc = now });
        var added = await sp.GetRequiredService<ISellingDevices>().AddAsync(id, name, ct);
        return added.IsFailure ? added.Error! : new PairResponse(id);
    }

    public static async Task<Result> Revoke(Guid businessId, Guid deviceId, IServiceScopeFactory scopes, CancellationToken ct)
    {
        await using var scope = scopes.CreateTenantScope(businessId);
        var sp = scope.ServiceProvider;
        var lite = sp.GetRequiredService<LiteDbContext>();
        if (await lite.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId && d.BusinessId == businessId, ct) is not { } device) return NotFound;
        lite.Devices.Remove(device); // saved with Identity's revocation, in its transaction
        return await sp.GetRequiredService<ISellingDevices>().RevokeAsync(deviceId, ct);
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            // The browser, before it has a key the host knows: the code proves the business.
            app.MapPost(PairPath, async (PairRequest r, IServiceScopeFactory scopes, LiteDbContext db, IConfiguration config, TimeProvider clock, CancellationToken ct) =>
                    (await Pair(r, scopes, db, config, clock, ct)).ToHttp())
                .AllowAnonymous().RequireRateLimiting(PairRateLimit).ExcludeFromDescription();

            // The cloud's panel, for the Owner of that business (Host.Cloud LiteDevices).
            var cloud = app.MapGroup(ProvisionBusiness.Path + "/{businessId:guid}").AllowAnonymous().ExcludeFromDescription()
                .AddEndpointFilter(async (context, next) =>
                    ProvisionBusiness.Allowed(context.HttpContext.RequestServices.GetRequiredService<IConfiguration>(), context.HttpContext) ? await next(context) : Results.NotFound());
            cloud.MapPost("/device-codes", async (Guid businessId, LiteDbContext db, TimeProvider clock, CancellationToken ct) =>
                (await CreateCode(businessId, db, clock, ct)).ToHttp());
            // 404 for a business this host does not serve: the panel then shows no selling device at all.
            cloud.MapGet("/devices", async (Guid businessId, LiteDbContext db, IConfiguration config, CancellationToken ct) =>
                !await db.Tenants.AnyAsync(t => t.Id == businessId, ct) ? EndpointExtensions.Problem(NotFound)
                : Results.Ok(new Listing(PlanLimits.SellingDevices(config), await db.Devices.AsNoTracking().Where(d => d.BusinessId == businessId)
                    .OrderBy(d => d.CreatedUtc).Select(d => new Item(d.DeviceId, d.Name, d.CreatedUtc)).ToListAsync(ct))));
            cloud.MapPost("/devices/{deviceId:guid}/revoke", async (Guid businessId, Guid deviceId, IServiceScopeFactory scopes, CancellationToken ct) =>
                await Revoke(businessId, deviceId, scopes, ct) is { IsFailure: true } failed ? EndpointExtensions.Problem(failed.Error!) : Results.NoContent());
        }
    }
}

/// <summary>TK-321, TK-347: this host serves every plan, each with its own caps: <c>Lite:Plans:&lt;Plan&gt;:SellingDevices</c> and <c>:Cashiers</c>
/// (besides the owner); the Lite plan's 1 and 1 when not set (ADR-0013 decision 5).</summary>
internal static class PlanLimits
{
    /// <summary>shortcut: one plan for every business (<c>Lite:DefaultPlan</c>, "Lite"); TK-325 reads each business's from its license.</summary>
    public static string PlanOf(IConfiguration config) => config["Lite:DefaultPlan"] is { Length: > 0 } plan ? plan : "Lite";
    public static int SellingDevices(IConfiguration config) => config.GetValue($"Lite:Plans:{PlanOf(config)}:SellingDevices", 1);
    public static int Cashiers(IConfiguration config) => config.GetValue($"Lite:Plans:{PlanOf(config)}:Cashiers", 1);
}

/// <summary>TK-321: the plan's caps (<see cref="PlanLimits"/>) on top of the business's license: the selling devices sign in and pair, as a store's
/// main PC plus <c>Registers</c>; the cashiers besides the owner. Identity counts both from its own rows.</summary>
internal sealed class LitePlan(IFeatureService license, IConfiguration config) : IFeatureService
{
    public LicenseStatus Status => license.Status;
    public DateTime? EndsUtc => license.EndsUtc;
    public string? LicenseJws => license.LicenseJws;
    public Error BlockedError => license.BlockedError;
    public bool IsEnabled(Feature feature) => license.IsEnabled(feature);

    public int LimitOf(Limit limit) => Math.Min(license.LimitOf(limit), limit switch
    {
        Limit.Registers => PlanLimits.SellingDevices(config) - 1,
        _ => PlanLimits.Cashiers(config),
    });
}
