using System.Globalization;
using System.Threading.RateLimiting;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Quotas;

/// <summary>TK-248: what a business consumes of Abastalia's services that cost money or invite abuse, counted per store month (docs/cuotas.md).</summary>
public enum Meter { AssistantQuestions, AssistantTokens, SupplierInvoices, Emails, ArchiveRetrievals, AccompanimentRequests, Invites }

/// <summary>One counter per business, store month (UTC−4) and meter. A month without a row is a month with nothing used.</summary>
public sealed class TenantUsage
{
    public Guid TenantId { get; set; }
    /// <summary>The first day of the store month.</summary>
    public DateOnly Month { get; set; }
    public Meter Meter { get; set; }
    public int Count { get; set; }
}

/// <param name="Used">After this call (it includes what was just taken).</param>
/// <param name="ResetsOn">The first day of the next store month.</param>
public sealed record QuotaResult(bool Allowed, Meter Meter, int Used, int Limit, DateOnly ResetsOn)
{
    public int Remaining => Math.Max(Limit - Used, 0);

    /// <summary>429 <c>Quota.Exceeded</c> with the meter, the numbers and the reset date, in Spanish.</summary>
    public IResult Problem() =>
        Results.Problem(detail: Quota.Message(Meter, Limit, ResetsOn), statusCode: StatusCodes.Status429TooManyRequests, title: Quota.ExceededCode,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = Quota.ExceededCode, ["meter"] = Meter.ToString(), ["used"] = Used, ["limit"] = Limit, ["resetsOn"] = ResetsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
}

public sealed record MeterUsage(Meter Meter, int Used, int Limit);

/// <param name="Plan">The business's plan code (<c>Quotas:&lt;Plan&gt;</c>); <c>Default</c> when Abastalia staff did not set one.</param>
public sealed record UsageResponse(string Plan, IReadOnlyList<string> AddOns, DateOnly ResetsOn, IReadOnlyList<MeterUsage> Meters);

public interface IQuota
{
    /// <summary>Takes <paramref name="n"/> from this month's allowance of the business, atomically, unless that would pass its limit (then nothing is taken).</summary>
    /// <remarks>Call it before adding the caller's own rows to the context: the first use of a month inserts the counter with SaveChanges.</remarks>
    Task<QuotaResult> TryConsumeAsync(Guid tenantId, Meter meter, int n = 1, CancellationToken ct = default);

    /// <summary>Adds <paramref name="n"/> whatever the limit: what is only known afterwards (tokens), what must never be refused (a password
    /// reset e-mail), or a negative <paramref name="n"/> to give back what a refused call took.</summary>
    Task AddAsync(Guid tenantId, Meter meter, int n, CancellationToken ct = default);

    Task<UsageResponse> UsageAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>Limits come from <see cref="PlanLimits"/> (TK-249): the plan's Paddle product (<c>custom_data.abastalia.quotas</c>), else
/// <c>Quotas:&lt;Plan&gt;:&lt;Meter&gt;</c>, else Default's, else 0 (not offered). Each add-on unit the business bought adds its own (TK-258). The plan and add-ons come from its Paddle subscription or from Abastalia staff in the console (<see cref="Tenant.Plan"/>, <see cref="Tenant.AddOns"/>).</summary>
/// <remarks>shortcut: the plan is not read from the store's signed license: the heartbeat only says it, the store could send anything. Staff set it in
/// the console when they issue the license; send the license (JWS) in the heartbeat and verify it here if keeping both in step becomes a chore.</remarks>
internal sealed class Quota(CloudDbContext db, PlanLimits limits, TimeProvider clock) : IQuota
{
    public const string ExceededCode = "Quota.Exceeded";
    public const string DefaultPlan = "Default";
    public const string AddOnsSection = "AddOns";
    public const string SupplierInvoiceReading = "SupplierInvoiceReading";

    public async Task<QuotaResult> TryConsumeAsync(Guid tenantId, Meter meter, int n = 1, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        var month = Month();
        var limit = await LimitAsync(tenantId, meter, ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // One statement: two calls at once cannot both take the last unit (SQL Server and Sqlite lock the row for the update).
            if (await Row(tenantId, month, meter).Where(u => u.Count + n <= limit).ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count + n), ct) == 1)
                return await ResultAsync(true);
            if (n > limit || await Row(tenantId, month, meter).AnyAsync(ct)) return await ResultAsync(false);
            if (await TryInsertAsync(tenantId, month, meter, n, ct)) return await ResultAsync(true);
            // another call created this month's row first: count on it
        }
        return await ResultAsync(false);

