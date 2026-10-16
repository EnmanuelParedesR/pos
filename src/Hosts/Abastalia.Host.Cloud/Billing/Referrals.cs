using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Persistence;

namespace Abastalia.Host.Cloud.Billing;

/// <summary>TK-255 "Recomiende Abastalia" (docs/cobro-paddle.md §Recomendaciones): a referral is a discount on the referrer's NEXT payment, earned only
/// when the referred business pays a qualifying plan (not when it signs up). Off while <see cref="Amount"/> is empty (the owner sets the values).</summary>
public sealed class ReferralOptions
{
    public const string Section = "Billing:Referrals";

    /// <summary>Plan codes (<c>MultiRegister</c>) or Paddle price ids whose first payment earns the reward. A Paddle product with
    /// <c>custom_data.abastalia.referral: true</c> qualifies too.</summary>
    public List<string> QualifyingPlans { get; set; } = [];
    /// <summary><c>percentage</c> or <c>flat</c> (Paddle's discount types).</summary>
    public string DiscountType { get; set; } = "percentage";
    /// <summary>As Paddle takes it: <c>10</c> = 10 % for percentage; the currency's smallest unit for flat (<c>500</c> = US$5.00).</summary>
    public string Amount { get; set; } = "";
    /// <summary>For flat discounts.</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>At most this many rewards per referrer per calendar month (UTC); the rest are recorded as rejected.</summary>
    public int MonthlyLimitPerReferrer { get; set; } = 5;
    /// <summary>Public code lookups per IP per minute (guessing codes).</summary>
    public int LookupsPerMinute { get; set; } = 20;

    public bool IsOn => DiscountType is "percentage" or "flat"
                        && decimal.TryParse(Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount > 0;
}

public enum ReferralStatus { Pending, Applied, Reverted, Rejected }

/// <summary>One per referred business, ever (unique <see cref="ReferredTenantId"/>): who brought it, the payment that earned it and the Paddle discount.</summary>
public sealed class Referral
{
    public Guid Id { get; set; }
    public Guid ReferrerTenantId { get; set; }
    public Guid ReferredTenantId { get; set; }
    public string Code { get; set; } = "";
    public ReferralStatus Status { get; set; }
    /// <summary>Why rejected or reverted: SelfReferral, SamePayer, SameRnc, MonthlyLimit, Refund, Staff.</summary>
    public string? Reason { get; set; }
    /// <summary>The referred business's first paid transaction (a refund or chargeback of it reverts the reward).</summary>
    public string TransactionId { get; set; } = "";
    public string DiscountType { get; set; } = "";
    public string DiscountAmount { get; set; } = "";
    public string Currency { get; set; } = "";
    public string? PaddleDiscountId { get; set; }
    public string? PaddleSubscriptionId { get; set; }
    /// <summary>The referrer's next billing when the discount was set: the payment it lowers.</summary>
    public DateTime? DiscountFromUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? AppliedUtc { get; set; }
    public DateTime? RevertedUtc { get; set; }
    /// <summary>Abastalia staff's reason for a manual change (the call itself is in PlatformAudit).</summary>
    public string? Note { get; set; }
}

internal static class ReferralModel
{
    public static void Configure(ModelBuilder builder) => builder.Entity<Referral>(e =>
    {
        e.ToTable("Referrals");
        e.HasIndex(r => r.ReferredTenantId).IsUnique(); // one reward per referred business, ever
        e.HasIndex(r => new { r.ReferrerTenantId, r.Status });
        e.HasIndex(r => r.TransactionId);
        e.Property(r => r.Code).HasMaxLength(20);
        e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        e.Property(r => r.Reason).HasMaxLength(40);
        e.Property(r => r.TransactionId).HasMaxLength(100);
        e.Property(r => r.DiscountType).HasMaxLength(20);
        e.Property(r => r.DiscountAmount).HasMaxLength(20);
        e.Property(r => r.Currency).HasMaxLength(3);
        e.Property(r => r.PaddleDiscountId).HasMaxLength(100);
        e.Property(r => r.PaddleSubscriptionId).HasMaxLength(100);
        e.Property(r => r.Note).HasMaxLength(500);
    });

    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I/L

