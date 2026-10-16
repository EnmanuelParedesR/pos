using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Persistence;
using Toki.Host.Cloud.Quotas;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Platform;

/// <summary>Toki staff (TK-189), not store owners. The role is a claim stored on the
/// account by the <see cref="PlatformCli"/> command. Never self-service.</summary>
public static class PlatformAuth
{
    public const string Policy = "PlatformAdmin";
    public const string RoleClaim = "platform_role";
    public const string Admin = "admin";

    public static bool IsPlatformAdmin(this ClaimsPrincipal user) => user.HasClaim(RoleClaim, Admin);
}

/// <summary><c>grant-platform-admin &lt;email&gt;</c> / <c>revoke-platform-admin &lt;email&gt;</c>: run by Toki staff against the cloud database.
/// The account must exist (registered in the panel); the change counts from its next sign-in.</summary>
internal static class PlatformCli
{
    public const string Grant = "grant-platform-admin";
    public const string Revoke = "revoke-platform-admin";

    public static async Task<int> RunAsync(IServiceProvider services, string command, string email)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        await CreateCloudDatabaseOnStartup.PrepareAsync(db, CancellationToken.None);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<CloudUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            await Console.Error.WriteLineAsync($"No hay una cuenta con el correo {email}. Regístrela primero en el panel.");
            return 1;
        }

        var role = new Claim(PlatformAuth.RoleClaim, PlatformAuth.Admin);
        var has = (await users.GetClaimsAsync(user)).Any(c => c.Type == role.Type && c.Value == role.Value);
        var result = command == Grant
            ? has ? IdentityResult.Success : await users.AddClaimAsync(user, role)
            : has ? await users.RemoveClaimAsync(user, role) : IdentityResult.Success;
        if (!result.Succeeded)
        {
            await Console.Error.WriteLineAsync(string.Join(" ", result.Errors.Select(e => e.Description)));
            return 1;
        }
        db.PlatformAudit.Add(new PlatformAuditRecord
        {
            AtUtc = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, UserId = Environment.UserName, Email = "cli",
            Method = "CLI", Path = $"{command} {email}", TenantId = user.TenantId,
        });
        await db.SaveChangesAsync();
        Console.WriteLine(command == Grant
            ? $"{email} ya es administrador de la plataforma. Debe volver a entrar al panel."
            : $"{email} ya no es administrador de la plataforma.");
        return 0;
    }
}

/// <param name="Plans">The plan codes configured (<c>Quotas</c>), Default first; <paramref name="AddOns"/> the add-ons (<c>Quotas:AddOns</c>).</param>
public sealed record PlatformUsageResponse(UsageResponse Usage, IReadOnlyList<string> Plans, IReadOnlyList<string> AddOns);

public sealed record TenantPlanRequest(string? Plan, IReadOnlyList<string>? AddOns);