        async Task<QuotaResult> ResultAsync(bool allowed) =>
            new(allowed, meter, await Row(tenantId, month, meter).Select(u => u.Count).FirstOrDefaultAsync(ct), limit, month.AddMonths(1));
    }

    public async Task AddAsync(Guid tenantId, Meter meter, int n, CancellationToken ct = default)
    {
        if (n == 0) return;
        var month = Month();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await Row(tenantId, month, meter).ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count + n), ct) == 1) return;
            if (n < 0 || await TryInsertAsync(tenantId, month, meter, n, ct)) return;
        }
    }

    public async Task<UsageResponse> UsageAsync(Guid tenantId, CancellationToken ct = default)
    {
        var month = Month();
        var (plan, addOns) = await PlanAsync(tenantId, ct);
        var used = await db.TenantUsage.AsNoTracking().Where(u => u.TenantId == tenantId && u.Month == month).ToDictionaryAsync(u => u.Meter, u => u.Count, ct);
        return new UsageResponse(plan, addOns, month.AddMonths(1),
            [.. Enum.GetValues<Meter>().Select(m => new MeterUsage(m, used.GetValueOrDefault(m), limits.MonthlyLimit(plan, addOns, m)))]);
    }

    /// <summary>Most of one add-on a business can have (Paddle's prices allow up to 50).</summary>
    public const int MaxAddOnQuantity = 50;

    /// <summary>The add-ons as stored ("ExtraRegister:2,SupplierInvoiceReading"), one entry per unit bought: ["ExtraRegister", "ExtraRegister",
    /// "SupplierInvoiceReading"]. TK-258: quantity add-ons (extra registers, stores, and later extra cashiers) count their entries.</summary>
    public static string[] SplitAddOns(string addOns) =>
        [.. addOns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SelectMany(a =>
            a.Split(':') is [var code, var n] && int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity)
                ? Enumerable.Repeat(code, Math.Clamp(quantity, 0, MaxAddOnQuantity)) : [a])];

    /// <summary>The inverse of <see cref="SplitAddOns"/>: each code once, with ":n" when bought more than once, in code order.</summary>
    public static string JoinAddOns(IEnumerable<string> addOns) =>
        string.Join(',', addOns.GroupBy(a => a).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key}:{Math.Min(g.Count(), MaxAddOnQuantity).ToString(CultureInfo.InvariantCulture)}"));

    public static string Message(Meter meter, int limit, DateOnly resetsOn)
    {
        var text = $"Este mes ya se usó todo lo incluido en su plan de {Names[meter]} ({limit.ToString("N0", CultureInfo.InvariantCulture)}). " +
                   $"Se renueva el {resetsOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}.";
        return meter switch
        {
            Meter.SupplierInvoices => text + " Para leer más facturas, agregue el adicional de lectura de facturas en Plan y facturación.",
            Meter.AssistantQuestions or Meter.AssistantTokens => text + " Para preguntar más, agregue el adicional del asistente en Plan y facturación.",
            _ => text,
        };
    }

    private static readonly Dictionary<Meter, string> Names = new()
    {
        [Meter.AssistantQuestions] = "preguntas al asistente",
        [Meter.AssistantTokens] = "uso del asistente",
        [Meter.SupplierInvoices] = "lecturas de facturas con IA",
        [Meter.Emails] = "correos",
        [Meter.ArchiveRetrievals] = "consultas al archivo de comprobantes",
        [Meter.AccompanimentRequests] = "solicitudes de acompañamiento",
        [Meter.Invites] = "invitaciones",
    };

    private DateOnly Month()
    {
        var day = BusinessDay.Of(clock.GetUtcNow().UtcDateTime);
        return new DateOnly(day.Year, day.Month, 1);
    }

    private IQueryable<TenantUsage> Row(Guid tenantId, DateOnly month, Meter meter) =>
        db.TenantUsage.Where(u => u.TenantId == tenantId && u.Month == month && u.Meter == meter);

    private async Task<int> LimitAsync(Guid tenantId, Meter meter, CancellationToken ct)
    {
        var (plan, addOns) = await PlanAsync(tenantId, ct);
        return limits.MonthlyLimit(plan, addOns, meter);
    }

    private async Task<(string Plan, string[] AddOns)> PlanAsync(Guid tenantId, CancellationToken ct)
    {
        var t = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.Plan, t.AddOns }).FirstOrDefaultAsync(ct);
        return (string.IsNullOrEmpty(t?.Plan) ? DefaultPlan : t.Plan, SplitAddOns(t?.AddOns ?? ""));
    }

    private async Task<bool> TryInsertAsync(Guid tenantId, DateOnly month, Meter meter, int n, CancellationToken ct)
    {
        var row = new TenantUsage { TenantId = tenantId, Month = month, Meter = meter, Count = n };
        db.TenantUsage.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // the key exists: another call inserted it
        }
        finally
        {
            db.Entry(row).State = EntityState.Detached; // later updates go through ExecuteUpdate, never through this copy
        }
    }
}