    public static string NewCode() => RandomNumberGenerator.GetString(Alphabet, 8);

    /// <summary>The code as typed or linked, or null when it cannot be one of ours.</summary>
    public static string? Normalize(string? code) =>
        code?.Trim().ToUpperInvariant() is { Length: 8 } c && c.All(Alphabet.Contains) ? c : null;
}

/// <summary>Records, applies and reverts referral rewards. Every Paddle call is best effort: what fails stays Pending and is tried again on the
/// referrer's next subscription or payment event (or by staff).</summary>
internal sealed class ReferralRewards(CloudDbContext db, PaddleApi api, ReferralOptions options, BillingCatalog catalog, TimeProvider clock, ILogger<ReferralRewards> log)
{
    /// <summary>shortcut: one App Service instance; with two, two webhooks could set two discounts at once (Paddle keeps the last one).</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The business's code, made on first use.</summary>
    public async Task<string> CodeOfAsync(Guid tenantId, CancellationToken ct)
    {
        var s = await BillingModel.SubscriptionAsync(db, tenantId, ct);
        if (s.ReferralCode is { } code) return code;
        for (var attempt = 0; ; attempt++)
        {
            s.ReferralCode = ReferralModel.NewCode();
            try
            {
                await db.SaveChangesAsync(ct);
                return s.ReferralCode;
            }
            catch (DbUpdateException) when (attempt < 3) { } // another business has it (unique index): draw again
        }
    }

    public async Task<Guid?> ReferrerOfAsync(string? code, CancellationToken ct) => ReferralModel.Normalize(code) is { } c
        ? await db.TenantSubscriptions.Where(s => s.ReferralCode == c).Select(s => (Guid?)s.TenantId).FirstOrDefaultAsync(ct)
        : null;

    /// <summary>On <c>transaction.completed</c>, before the event is saved: the business's first paid transaction decides its referral, once.
    /// The referrer comes from the code kept at checkout, else from the transaction's <c>custom_data.referral_code</c>.</summary>
    public async Task<Referral?> RecordAsync(Guid tenantId, TenantSubscription s, JsonElement data, DateTime now, CancellationToken ct)
    {
        if (s.FirstPaidUtc is not null || Total(data) == "0") return null; // not the first payment, or nothing paid (a trial's checkout)
        s.FirstPaidUtc = now;
        if (!options.IsOn) return null;
        var code = s.ReferredByCode ?? (data.TryGetProperty("custom_data", out var custom) && custom.ValueKind == JsonValueKind.Object
            ? ReferralModel.Normalize(Text(custom, "referral_code")) : null);
        if (code is null || await ReferrerOfAsync(code, ct) is not { } referrer) return null;
        if (!Qualifies(PriceIds(data)) || await db.Referrals.AnyAsync(r => r.ReferredTenantId == tenantId, ct)) return null;

        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var reason = referrer == tenantId ? "SelfReferral"
            : await SamePayerAsync(referrer, tenantId, Text(data, "customer_id"), ct) ? "SamePayer"
            : await SameRncAsync(referrer, tenantId, ct) ? "SameRnc"
            : await db.Referrals.CountAsync(r => r.ReferrerTenantId == referrer && r.CreatedUtc >= monthStart
                && (r.Status == ReferralStatus.Pending || r.Status == ReferralStatus.Applied), ct) >= options.MonthlyLimitPerReferrer ? "MonthlyLimit"
            : null;
        if (reason is not null) log.LogWarning("Referral of {Referred} by {Referrer} rejected: {Reason}", tenantId, referrer, reason);
        return db.Referrals.Add(new Referral
        {
            Id = Guid.CreateVersion7(), ReferrerTenantId = referrer, ReferredTenantId = tenantId, Code = code,
            Status = reason is null ? ReferralStatus.Pending : ReferralStatus.Rejected, Reason = reason,
            TransactionId = Text(data, "id") ?? "", DiscountType = options.DiscountType, DiscountAmount = options.Amount,
            Currency = options.Currency, CreatedUtc = now,
        }).Entity;
    }

