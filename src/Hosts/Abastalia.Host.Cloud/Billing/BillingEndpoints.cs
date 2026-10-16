using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Catalog;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Quotas;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Billing;

/// <summary>A plan or add-on with its Paddle prices (null = not sold that way). <paramref name="Name"/> is the Paddle product's name.</summary>
public sealed record BillingOption(string Code, string Name, CatalogPrice? Monthly, CatalogPrice? Annual);

/// <summary>"Plan y facturación" (Owners). <paramref name="Enabled"/> false = Paddle is not set up: the page shows the plan and how to ask Abastalia.</summary>
/// <param name="Environment">sandbox or live, for Paddle.js; null when off.</param>
/// <param name="CanManage">A Paddle customer exists: the portal (card, invoices, cancel) can open.</param>
public sealed record BillingResponse(bool Enabled, string? Environment, string Plan, IReadOnlyList<string> AddOns, SubscriptionStatus Status, BillingInterval? Interval,
    DateTime? PaidThroughUtc, DateTime? NextBilledUtc, DateTime? PastDueSinceUtc, DateTime? ManualUntilUtc, bool CanManage,
    IReadOnlyList<BillingOption> Plans, IReadOnlyList<BillingOption> AddOnOptions);

/// <summary>The public /precios page: the prices as Paddle has them; empty lists with billing off (the page says "Precio por confirmar").</summary>
public sealed record PublicPricesResponse(bool Enabled, IReadOnlyList<BillingOption> Plans, IReadOnlyList<BillingOption> AddOns);

/// <param name="AddOns">Each code once per unit: ["ExtraRegister", "ExtraRegister"] buys two extra registers (TK-258).</param>
/// <param name="ReferralCode">TK-255: the code from <c>/precios?ref=</c> the panel kept; ignored when it is the business's own or one is kept already.</param>
public sealed record CheckoutRequest(string? Plan, IReadOnlyList<string>? AddOns, BillingInterval Interval = BillingInterval.Monthly, string? ReferralCode = null);

public sealed record CheckoutItem(string PriceId, int Quantity);

/// <summary>What Paddle.js needs: <c>Paddle.Checkout.open({ items, customer: { email }, customData })</c>. <paramref name="CustomData"/> carries
/// the tenant id, which links the subscription to the business in every webhook.</summary>
public sealed record CheckoutResponse(string Environment, string ClientToken, IReadOnlyList<CheckoutItem> Items, string? CustomerEmail, IReadOnlyDictionary<string, string> CustomData);

/// <summary>A changed subscription (it already exists): Paddle prorates now and its webhook sets the plan.</summary>
public sealed record BillingChangeResponse(bool Changed);

public sealed record PortalResponse(string Url);

/// <summary>The store's license (TK-249), or null when its business has no paid time left.</summary>
/// <param name="TrialDays">TK-276: the length of the store's trial when its business came with a referral code (30); null = the store's own 14.</param>
public sealed record NodeLicenseResponse(string? License, int? TrialDays = null);

/// <param name="ManualUntilUtc">Paid through this date whatever Paddle says; null removes the override.</param>
public sealed record BillingOverrideRequest(DateTime? ManualUntilUtc, string? Note);

public sealed record PlatformBillingResponse(TenantSubscriptionView Subscription, IReadOnlyList<BillingEventView> Events);
public sealed record TenantSubscriptionView(SubscriptionStatus Status, BillingInterval? Interval, string? PaddleSubscriptionId, string? PaddleCustomerId,
    DateTime? PaidThroughUtc, DateTime? NextBilledUtc, DateTime? PastDueSinceUtc, DateTime? ManualUntilUtc, string? ManualNote, DateTime? UpdatedUtc);
public sealed record BillingEventView(string Id, string Type, DateTime OccurredUtc, DateTime ReceivedUtc);

internal static class BillingEndpoints
{
    private static readonly Error Off = Error.Conflict("Billing.Off", "El pago en línea todavía no está disponible. Escríbanos y le activamos el plan.");

    public static void AddBilling(this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        services.AddSingleton(config.GetSection(PaddleOptions.Section).Get<PaddleOptions>() ?? new PaddleOptions());
        services.AddSingleton<BillingCatalog>();
        services.AddSingleton<CatalogSignal>();
        services.AddSingleton<PlanLimits>();
        services.AddSingleton<TenantPlans>();
        services.AddScoped<PaddleWebhook>();
        services.AddScoped<PaddleCatalogSync>();
        services.AddHostedService<CatalogSyncWorker>();
        services.AddHttpClient<PaddleApi>(c => c.Timeout = TimeSpan.FromSeconds(20));
        ReferralEndpoints.AddReferrals(services, config); // TK-255
        CloudLicenses.AddCloudLicenses(services, config, environment);
    }

