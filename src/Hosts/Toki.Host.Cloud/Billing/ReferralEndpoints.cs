using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Billing;

/// <summary>The reward as configured: <paramref name="Amount"/> as Paddle takes it (percent, or the currency's smallest unit for flat).</summary>
public sealed record ReferralReward(string Type, string Amount, string Currency);

/// <summary>"Recomiende Toki" (Owners). <paramref name="NextDiscount"/>: the reward on the next payment (<c>FromUtc</c> = that billing) or queued
/// (<c>FromUtc</c> null: waiting for an active subscription or the next cycle); null when none is due.</summary>
public sealed record ReferralsResponse(bool Enabled, string? Code, int Pending, int Applied, ReferralReward? Reward, ReferralDiscountView? NextDiscount);
public sealed record ReferralDiscountView(string Type, string Amount, string Currency, DateTime? FromUtc);

public sealed record ReferralLookupResponse(bool Valid);

public sealed record ReferralView(Guid Id, Guid ReferrerTenantId, string ReferrerName, Guid ReferredTenantId, string ReferredName, ReferralStatus Status, string? Reason,
    string DiscountType, string DiscountAmount, string Currency, DateTime CreatedUtc, DateTime? AppliedUtc, DateTime? DiscountFromUtc, DateTime? RevertedUtc, string? Note);
public sealed record PlatformReferralsResponse(string? Code, string? ReferredByCode, IReadOnlyList<ReferralView> Referrals);

/// <param name="Action"><c>Revert</c> (takes the discount off if still unbilled), <c>Reinstate</c> (back to pending) or <c>Retry</c> (apply now).</param>
public sealed record ReferralAdjustRequest(string? Action, string? Note);

internal static class ReferralEndpoints
{
    public const string LookupRateLimit = "referral-lookup";