    /// <summary>A plan bought whose code or price id is in <see cref="ReferralOptions.QualifyingPlans"/>, or whose Paddle product says <c>referral: true</c>.</summary>
    private bool Qualifies(IEnumerable<string> priceIds) => priceIds.Any(p =>
        options.QualifyingPlans.Contains(p)
        || catalog.Find(p) is { } found && (options.QualifyingPlans.Contains(found.Code) || catalog.Of(found.Code)?.Abastalia.Referral == true));

    /// <summary>The same Paddle customer (Paddle has one per e-mail) or a shared owner account.</summary>
    private async Task<bool> SamePayerAsync(Guid referrer, Guid referred, string? customerId, CancellationToken ct)
    {
        if (customerId is not null && await db.TenantSubscriptions.AnyAsync(s => s.TenantId == referrer && s.PaddleCustomerId == customerId, ct)) return true;
        var owners = db.TenantMembers.Where(m => m.TenantId == referrer && m.Role == MemberRole.Owner).Select(m => m.UserId);
        return await db.TenantMembers.AnyAsync(m => m.TenantId == referred && m.Role == MemberRole.Owner && owners.Contains(m.UserId), ct);
    }

    /// <summary>An RNC both businesses gave the e-CF receiver, now or before (the only RNC the cloud keeps; one business holds it at a time).</summary>
    private async Task<bool> SameRncAsync(Guid referrer, Guid referred, CancellationToken ct)
    {
        var rncs = db.FeCertificates.Where(c => c.TenantId == referrer).Select(c => c.Rnc);
        return await db.FeCertificates.AnyAsync(c => c.TenantId == referred && rncs.Contains(c.Rnc), ct);
    }

    /// <summary>The referrer's oldest pending reward goes on its next payment: a custom Paddle discount (one use, not recurring) set on the
    /// subscription from the next billing period, after a preview. Waits while the subscription is not active, already has a discount
    /// (also ours for this cycle) or Paddle fails. Never throws: the webhook already answered for its own event.</summary>
    public async Task TryApplyAsync(Guid referrerTenantId, CancellationToken ct)
    {
        if (!options.IsOn) return;
        await Gate.WaitAsync(ct);
        try
        {
            var referral = await db.Referrals.Where(r => r.ReferrerTenantId == referrerTenantId && r.Status == ReferralStatus.Pending)
                .OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).FirstOrDefaultAsync(ct);
            if (referral is null) return;
            var s = await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == referrerTenantId, ct);
            if (s is not { PaddleSubscriptionId: { } subscriptionId, NextBilledUtc: { } nextBilled, Status: SubscriptionStatus.Active or SubscriptionStatus.Trialing })
                return; // past due, paused, canceled or never paid: queued
            if (await db.Referrals.AnyAsync(r => r.ReferrerTenantId == referrerTenantId && r.Status == ReferralStatus.Applied && r.DiscountFromUtc >= nextBilled, ct))
                return; // one per cycle: this payment already has one
            if (await api.SubscriptionAsync(subscriptionId, ct) is not { } paddle || Text(paddle, "status") is not ("active" or "trialing")
                || paddle.TryGetProperty("discount", out var current) && current.ValueKind == JsonValueKind.Object)
                return; // Paddle's view: not active, or a discount is already on it