    /// <summary>Each plan or add-on that has at least one price in Paddle (billing on), in the catalog's order.</summary>
    private static List<BillingOption> Options(IEnumerable<string> codes, BillingCatalog catalog, PaddleOptions paddle) => !paddle.IsConfigured ? [] :
        [.. codes.Select(code => catalog.Of(code) is { } e ? new BillingOption(code, e.Name, catalog.PriceOf(code, BillingInterval.Monthly), catalog.PriceOf(code, BillingInterval.Annual)) : null)
            .OfType<BillingOption>().Where(o => o.Monthly is not null || o.Annual is not null)];

    public static void MapBilling(this IEndpointRouteBuilder app)
    {
        // Paddle calls it: no account, only the signature (and each event once).
        app.MapPost(PaddleWebhook.Path, (HttpRequest request, PaddleWebhook webhook, CancellationToken ct) => webhook.HandleAsync(request, ct))
            .AllowAnonymous().ExcludeFromDescription();

        app.MapPost("/api/nodes/license", async (ClaimsPrincipal user, CloudLicenses licenses, CloudDbContext db, PaddleOptions paddle, ReferralOptions referrals,
            CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            var referred = ReferralEndpoints.On(paddle, referrals)
                && await db.TenantSubscriptions.AnyAsync(s => s.TenantId == node.TenantId && s.ReferredByCode != null, ct);
            return Results.Ok(new NodeLicenseResponse(await licenses.IssueAsync(node.TenantId, node.StoreId, ct), referred ? referrals.ReferredTrialDays : null));
        }).RequireAuthorization(CloudAuth.NodePolicy);

        // The public /precios page reads the real prices (no account; limited per IP like the public catalog).
        app.MapGet("/api/public/prices", (BillingCatalog catalog, PaddleOptions paddle, PlanLimits limits) =>
        {
            var (plans, addOns) = limits.Catalog();
            return Results.Ok(new PublicPricesResponse(paddle.IsConfigured, Options(plans, catalog, paddle), Options(addOns, catalog, paddle)));
        }).AllowAnonymous().RequireRateLimiting(PublicCatalogEndpoints.RateLimit);

        var owner = app.MapGroup("/api/billing").RequireAuthorization(CloudAuth.OwnerPolicy).RequireRateLimiting(QuotaRateLimits.PanelUser);

        owner.MapGet("", async (ClaimsPrincipal user, CloudDbContext db, PaddleOptions paddle, BillingCatalog catalog, PlanLimits limits, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
            var s = await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct) ?? new TenantSubscription();
            var (plans, addOns) = limits.Catalog();
            return Results.Ok(new BillingResponse(paddle.IsConfigured, paddle.IsConfigured ? paddle.Environment : null,
                string.IsNullOrEmpty(tenant.Plan) ? Quota.DefaultPlan : tenant.Plan, Quota.SplitAddOns(tenant.AddOns), s.Status, s.Interval,
                s.PaidThroughUtc, s.NextBilledUtc, s.PastDueSinceUtc, s.ManualUntilUtc, paddle.IsConfigured && s.PaddleCustomerId is not null,
                Options(plans, catalog, paddle), Options(addOns, catalog, paddle)));
        });