internal static class PlatformEndpoints
{
    public static void MapPlatform(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/platform").RequireAuthorization(PlatformAuth.Policy).AddEndpointFilter(AuditAsync);

        platform.MapGet("/tenants", async (string? search, PlatformQueries q, CancellationToken ct) => Results.Ok(await q.TenantsAsync(search, ct)));

        platform.MapGet("/tenants/{tenantId:guid}", async (Guid tenantId, PlatformQueries q, CancellationToken ct) =>
            await q.TenantAsync(tenantId, ct) is { } tenant ? Results.Ok(tenant) : NotFound("Platform.TenantNotFound", "No encontramos ese cliente."));

        platform.MapPost("/tenants/{tenantId:guid}/disable", (Guid tenantId, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
            SetDisabledAsync(db, tenantId, clock.GetUtcNow().UtcDateTime, ct));

        platform.MapPost("/tenants/{tenantId:guid}/enable", (Guid tenantId, CloudDbContext db, CancellationToken ct) => SetDisabledAsync(db, tenantId, null, ct));

        // TK-248: this month's usage against the plan, and the plan and add-ons the business paid for (the quotas follow them at once).
        platform.MapGet("/tenants/{tenantId:guid}/usage", async (Guid tenantId, CloudDbContext db, IQuota quota, PlanLimits limits, CancellationToken ct) =>
        {
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) return NotFound("Platform.TenantNotFound", "No encontramos ese cliente.");
            var (plans, addOns) = limits.Catalog();
            return Results.Ok(new PlatformUsageResponse(await quota.UsageAsync(tenantId, ct), plans, addOns));
        });

        platform.MapPut("/tenants/{tenantId:guid}/plan", async (Guid tenantId, TenantPlanRequest r, CloudDbContext db, PlanLimits limits, TenantPlans cache, CancellationToken ct) =>
        {
            var (plans, addOns) = limits.Catalog();
            var chosen = (r.AddOns ?? []).ToList(); // a code n times = n units (TK-258)
            if (r.Plan is not { } plan || !plans.Contains(plan) || chosen.Any(a => !addOns.Contains(a)) || chosen.CountBy(a => a).Any(c => c.Value > Quota.MaxAddOnQuantity))
                return EndpointExtensions.Problem(Error.Validation("Platform.InvalidPlan", "Elija un plan y servicios adicionales de la lista."));
            var updated = await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Plan, plan == Quota.DefaultPlan ? null : plan).SetProperty(t => t.AddOns, Quota.JoinAddOns(chosen)), ct);
            cache.Forget(tenantId); // the per-minute limits follow at once
            return updated == 0 ? NotFound("Platform.TenantNotFound", "No encontramos ese cliente.") : Results.NoContent();
        });

        platform.MapGet("/stores/{storeId:guid}", async (Guid storeId, PlatformQueries q, CancellationToken ct) =>
            await q.StoreAsync(storeId, ct) is { } store ? Results.Ok(store) : NotFound("Platform.StoreNotFound", "No encontramos esa tienda."));

        platform.MapPost("/nodes/{nodeId:guid}/revoke", async (Guid nodeId, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (!await db.Nodes.AnyAsync(n => n.Id == nodeId, ct)) return NotFound("Platform.NodeNotFound", "No encontramos esa caja.");
            await db.Nodes.Where(n => n.Id == nodeId && n.RevokedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.RevokedUtc, now), ct);
            return Results.NoContent();
        });

        Certification.AccompanimentEndpoints.MapPlatformAccompaniment(platform); // TK-234
        Billing.BillingEndpoints.MapPlatformBilling(platform); // TK-249

        var metrics = platform.MapGroup("/metrics");
        metrics.MapGet("/overview", async (PlatformQueries q, CancellationToken ct) => Results.Ok((await q.MetricsAsync(ct)).Overview()));
        metrics.MapGet("/daily", async (int? days, PlatformQueries q, CancellationToken ct) => Results.Ok((await q.MetricsAsync(ct)).Daily(Math.Clamp(days ?? 30, 1, 120))));
        metrics.MapGet("/retention", async (int? weeks, PlatformQueries q, CancellationToken ct) => Results.Ok((await q.MetricsAsync(ct)).Retention(Math.Clamp(weeks ?? 8, 1, 26))));
        metrics.MapGet("/tenants/{tenantId:guid}", async (Guid tenantId, PlatformQueries q, CancellationToken ct) =>
            (await q.MetricsAsync(ct)).ForTenant(tenantId) is { } m ? Results.Ok(m) : NotFound("Platform.TenantNotFound", "No encontramos ese cliente."));
    }

    private static async Task<IResult> SetDisabledAsync(CloudDbContext db, Guid tenantId, DateTime? disabledUtc, CancellationToken ct) =>
        await db.Tenants.Where(t => t.Id == tenantId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DisabledUtc, disabledUtc), ct) == 0
            ? NotFound("Platform.TenantNotFound", "No encontramos ese cliente.")
            : Results.NoContent();

    private static IResult NotFound(string code, string message) => EndpointExtensions.Problem(Error.NotFound(code, message));

    /// <summary>Every platform call leaves a row (who, what, when) before it runs, also the ones that fail.</summary>
    private static async ValueTask<object?> AuditAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var db = http.RequestServices.GetRequiredService<CloudDbContext>();
        var path = http.Request.Path + http.Request.QueryString;
        db.PlatformAudit.Add(new PlatformAuditRecord
        {
            AtUtc = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
            UserId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
            Email = http.User.FindFirstValue(ClaimTypes.Email) ?? http.User.Identity?.Name ?? "",
            Method = http.Request.Method,
            Path = path.Length > 1000 ? path[..1000] : path,
            TenantId = http.Request.RouteValues["tenantId"] is string t && Guid.TryParse(t, out var id) ? id : null,
        });
        await db.SaveChangesAsync(http.RequestAborted);
        return await next(context);
    }
}