/// <summary>The business's usage: Owners in the panel (<c>GET /api/usage</c>), the store for its invoice reading (<c>POST /ai/v1/supplier-invoice/quota</c>).</summary>
internal static class QuotaEndpoints
{
    public static void MapQuotas(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/usage", async (ClaimsPrincipal user, IQuota quota, CancellationToken ct) => Results.Ok(await quota.UsageAsync(user.GetTenantId(), ct)))
            .RequireAuthorization(CloudAuth.OwnerPolicy).RequireRateLimiting(QuotaRateLimits.PanelUser);

        app.MapPost("/ai/v1/supplier-invoice/quota", async (ClaimsPrincipal user, IQuota quota, CancellationToken ct) =>
        {
            var usage = await quota.UsageAsync(NodeIdentity.From(user).TenantId, ct);
            var meter = usage.Meters.Single(m => m.Meter == Meter.SupplierInvoices);
            return Results.Ok(new Abastalia.Purchasing.Contracts.SupplierInvoiceQuota(meter.Used, meter.Limit, usage.ResetsOn, usage.AddOns.Contains(Quota.SupplierInvoiceReading)));
        }).RequireAuthorization(CloudAuth.NodePolicy);
    }
}

/// <summary>Per-minute limits (anti-abuse) of the panel and the sign-in endpoints; the monthly ones are <see cref="IQuota"/>.</summary>
internal static class QuotaRateLimits
{
    /// <summary>Per IP: register, login, forgotPassword, resetPassword and the invite links (<c>Auth:RequestsPerMinute</c>, 10).</summary>
    public const string Auth = "auth";

    /// <summary>Per signed-in user: the panel API (<c>Panel:RequestsPerMinutePerUser</c>, 120).</summary>
    public const string PanelUser = "panel-user";

    /// <summary>Per signed-in user: questions to the assistant (<c>Ai:AssistantQuestionsPerMinutePerUser</c>, 10).</summary>
    public const string AssistantUser = "assistant-user";

    public static readonly string[] AuthRoutes = ["/register", "/login", "/forgotPassword", "/resetPassword"];

    /// <summary>A fixed one-minute window per <paramref name="partition"/> whose size is the business's plan limit <paramref name="key"/>
    /// (<see cref="PlanLimits.RateLimit"/>); <paramref name="fallback"/> without a business (signed out). The limit is part of the key, so a new
    /// plan gets its new window at once.</summary>
    public static RateLimitPartition<string> PerPlan(HttpContext http, string partition, string key, int fallback)
    {
        var limit = fallback;
        if (Guid.TryParse(http.User.FindFirstValue(Accounts.TenantClaims.TenantId), out var tenantId))
        {
            var (plan, addOns) = http.RequestServices.GetRequiredService<TenantPlans>().Of(tenantId);
            limit = http.RequestServices.GetRequiredService<PlanLimits>().RateLimit(plan, addOns, key, fallback);
        }
        return RateLimitPartition.GetFixedWindowLimiter($"{partition}|{limit}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(limit, 1), Window = TimeSpan.FromMinutes(1) });
    }

    public static string UserOrIp(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id ? "user:" + id : "ip:" + http.Connection.RemoteIpAddress;
}
