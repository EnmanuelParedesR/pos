using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Billing;
using Abastalia.Host.Cloud.Persistence;

namespace Abastalia.Host.Cloud.Quotas;

/// <summary>TK-249: what each plan and add-on allows, merged: the Paddle product's <c>custom_data.abastalia</c> &gt; appsettings
/// (<c>Quotas:&lt;Plan&gt;</c>, <c>Quotas:&lt;Plan&gt;:RateLimits</c>, <c>Quotas:AddOns</c>) &gt; Default's (Paddle, then appsettings) &gt; the
/// global setting. Each add-on unit adds its own value to the plan's (TK-258: quantity add-ons; docs/cuotas.md, docs/cobro-paddle.md).</summary>
public sealed class PlanLimits(IConfiguration config, BillingCatalog catalog)
{
    public const string PanelPerMinute = "panelPerMinute", AssistantPerMinute = "assistantPerMinute", SupplierInvoicesPerMinute = "supplierInvoicesPerMinute";

    public int MonthlyLimit(string plan, IEnumerable<string> addOns, Meter meter) =>
        Merge(plan, addOns, meter.ToString(), t => t.Quotas, (code, key) => $"Quotas:{code}:{key}", $"Quotas:{Quota.AddOnsSection}:{{0}}:{meter}", 0);

    public int RateLimit(string plan, IEnumerable<string> addOns, string key, int fallback) =>
        Merge(plan, addOns, key, t => t.RateLimits, (code, k) => $"Quotas:{code}:RateLimits:{k}", $"Quotas:{Quota.AddOnsSection}:{{0}}:RateLimits:{key}", fallback);

    /// <summary>Plan codes (Default first) and add-on codes: those in appsettings and those Paddle's products name.</summary>
    public (IReadOnlyList<string> Plans, IReadOnlyList<string> AddOns) Catalog()
    {
        var plans = config.GetSection("Quotas").GetChildren().Select(c => c.Key).Where(k => k != Quota.AddOnsSection)
            .Concat(catalog.Entries.Select(e => e.Abastalia.Plan).OfType<string>()).Append(Quota.DefaultPlan).Distinct();
        var addOns = config.GetSection($"Quotas:{Quota.AddOnsSection}").GetChildren()
            .Concat(config.GetSection($"Billing:{Quota.AddOnsSection}").GetChildren()).Select(c => c.Key) // TK-258: add-ons with no quota (extra register, store)
            .Concat(catalog.Entries.Select(e => e.Abastalia.AddOn).OfType<string>()).Distinct();
        return ([.. plans.OrderBy(k => k != Quota.DefaultPlan).ThenBy(k => k, StringComparer.Ordinal)], [.. addOns.Order(StringComparer.Ordinal)]);
    }

    private int Merge(string plan, IEnumerable<string> addOns, string key, Func<AbastaliaProduct, IReadOnlyDictionary<string, int>?> of,
        Func<string, string, string> configKey, string addOnConfigKey, int fallback)
    {
        // Keys as the owner typed them in Paddle: "SupplierInvoices" or "supplierInvoices" both count.
        int? Paddle(string code) => catalog.Of(code)?.Abastalia is { } t && of(t) is { } values
            ? values.Where(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Select(kv => (int?)kv.Value).FirstOrDefault()
            : null;
        var limit = Paddle(plan) ?? config.GetValue<int?>(configKey(plan, key))
                    ?? Paddle(Quota.DefaultPlan) ?? config.GetValue<int?>(configKey(Quota.DefaultPlan, key)) ?? fallback;
        foreach (var addOn in addOns)
            if ((Paddle(addOn) ?? config.GetValue<int?>(string.Format(System.Globalization.CultureInfo.InvariantCulture, addOnConfigKey, addOn))) is { } raised)
                limit += raised;
        return limit;
    }
}

/// <summary>The plan and add-ons of each business for the per-minute limiters, which cannot wait on the database: kept a minute, forgotten at
/// once when the plan changes here (webhook, console).</summary>
public sealed class TenantPlans(IServiceScopeFactory scopes, TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, (string Plan, string[] AddOns, DateTime Until)> _plans = new();

    public (string Plan, string[] AddOns) Of(Guid tenantId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (_plans.TryGetValue(tenantId, out var cached) && cached.Until > now) return (cached.Plan, cached.AddOns);
        using var scope = scopes.CreateScope();
        var t = scope.ServiceProvider.GetRequiredService<CloudDbContext>().Tenants.AsNoTracking().Where(x => x.Id == tenantId)
            .Select(x => new { x.Plan, x.AddOns }).FirstOrDefault();
        var plan = (string.IsNullOrEmpty(t?.Plan) ? Quota.DefaultPlan : t.Plan, Quota.SplitAddOns(t?.AddOns ?? ""));
        _plans[tenantId] = (plan.Item1, plan.Item2, now.AddMinutes(1));
        return plan;
    }

    public void Forget(Guid tenantId) => _plans.TryRemove(tenantId, out _);

    public void ForgetAll() => _plans.Clear();
}