            referral.PaddleDiscountId ??= await api.CreateDiscountAsync(new Dictionary<string, object?>
            {
                ["amount"] = referral.DiscountAmount, ["type"] = referral.DiscountType, ["description"] = "Abastalia: recomendación",
                ["enabled_for_checkout"] = false, ["recur"] = false, ["usage_limit"] = 1, ["mode"] = "custom",
                ["currency_code"] = referral.DiscountType == "flat" ? referral.Currency : null,
                ["restrict_to"] = PlanPrices(paddle) is { Count: > 0 } prices ? prices : null,
                ["custom_data"] = new { abastalia_referral_id = referral.Id.ToString() },
            }, ct);
            if (referral.PaddleDiscountId is null) return;
            await db.SaveChangesAsync(ct); // the discount is made once even if what follows fails
            if (!await api.PreviewDiscountAsync(subscriptionId, referral.PaddleDiscountId, ct) || !await api.SetDiscountAsync(subscriptionId, referral.PaddleDiscountId, ct))
            {
                log.LogWarning("Paddle did not take referral {ReferralId}'s discount; it stays pending", referral.Id);
                return;
            }
            (referral.Status, referral.PaddleSubscriptionId, referral.DiscountFromUtc, referral.AppliedUtc) =
                (ReferralStatus.Applied, subscriptionId, nextBilled, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or DbUpdateException or JsonException && !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Applying a referral reward for {Tenant} failed; it stays pending", referrerTenantId);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary><c>adjustment.created|updated</c>: an approved refund or chargeback of a referred business's first payment reverts its reward.
    /// False = Paddle could not take the discount off; the webhook answers 503 and Paddle retries.</summary>
    public async Task<bool> OnAdjustmentAsync(JsonElement data, CancellationToken ct)
    {
        if (Text(data, "action") is not ("refund" or "chargeback") || Text(data, "status") != "approved" || Text(data, "transaction_id") is not { } transaction)
            return true;
        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.TransactionId == transaction, ct);
        return referral is null || await RevertAsync(referral, "Refund", staff: false, ct);
    }

    /// <summary>Pending: reverted. Applied: the discount comes off the referrer's subscription if that payment has not happened yet; once billed,
    /// only staff can mark it reverted (the money is settled with the customer).</summary>
    public async Task<bool> RevertAsync(Referral referral, string reason, bool staff, CancellationToken ct)
    {
        if (referral.Status is ReferralStatus.Reverted or ReferralStatus.Rejected) return true;
        if (referral is { Status: ReferralStatus.Applied, PaddleSubscriptionId: { } subscriptionId, PaddleDiscountId: { } discountId })
        {
            var nextBilled = await db.TenantSubscriptions.Where(s => s.TenantId == referral.ReferrerTenantId).Select(s => s.NextBilledUtc).FirstOrDefaultAsync(ct);
            var billed = nextBilled > referral.DiscountFromUtc;
            if (billed && !staff)
            {
                log.LogWarning("Referral {ReferralId} was refunded after the referrer's discounted payment; left for staff", referral.Id);
                return true;
            }
            if (!billed && await api.SubscriptionAsync(subscriptionId, ct) is { } paddle && paddle.TryGetProperty("discount", out var d)
                && d.ValueKind == JsonValueKind.Object && Text(d, "id") == discountId && !await api.SetDiscountAsync(subscriptionId, null, ct))
                return false;
        }
        (referral.Status, referral.Reason, referral.RevertedUtc) = (ReferralStatus.Reverted, reason, clock.GetUtcNow().UtcDateTime);
        return true;
    }

    private static List<string> PriceIds(JsonElement data) => data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
        ? items.EnumerateArray().Select(i => i.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Object ? Text(p, "id") : Text(i, "price_id")).OfType<string>().ToList()
        : [];

    /// <summary>The subscription's plan prices (not add-ons): the discount lowers the plan only.</summary>
    private List<string> PlanPrices(JsonElement subscription) =>
        [.. PriceIds(subscription).Where(p => catalog.Find(p) is { } f && catalog.Of(f.Code)?.Abastalia.Plan is not null)];

    /// <summary><c>details.totals.grand_total</c>, or null when Paddle did not send it.</summary>
    private static string? Total(JsonElement data) => data.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object
        && d.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.Object ? Text(t, "grand_total") : null;

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