    public static void AddReferrals(IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection(ReferralOptions.Section).Get<ReferralOptions>() ?? new ReferralOptions();
        services.AddSingleton(options);
        services.AddScoped<ReferralRewards>();
        services.Configure<RateLimiterOptions>(o => o.AddPolicy(LookupRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "", _ => new FixedWindowRateLimiterOptions { PermitLimit = options.LookupsPerMinute, Window = TimeSpan.FromMinutes(1) })));
    }

    private static bool On(PaddleOptions paddle, ReferralOptions options) => paddle.IsConfigured && options.IsOn;

    /// <summary>The code from <c>/precios?ref=</c>, kept at the business's first checkout (never its own); the checkout's custom_data carries it to Paddle.</summary>
    public static async Task<Dictionary<string, string>> CheckoutDataAsync(CloudDbContext db, ReferralRewards rewards, Guid tenantId, string? code, CancellationToken ct)
    {
        var data = new Dictionary<string, string> { ["tenantId"] = tenantId.ToString() };
        var s = await BillingModel.SubscriptionAsync(db, tenantId, ct);
        if (s.ReferredByCode is null && s.FirstPaidUtc is null && await rewards.ReferrerOfAsync(code, ct) is { } referrer && referrer != tenantId)
        {
            s.ReferredByCode = ReferralModel.Normalize(code);
            await db.SaveChangesAsync(ct);
        }
        if (s.ReferredByCode is { } kept && await rewards.ReferrerOfAsync(kept, ct) is { } by)
            (data["referral_code"], data["referrer_tenant_id"]) = (kept, by.ToString());
        return data;
    }

    public static void MapReferrals(IEndpointRouteBuilder app, RouteGroupBuilder owner)
    {
        // /precios?ref=CODE checks the code before keeping it (no account; limited per IP against guessing).
        app.MapGet("/api/public/referrals/{code}", async (string code, PaddleOptions paddle, ReferralOptions options, ReferralRewards rewards, CancellationToken ct) =>
            On(paddle, options) && await rewards.ReferrerOfAsync(code, ct) is not null ? Results.Ok(new ReferralLookupResponse(true)) : Results.NotFound())
            .AllowAnonymous().RequireRateLimiting(LookupRateLimit);

        owner.MapGet("/referrals", async (ClaimsPrincipal user, CloudDbContext db, PaddleOptions paddle, ReferralOptions options, ReferralRewards rewards,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (!On(paddle, options)) return Results.Ok(new ReferralsResponse(false, null, 0, 0, null, null));
            var tenantId = user.GetTenantId();
            var code = await rewards.CodeOfAsync(tenantId, ct);
            var mine = await db.Referrals.AsNoTracking().Where(r => r.ReferrerTenantId == tenantId && (r.Status == ReferralStatus.Pending || r.Status == ReferralStatus.Applied))
                .ToListAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var next = mine.Where(r => r.Status == ReferralStatus.Applied && r.DiscountFromUtc >= now).OrderBy(r => r.DiscountFromUtc).FirstOrDefault()
                       ?? mine.Where(r => r.Status == ReferralStatus.Pending).OrderBy(r => r.CreatedUtc).FirstOrDefault();
            return Results.Ok(new ReferralsResponse(true, code, mine.Count(r => r.Status == ReferralStatus.Pending), mine.Count(r => r.Status == ReferralStatus.Applied),
                new ReferralReward(options.DiscountType, options.Amount, options.Currency),
                next is null ? null : new ReferralDiscountView(next.DiscountType, next.DiscountAmount, next.Currency, next.Status == ReferralStatus.Applied ? next.DiscountFromUtc : null)));
        });
    }

    /// <summary>Toki staff: the business's referrals (made and received) and the manual adjustments, audited by the platform group.</summary>
    public static void MapPlatformReferrals(RouteGroupBuilder platform)
    {
        platform.MapGet("/tenants/{tenantId:guid}/referrals", async (Guid tenantId, CloudDbContext db, CancellationToken ct) =>
        {
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) return EndpointExtensions.Problem(Error.NotFound("Platform.TenantNotFound", "No encontramos ese cliente."));
            var s = await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            var rows = await db.Referrals.AsNoTracking().Where(r => r.ReferrerTenantId == tenantId || r.ReferredTenantId == tenantId)
                .OrderByDescending(r => r.CreatedUtc).Take(200).ToListAsync(ct);
            var ids = rows.SelectMany(r => new[] { r.ReferrerTenantId, r.ReferredTenantId }).Distinct().ToList();
            var names = await db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            return Results.Ok(new PlatformReferralsResponse(s?.ReferralCode, s?.ReferredByCode, [.. rows.Select(r => new ReferralView(r.Id,
                r.ReferrerTenantId, names.GetValueOrDefault(r.ReferrerTenantId, ""), r.ReferredTenantId, names.GetValueOrDefault(r.ReferredTenantId, ""), r.Status, r.Reason,
                r.DiscountType, r.DiscountAmount, r.Currency, r.CreatedUtc, r.AppliedUtc, r.DiscountFromUtc, r.RevertedUtc, r.Note))]));
        });

        platform.MapPost("/tenants/{tenantId:guid}/referrals/{referralId:guid}", async (Guid tenantId, Guid referralId, ReferralAdjustRequest r, CloudDbContext db,
            ReferralRewards rewards, CancellationToken ct) =>
        {
            var referral = await db.Referrals.SingleOrDefaultAsync(x => x.Id == referralId && (x.ReferrerTenantId == tenantId || x.ReferredTenantId == tenantId), ct);
            if (referral is null) return EndpointExtensions.Problem(Error.NotFound("Referral.NotFound", "No encontramos esa recomendación."));
            var note = r.Note?.Trim() ?? "";
            if (r.Action is "Revert" or "Reinstate" && note.Length is 0 or > 500)
                return EndpointExtensions.Problem(Error.Validation("Referral.NoteRequired", "Escriba por qué, en 500 letras o menos."));
            switch (r.Action)
            {
                case "Revert":
                    if (!await rewards.RevertAsync(referral, "Staff", staff: true, ct))
                        return EndpointExtensions.Problem(Error.Conflict("Billing.PaddleFailed", "Paddle no quitó el descuento. Intente de nuevo en un momento."));
                    referral.Note = note;
                    break;
                case "Reinstate" when referral.Status is ReferralStatus.Rejected or ReferralStatus.Reverted:
                    (referral.Status, referral.Reason, referral.RevertedUtc, referral.Note) = (ReferralStatus.Pending, null, null, note);
                    break;
                case "Retry" when referral.Status == ReferralStatus.Pending:
                    break;
                default:
                    return EndpointExtensions.Problem(Error.Validation("Referral.InvalidAction", "Esa acción no aplica a esta recomendación."));
            }
            await db.SaveChangesAsync(ct);
            if (referral.Status == ReferralStatus.Pending) await rewards.TryApplyAsync(referral.ReferrerTenantId, ct);
            return Results.NoContent();
        });
    }
}
