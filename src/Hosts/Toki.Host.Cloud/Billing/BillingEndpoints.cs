using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.Host.Cloud.Quotas;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Billing;

/// <param name="Monthly">A Paddle price exists for monthly billing; <paramref name="Annual"/> the same for yearly.</param>
public sealed record BillingOption(string Code, bool Monthly, bool Annual);

/// <summary>"Plan y facturación" (Owners). <paramref name="Enabled"/> false = Paddle is not set up: the page shows the plan and how to ask Toki.</summary>
/// <param name="Environment">sandbox or live, for Paddle.js; null when off.</param>
/// <param name="CanManage">A Paddle customer exists: the portal (card, invoices, cancel) can open.</param>
public sealed record BillingResponse(bool Enabled, string? Environment, string Plan, IReadOnlyList<string> AddOns, SubscriptionStatus Status, BillingInterval? Interval,
    DateTime? PaidThroughUtc, DateTime? NextBilledUtc, DateTime? PastDueSinceUtc, DateTime? ManualUntilUtc, bool CanManage,
    IReadOnlyList<BillingOption> Plans, IReadOnlyList<BillingOption> AddOnOptions);

public sealed record CheckoutRequest(string? Plan, IReadOnlyList<string>? AddOns, BillingInterval Interval = BillingInterval.Monthly);

public sealed record CheckoutItem(string PriceId, int Quantity);

/// <summary>What Paddle.js needs: <c>Paddle.Checkout.open({ items, customer: { email }, customData })</c>. <paramref name="CustomData"/> carries
/// the tenant id, which links the subscription to the business in every webhook.</summary>
public sealed record CheckoutResponse(string Environment, string ClientToken, IReadOnlyList<CheckoutItem> Items, string? CustomerEmail, IReadOnlyDictionary<string, string> CustomData);

/// <summary>A changed subscription (it already exists): Paddle prorates now and its webhook sets the plan.</summary>
public sealed record BillingChangeResponse(bool Changed);

public sealed record PortalResponse(string Url);

/// <summary>The store's license (TK-249), or null when its business has no paid time left.</summary>
public sealed record NodeLicenseResponse(string? License);

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
        services.AddScoped<PaddleWebhook>();
        services.AddHttpClient<PaddleApi>(c => c.Timeout = TimeSpan.FromSeconds(20));
        CloudLicenses.AddCloudLicenses(services, config, environment);
    }

    public static void MapBilling(this IEndpointRouteBuilder app)
    {
        // Paddle calls it: no account, only the signature (and each event once).
        app.MapPost(PaddleWebhook.Path, (HttpRequest request, PaddleWebhook webhook, CancellationToken ct) => webhook.HandleAsync(request, ct))
            .AllowAnonymous().ExcludeFromDescription();

        app.MapPost("/api/nodes/license", async (ClaimsPrincipal user, CloudLicenses licenses, CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            return Results.Ok(new NodeLicenseResponse(await licenses.IssueAsync(node.TenantId, node.StoreId, ct)));
        }).RequireAuthorization(CloudAuth.NodePolicy);

        var owner = app.MapGroup("/api/billing").RequireAuthorization(CloudAuth.OwnerPolicy).RequireRateLimiting(QuotaRateLimits.PanelUser);

        owner.MapGet("", async (ClaimsPrincipal user, CloudDbContext db, PaddleOptions paddle, IConfiguration config, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
            var s = await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct) ?? new TenantSubscription();
            var (plans, addOns) = Quota.Catalog(config);
            BillingOption Option(string code) => new(code, paddle.PriceOf(code, BillingInterval.Monthly) is not null, paddle.PriceOf(code, BillingInterval.Annual) is not null);
            return Results.Ok(new BillingResponse(paddle.IsConfigured, paddle.IsConfigured ? paddle.Environment : null,
                string.IsNullOrEmpty(tenant.Plan) ? Quota.DefaultPlan : tenant.Plan, Quota.SplitAddOns(tenant.AddOns), s.Status, s.Interval,
                s.PaidThroughUtc, s.NextBilledUtc, s.PastDueSinceUtc, s.ManualUntilUtc, paddle.IsConfigured && s.PaddleCustomerId is not null,
                paddle.IsConfigured ? [.. plans.Select(Option).Where(o => o.Monthly || o.Annual)] : [],
                paddle.IsConfigured ? [.. addOns.Select(Option).Where(o => o.Monthly || o.Annual)] : []));
        });

        // A business without a live subscription buys one with Paddle.js (overlay checkout); one that has it changes its items instead.
        owner.MapPost("/checkout", async (CheckoutRequest r, ClaimsPrincipal user, CloudDbContext db, PaddleOptions paddle, IConfiguration config, PaddleApi api, CancellationToken ct) =>
        {
            if (!paddle.IsConfigured) return EndpointExtensions.Problem(Off);
            var tenantId = user.GetTenantId();
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
            var s = await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
            var live = s is { PaddleSubscriptionId: not null } && s.Status is not (SubscriptionStatus.None or SubscriptionStatus.Canceled);
            var interval = live && s!.Interval is { } current ? current : r.Interval;
            var plan = r.Plan ?? (string.IsNullOrEmpty(tenant.Plan) ? Quota.DefaultPlan : tenant.Plan);
            var addOns = (r.AddOns ?? Quota.SplitAddOns(tenant.AddOns)).Distinct().ToList();
            var (plans, offered) = Quota.Catalog(config);
            List<string?> prices = [plans.Contains(plan) ? paddle.PriceOf(plan, interval) : null, .. addOns.Select(a => offered.Contains(a) ? paddle.PriceOf(a, interval) : null)];
            if (prices.Any(p => p is null))
                return EndpointExtensions.Problem(Error.Validation("Billing.InvalidPlan", "Ese plan o servicio adicional no se vende así. Elija uno de la lista."));

            if (live)
                return await api.ChangeItemsAsync(s!.PaddleSubscriptionId!, prices!, ct)
                    ? Results.Ok(new BillingChangeResponse(true))
                    : EndpointExtensions.Problem(Error.Conflict("Billing.PaddleFailed", "Paddle no aceptó el cambio. Intente de nuevo en un momento."));
            return Results.Ok(new CheckoutResponse(paddle.Environment, paddle.ClientToken, [.. prices.Select(p => new CheckoutItem(p!, 1))],
                user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name, new Dictionary<string, string> { ["tenantId"] = tenantId.ToString() }));
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
    }

    /// <summary>Toki staff (the platform group: authorized and audited). The override is for special deals and payments outside Paddle.</summary>
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
    }
}