        // A business without a live subscription buys one with Paddle.js (overlay checkout); one that has it changes its items instead.
        owner.MapPost("/checkout", async (CheckoutRequest r, ClaimsPrincipal user, CloudDbContext db, PaddleOptions paddle, BillingCatalog catalog, PlanLimits limits,
            PaddleApi api, ReferralRewards referrals, CancellationToken ct) =>
        {
            if (!paddle.IsConfigured) return EndpointExtensions.Problem(Off);
            var tenantId = user.GetTenantId();
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
            var s = await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            var live = s is { PaddleSubscriptionId: not null } && s.Status is not (SubscriptionStatus.None or SubscriptionStatus.Canceled);
            var interval = live && s!.Interval is { } current ? current : r.Interval;
            var plan = r.Plan ?? (string.IsNullOrEmpty(tenant.Plan) ? Quota.DefaultPlan : tenant.Plan);
            // TK-258: an add-on listed n times is bought n times (extra registers, stores…): one item with that quantity.
            var addOns = (r.AddOns ?? Quota.SplitAddOns(tenant.AddOns)).GroupBy(a => a).ToList();
            var (plans, offered) = limits.Catalog();
            List<(string? PriceId, int Quantity)> prices = [(plans.Contains(plan) ? catalog.PriceOf(plan, interval)?.PriceId : null, 1),
                .. addOns.Select(a => (offered.Contains(a.Key) ? catalog.PriceOf(a.Key, interval)?.PriceId : null, a.Count()))];
            if (prices.Any(p => p.PriceId is null || p.Quantity > Quota.MaxAddOnQuantity))
                return EndpointExtensions.Problem(Error.Validation("Billing.InvalidPlan", "Ese plan o servicio adicional no se vende así. Elija uno de la lista."));
            List<CheckoutItem> items = [.. prices.Select(p => new CheckoutItem(p.PriceId!, p.Quantity))];

            if (live)
                return await api.ChangeItemsAsync(s!.PaddleSubscriptionId!, items, ct)
                    ? Results.Ok(new BillingChangeResponse(true))
                    : EndpointExtensions.Problem(Error.Conflict("Billing.PaddleFailed", "Paddle no aceptó el cambio. Intente de nuevo en un momento."));
            return Results.Ok(new CheckoutResponse(paddle.Environment, paddle.ClientToken, items,
                user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name, await ReferralEndpoints.CheckoutDataAsync(db, referrals, tenantId, r.ReferralCode, ct)));
        });

        owner.MapPost("/portal", async (ClaimsPrincipal user, CloudDbContext db, PaddleOptions paddle, PaddleApi api, CancellationToken ct) =>
        {
            if (!paddle.IsConfigured) return EndpointExtensions.Problem(Off);
            var tenantId = user.GetTenantId();
            var s = await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (s?.PaddleCustomerId is not { } customer)
                return EndpointExtensions.Problem(Error.Conflict("Billing.NoCustomer", "Todavía no tiene un pago con Paddle. Elija un plan primero."));
            return await api.PortalUrlAsync(customer, s.PaddleSubscriptionId, ct) is { } url
                ? Results.Ok(new PortalResponse(url))
                : EndpointExtensions.Problem(Error.Conflict("Billing.PaddleFailed", "Paddle no respondió. Intente de nuevo en un momento."));
        });

        ReferralEndpoints.MapReferrals(app, owner); // TK-255
    }

    /// <summary>Abastalia staff (the platform group: authorized and audited). The override is for special deals and payments outside Paddle.</summary>
    public static void MapPlatformBilling(RouteGroupBuilder platform)
    {
        platform.MapGet("/tenants/{tenantId:guid}/billing", async (Guid tenantId, CloudDbContext db, CancellationToken ct) =>
        {
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) return EndpointExtensions.Problem(Error.NotFound("Platform.TenantNotFound", "No encontramos ese cliente."));
            var s = await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            var events = await db.BillingEvents.AsNoTracking().Where(e => e.TenantId == tenantId).OrderByDescending(e => e.ReceivedUtc).Take(20)
                .Select(e => new BillingEventView(e.Id, e.Type, e.OccurredUtc, e.ReceivedUtc)).ToListAsync(ct);
            return Results.Ok(new PlatformBillingResponse(s is null
                ? new TenantSubscriptionView(SubscriptionStatus.None, null, null, null, null, null, null, null, null, null)
                : new TenantSubscriptionView(s.Status, s.Interval, s.PaddleSubscriptionId, s.PaddleCustomerId, s.PaidThroughUtc, s.NextBilledUtc, s.PastDueSinceUtc,
                    s.ManualUntilUtc, s.ManualNote, s.UpdatedUtc), events));
        });

        platform.MapPut("/tenants/{tenantId:guid}/billing", async (Guid tenantId, BillingOverrideRequest r, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) return EndpointExtensions.Problem(Error.NotFound("Platform.TenantNotFound", "No encontramos ese cliente."));
            var note = r.Note?.Trim() ?? "";
            if (r.ManualUntilUtc is not null && note.Length is 0 or > 500)
                return EndpointExtensions.Problem(Error.Validation("Billing.NoteRequired", "Escriba por qué (el acuerdo o el pago), en 500 letras o menos."));
            var s = await BillingModel.SubscriptionAsync(db, tenantId, ct);
            (s.ManualUntilUtc, s.ManualNote, s.UpdatedUtc) = (r.ManualUntilUtc is { } until ? (until.Kind == DateTimeKind.Local ? until.ToUniversalTime() : DateTime.SpecifyKind(until, DateTimeKind.Utc)) : null,
                r.ManualUntilUtc is null ? null : note, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        ReferralEndpoints.MapPlatformReferrals(platform); // TK-255
    }
}
