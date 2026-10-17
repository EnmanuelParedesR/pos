using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Quotas;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Platform;

/// <summary>Abastalia staff (TK-189), not store owners. The role is a claim stored on the
/// account by the <see cref="PlatformCli"/> command. Never self-service.</summary>
/// <remarks>R11 B-Med6: the policy reads the role from the database on every call (a revoke counts at once), needs 2FA on the account and a token
/// from a sign-in that passed it (<c>amr=mfa</c>). A refreshed token has no <c>amr</c>, so the console lasts one access token (Program.cs,
/// <c>Auth:AccessTokenMinutes</c>) after the sign-in with the code; the rest of the panel goes on as usual.</remarks>
public static class PlatformAuth
{
    public const string Policy = "PlatformAdmin";
    public const string RoleClaim = "platform_role";
    public const string Admin = "admin";

    public static void Require(AuthorizationPolicyBuilder p) =>
        p.RequireAuthenticatedUser().RequireClaim("amr", "mfa").RequireAssertion(async context =>
            context.Resource is HttpContext http && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId
            && await IsAdminInDbAsync(http.RequestServices.GetRequiredService<CloudDbContext>(), userId, http.RequestAborted, needsTwoFactor: true));

    /// <summary>The account has the role now (and, for the console, 2FA on).</summary>
    public static async Task<bool> IsAdminInDbAsync(CloudDbContext db, string? userId, CancellationToken ct, bool needsTwoFactor = false) =>
        userId is not null
        && await db.UserClaims.AnyAsync(c => c.UserId == userId && c.ClaimType == RoleClaim && c.ClaimValue == Admin, ct)
        && (!needsTwoFactor || await db.Users.AnyAsync(u => u.Id == userId && u.TwoFactorEnabled, ct));
}

/// <summary><c>grant-platform-admin &lt;email&gt;</c> / <c>revoke-platform-admin &lt;email&gt;</c> / <c>reset-2fa &lt;email&gt;</c>: run by Abastalia staff
/// against the cloud database (audited).
/// The account must exist (registered in the panel); the change counts from its next sign-in.</summary>
internal static class PlatformCli
{
    public const string Grant = "grant-platform-admin";
    public const string Revoke = "revoke-platform-admin";

    /// <summary>TK-298: the owner lost the phone and the backup codes. Staff check who asks first (docs/despliegue-azure.md); this turns 2FA off,
    /// throws the old key away and closes every session (new security stamp). The owner signs in with the password and turns it on again.</summary>
    public const string ResetTwoFactor = "reset-2fa";

    /// <param name="reason">R12 #5: required by <see cref="ResetTwoFactor"/> (<c>--reason "…"</c>): who asked and how it was checked; kept in the audit.</param>
    /// <param name="force">R12 #5: <c>--force</c>, required to reset an account of Abastalia staff (platform role).</param>
    public static async Task<int> RunAsync(IServiceProvider services, string command, string email, string? reason = null, bool force = false)
    {
        reason = reason?.Trim();
        if (command == ResetTwoFactor && string.IsNullOrEmpty(reason))
        {
            await Console.Error.WriteLineAsync("Indique por qué y cómo se comprobó quién pide: reset-2fa <correo> --reason \"…\".");
            return 1;
        }
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
        if (command == ResetTwoFactor && has && !force)
        {
            await Console.Error.WriteLineAsync($"{email} es administrador de la plataforma. Para quitarle la verificación en dos pasos agregue --force (otra persona del equipo).");
            return 1;
        }
        var result = command switch
        {
            Grant => has ? IdentityResult.Success : await users.AddClaimAsync(user, role),
            Revoke => has ? await users.RemoveClaimAsync(user, role) : IdentityResult.Success,
            _ => await ResetTwoFactorAsync(users, user),
        };
        if (!result.Succeeded)
        {
            await Console.Error.WriteLineAsync(string.Join(" ", result.Errors.Select(e => e.Description)));
            return 1;
        }
        db.PlatformAudit.Add(new PlatformAuditRecord
        {
            AtUtc = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, UserId = Environment.UserName, Email = "cli",
            Method = "CLI", Path = Fe.FeXml.Cut(command == ResetTwoFactor ? $"{command} {email}{(force ? " --force" : "")} --reason {reason}" : $"{command} {email}", 1000),
            TenantId = user.TenantId,
        });
        await db.SaveChangesAsync();
        // R12 #5: the account's owner hears of it at once, by the address of the account (a reset they did not ask for is a warning).
        if (command == ResetTwoFactor && user.Email is { } to)
            await scope.ServiceProvider.GetRequiredService<Accounts.IEmailSender>().SendAsync(to, "Se quitó la verificación en dos pasos de su cuenta de Abastalia",
                "El equipo de Abastalia quitó la verificación en dos pasos de su cuenta, como se pidió, y cerró todas sus sesiones.\n\n" +
                "Entre al panel con su contraseña y actívela de nuevo en Seguridad.\n\n" +
                "Si usted no pidió esto, cambie su contraseña ahora y escríbanos respondiendo a este correo.", CancellationToken.None);
        Console.WriteLine(command switch
        {
            Grant => $"{email} ya es administrador de la plataforma. Debe volver a entrar al panel.",
            Revoke => $"{email} ya no es administrador de la plataforma.",
            _ => $"{email} ya no tiene la verificación en dos pasos y sus sesiones se cerraron. Puede entrar con su contraseña y activarla de nuevo en Seguridad.",
        });
        return 0;
    }

    /// <summary>Off, no backup codes left (turning it on again makes new ones) and a new key; the key reset also rotates the security stamp.</summary>
    private static async Task<IdentityResult> ResetTwoFactorAsync(UserManager<CloudUser> users, CloudUser user)
    {
        var off = await users.SetTwoFactorEnabledAsync(user, false);
        if (!off.Succeeded) return off;
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
        return await users.ResetAuthenticatorKeyAsync(user);
    }
}

/// <param name="Plans">The plan codes configured (<c>Quotas</c>), Default first; <paramref name="AddOns"/> the add-ons (<c>Quotas:AddOns</c>).</param>
public sealed record PlatformUsageResponse(UsageResponse Usage, IReadOnlyList<string> Plans, IReadOnlyList<string> AddOns);

public sealed record TenantPlanRequest(string? Plan, IReadOnlyList<string>? AddOns);

public sealed record TenantSearchRequest(string? Search);

internal static class PlatformEndpoints
{
    public static void MapPlatform(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/platform").RequireAuthorization(PlatformAuth.Policy).AddEndpointFilter(AuditAsync);

        platform.MapGet("/tenants", async (PlatformQueries q, CancellationToken ct) => Results.Ok(await q.TenantsAsync(null, ct)));
        // R11 B-Low1: the search (often an e-mail) goes in the body, never in a URL that telemetry and the audit keep.
        platform.MapPost("/tenants/search", async (TenantSearchRequest r, PlatformQueries q, CancellationToken ct) => Results.Ok(await q.TenantsAsync(r.Search, ct)));

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

        // TK-311: Salud de la plataforma, the last 24 hours or 7 days.
        platform.MapGet("/watch", async (int? days, PlatformHealth health, CancellationToken ct) => Results.Ok(await health.ReportAsync(days == 7 ? 7 : 1, ct)));

        Certification.AccompanimentEndpoints.MapPlatformAccompaniment(platform); // TK-234
        Billing.BillingEndpoints.MapPlatformBilling(platform); // TK-249
        Releases.MapPlatformReleases(platform); // TK-254
        Fe.FeDelegations.MapPlatformDelegations(platform); // R11 B-High2

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
