using Microsoft.EntityFrameworkCore;
using Toki.Host.Cloud.Persistence;
using Toki.Host.Cloud.Quotas;

namespace Toki.Host.Cloud.Billing;

/// <summary>TK-249: Paddle Billing (docs/cobro-paddle.md). Empty <see cref="Environment"/> or any secret missing = billing off (the default):
/// the webhook answers 404, checkout refuses, and plans stay what Toki staff set in the console. The secrets come from Key Vault references.</summary>
public sealed class PaddleOptions
{
    public const string Section = "Billing:Paddle";

    /// <summary><c>sandbox</c> or <c>live</c>: separate Paddle accounts, keys and price ids.</summary>
    public string Environment { get; set; } = "";
    /// <summary>Server-side API key (<c>pdl_sdbx_apikey_…</c> / <c>pdl_live_apikey_…</c>): portal sessions and subscription changes.</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>The notification destination's secret key (<c>pdl_ntfset_…</c>), shown once when the destination is created.</summary>
    public string WebhookSecret { get; set; } = "";
    /// <summary>Client-side token for Paddle.js (<c>test_…</c> / <c>live_…</c>). Not secret: it goes to the browser.</summary>
    public string ClientToken { get; set; } = "";
    // TK-249: no price ids or amounts here: the products and prices come from Paddle (BillingCatalog), linked by custom_data.toki.

    public bool IsConfigured => Environment is "sandbox" or "live" && ApiKey.Length > 0 && WebhookSecret.Length > 0 && ClientToken.Length > 0;

    public string ApiUrl => Environment == "live" ? "https://api.paddle.com" : "https://sandbox-api.paddle.com";
}

public enum BillingInterval { Monthly, Annual }

/// <summary>Paddle's subscription statuses, plus None (never subscribed).</summary>
public enum SubscriptionStatus { None, Trialing, Active, PastDue, Paused, Canceled }

/// <summary>One per business: its Paddle subscription as the last webhook told it, and the staff override for special deals (TK-249).</summary>
public sealed class TenantSubscription
{
    public Guid TenantId { get; set; }
    public string? PaddleSubscriptionId { get; set; }
    public string? PaddleCustomerId { get; set; }
    public SubscriptionStatus Status { get; set; }
    public BillingInterval? Interval { get; set; }
    /// <summary>The end of the last period Paddle was paid for (or of the trial). It never moves while the payment is overdue, so the store's
    /// license runs out at the end of what was paid, then its 15 days of grace, then Restricted (never read-only).</summary>
    public DateTime? PaidThroughUtc { get; set; }
    public DateTime? NextBilledUtc { get; set; }
    public DateTime? PastDueSinceUtc { get; set; }
    /// <summary>Paddle does not deliver in order: an event older than this one is recorded but not applied.</summary>
    public DateTime? LastEventUtc { get; set; }
    /// <summary>Toki staff: paid through this date whatever Paddle says (special deals, payments outside Paddle). Audited (PlatformAudit).</summary>
    public DateTime? ManualUntilUtc { get; set; }
    public string? ManualNote { get; set; }
    /// <summary>When the subscription last changed; the cloud license is "issued" at this instant, so the store only installs a real change.</summary>
    public DateTime UpdatedUtc { get; set; }
    /// <summary>TK-255: this business's own referral code ("Recomiende Toki"), made on first use.</summary>
    public string? ReferralCode { get; set; }
    /// <summary>TK-255: the code it came with (<c>/precios?ref=</c>), kept at its first checkout.</summary>
    public string? ReferredByCode { get; set; }
    /// <summary>TK-255: its first paid transaction; only that one can earn its referrer a reward.</summary>
    public DateTime? FirstPaidUtc { get; set; }

    /// <summary>Until when the business has paid: the later of Paddle and the staff override.</summary>
    public DateTime? PaidUntilUtc => new[] { PaidThroughUtc, ManualUntilUtc }.Max();
}

/// <summary>Each Paddle event once (<c>event_id</c>): Paddle retries until it gets a 2xx, and a replayed request is answered without doing it again.</summary>
public sealed class BillingEvent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime OccurredUtc { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public Guid? TenantId { get; set; }
}

internal static class BillingModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<PaddleProduct>(e =>
        {
            e.ToTable("PaddleProducts");
            e.Property(p => p.Id).HasMaxLength(100);
            e.Property(p => p.Name).HasMaxLength(300);
            e.Property(p => p.TokiJson).HasMaxLength(CloudDbContext.LongText);
            e.Property(p => p.PricesJson).HasMaxLength(CloudDbContext.LongText);
        });
        builder.Entity<TenantSubscription>(e =>
        {
            e.ToTable("TenantSubscriptions");
            e.HasKey(s => s.TenantId);
            e.Property(s => s.PaddleSubscriptionId).HasMaxLength(100);
            e.Property(s => s.PaddleCustomerId).HasMaxLength(100);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.Interval).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.ManualNote).HasMaxLength(500);
            e.HasIndex(s => s.PaddleSubscriptionId);
            e.Property(s => s.ReferralCode).HasMaxLength(20);
            e.Property(s => s.ReferredByCode).HasMaxLength(20);
            e.HasIndex(s => s.ReferralCode).IsUnique().HasFilter("[ReferralCode] IS NOT NULL");
            e.Ignore(s => s.PaidUntilUtc);
        });
        ReferralModel.Configure(builder); // TK-255
        builder.Entity<BillingEvent>(e =>
        {
            e.ToTable("BillingEvents");
            e.Property(b => b.Id).HasMaxLength(100);
            e.Property(b => b.Type).HasMaxLength(100);
            e.HasIndex(b => b.TenantId);
        });
    }

    /// <summary>The business's plan and add-ons from the subscription's prices and quantities, the same fields staff set in the console (TK-248).</summary>
    public static (string? Plan, string AddOns) PlanOf(BillingCatalog catalog, PlanLimits limits, IEnumerable<CheckoutItem> items)
    {
        var (plans, addOns) = limits.Catalog();
        var codes = items.SelectMany(i => catalog.Find(i.PriceId) is { } f ? Enumerable.Repeat(f.Code, Math.Clamp(i.Quantity, 1, Quota.MaxAddOnQuantity)) : []).ToList();
        var plan = codes.FirstOrDefault(plans.Contains);
        return (plan is null or Quota.DefaultPlan ? null : plan, Quota.JoinAddOns(codes.Where(addOns.Contains)));
    }

    /// <summary>Statuses that keep the paid plan's quotas in the cloud; past due keeps them while Paddle retries the card (its dunning).</summary>
    public static bool KeepsPlan(SubscriptionStatus status) => status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue;

    public static async Task<TenantSubscription> SubscriptionAsync(CloudDbContext db, Guid tenantId, CancellationToken ct) =>
        await db.TenantSubscriptions.SingleOrDefaultAsync(s => s.TenantId == tenantId, ct)
        ?? db.TenantSubscriptions.Add(new TenantSubscription { TenantId = tenantId }).Entity;
}
