using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Toki.Host.Cloud.Persistence;
using Toki.Host.Cloud.Quotas;

namespace Toki.Host.Cloud.Billing;

/// <summary><c>Paddle-Signature: ts=&lt;unix&gt;;h1=&lt;hex&gt;</c>: HMAC SHA-256 of <c>ts:rawBody</c> with the destination's secret
/// (developer.paddle.com/webhooks/signature-verification). Any h1 may match (Paddle sends several while it rotates a secret).</summary>
public static class PaddleSignature
{
    /// <summary>Paddle's own SDKs allow 5 seconds; 5 minutes absorbs a slow retry and clock drift. A replay inside it is stopped by the event id.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static bool Matches(string? header, byte[] body, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header) || secret.Length == 0) return false;
        string? ts = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = part.IndexOf('=') is > 0 and var i ? (part[..i], part[(i + 1)..]) : (part, "");
            if (key == "ts") ts = value;
            else if (key == "h1" && value.Length == 64 && value.All(char.IsAsciiHexDigit)) signatures.Add(Convert.FromHexString(value));
        }
        if (ts is null || signatures.Count == 0 || !long.TryParse(ts, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, 253_402_300_799))).Duration() > Tolerance) return false;
        var expected = Hmac(secret, ts, body);
        return signatures.Any(s => CryptographicOperations.FixedTimeEquals(s, expected));
    }

    /// <summary>The header Paddle would send for <paramref name="body"/> at <paramref name="at"/> (tests and local tries).</summary>
    public static string Header(byte[] body, string secret, DateTimeOffset at)
    {
        var ts = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"ts={ts};h1={Convert.ToHexStringLower(Hmac(secret, ts, body))}";
    }

    private static byte[] Hmac(string secret, string ts, byte[] body) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), (byte[])[.. Encoding.ASCII.GetBytes(ts + ":"), .. body]);
}

/// <summary>POST /billing/paddle/webhook. Answers 2xx once the event is stored (Paddle retries anything else), each <c>event_id</c> once.
/// subscription.* set the subscription and the business's plan and add-ons; transaction.completed records the payment (paid through).</summary>
internal sealed class PaddleWebhook(CloudDbContext db, PaddleOptions paddle, BillingCatalog catalog, PlanLimits limits, TenantPlans plans, CatalogSignal signal,
    TimeProvider clock, ILogger<PaddleWebhook> log)
{
    public const string Path = "/billing/paddle/webhook";
    private const int MaxBody = 512 * 1024;

    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken ct)
    {
        if (!paddle.IsConfigured) return Results.NotFound();
        if (request.ContentLength > MaxBody) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBody;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();
        var now = clock.GetUtcNow();
        if (!PaddleSignature.Matches(request.Headers["Paddle-Signature"], body, paddle.WebhookSecret, now)) return Results.Unauthorized();

        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch (JsonException) { return Results.BadRequest(); }
        if (Text(root, "event_id") is not { Length: > 0 and <= 100 } id || Text(root, "event_type") is not { Length: > 0 } type || !root.TryGetProperty("data", out var data))
            return Results.BadRequest();
        if (await db.BillingEvents.AnyAsync(e => e.Id == id, ct)) return Results.Ok(); // a retry or a replay: done already
        var occurred = Date(root, "occurred_at") ?? now.UtcDateTime;

        var tenantId = type.StartsWith("subscription.", StringComparison.Ordinal) || type == "transaction.completed" ? await TenantOfAsync(type, data, ct) : null;
        if (tenantId is { } tenant)
        {
            var subscription = await BillingModel.SubscriptionAsync(db, tenant, ct);
            var row = await db.Tenants.SingleAsync(t => t.Id == tenant, ct);
            if (type == "transaction.completed") Paid(subscription, row, data, occurred, now.UtcDateTime);
            else Subscription(subscription, row, data, occurred, now.UtcDateTime);
        }
        else if (type.StartsWith("subscription.", StringComparison.Ordinal) || type == "transaction.completed")
            log.LogWarning("Paddle event {EventId} ({Type}) names no business of ours", id, type);
        // Someone changed a product or a price in Paddle: the catalog (prices, limits) syncs now rather than at the next hour.
        if (type.StartsWith("product.", StringComparison.Ordinal) || type.StartsWith("price.", StringComparison.Ordinal)) signal.Request();

        db.BillingEvents.Add(new BillingEvent { Id = id, Type = type.Length > 100 ? type[..100] : type, OccurredUtc = occurred, ReceivedUtc = now.UtcDateTime, TenantId = tenantId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // the same event delivered twice at once: the other request applied it; anything else is a real failure (Paddle retries)
            db.ChangeTracker.Clear();
            if (!await db.BillingEvents.AsNoTracking().AnyAsync(e => e.Id == id, ct)) throw;
        }
        if (tenantId is { } changed) plans.Forget(changed); // its per-minute limits follow the new plan at once
        return Results.Ok();
    }

    /// <summary><c>custom_data.tenantId</c> (set by our checkout, copied by Paddle to the subscription), else the subscription we already know.</summary>
    private async Task<Guid?> TenantOfAsync(string type, JsonElement data, CancellationToken ct)
    {
        if (data.TryGetProperty("custom_data", out var custom) && custom.ValueKind == JsonValueKind.Object && Guid.TryParse(Text(custom, "tenantId"), out var id)
            && await db.Tenants.AnyAsync(t => t.Id == id, ct))
            return id;
        var subscriptionId = type == "transaction.completed" ? Text(data, "subscription_id") : Text(data, "id");
        return subscriptionId is null ? null
            : await db.TenantSubscriptions.Where(s => s.PaddleSubscriptionId == subscriptionId).Select(s => (Guid?)s.TenantId).FirstOrDefaultAsync(ct);
    }

    private void Subscription(TenantSubscription s, Tenant tenant, JsonElement data, DateTime occurred, DateTime now)
    {
        if (s.LastEventUtc > occurred) return; // an older state arriving late
        s.LastEventUtc = occurred;
        s.PaddleSubscriptionId = Text(data, "id") ?? s.PaddleSubscriptionId;
        s.PaddleCustomerId = Text(data, "customer_id") ?? s.PaddleCustomerId;
        var status = Text(data, "status") switch
        {
            "active" => SubscriptionStatus.Active,
            "trialing" => SubscriptionStatus.Trialing,
            "past_due" => SubscriptionStatus.PastDue,
            "paused" => SubscriptionStatus.Paused,
            "canceled" => SubscriptionStatus.Canceled,
            _ => s.Status,
        };
        s.PastDueSinceUtc = status != SubscriptionStatus.PastDue ? null : s.PastDueSinceUtc ?? now;
        s.Status = status;
        if (data.TryGetProperty("billing_cycle", out var cycle) && cycle.ValueKind == JsonValueKind.Object)
            s.Interval = Text(cycle, "interval") == "year" ? BillingInterval.Annual : BillingInterval.Monthly;
        s.NextBilledUtc = Date(data, "next_billed_at");
        // Active or trialing: the current period is paid (or the trial's). Past due: Paddle already moved to the unpaid one, so it is not taken.
        if (status is SubscriptionStatus.Active or SubscriptionStatus.Trialing && Period(data, "current_billing_period") is { } end) Extend(s, end);
        SetPlan(s, tenant, data, now);
        s.UpdatedUtc = now;
    }

    private void Paid(TenantSubscription s, Tenant tenant, JsonElement data, DateTime occurred, DateTime now)
    {
        s.PaddleCustomerId = Text(data, "customer_id") ?? s.PaddleCustomerId;
        s.PaddleSubscriptionId = Text(data, "subscription_id") ?? s.PaddleSubscriptionId;
        if (Period(data, "billing_period") is { } end) Extend(s, end); // a payment is a fact, whatever order it arrives in
        if (s.LastEventUtc <= occurred || s.LastEventUtc is null)
        {
            s.LastEventUtc = occurred;
            if (s.Status is SubscriptionStatus.None or SubscriptionStatus.PastDue) (s.Status, s.PastDueSinceUtc) = (SubscriptionStatus.Active, null);
            SetPlan(s, tenant, data, now);
        }
        s.UpdatedUtc = now;
    }

    private static void Extend(TenantSubscription s, DateTime end)
    {
        if (s.PaidThroughUtc is null || end > s.PaidThroughUtc) s.PaidThroughUtc = end;
    }

    /// <summary>The prices bought set the plan and add-ons (TK-248's fields); an ended subscription goes back to Default unless staff gave a deal.</summary>
    private void SetPlan(TenantSubscription s, Tenant tenant, JsonElement data, DateTime now)
    {
        if (BillingModel.KeepsPlan(s.Status))
        {
            var prices = data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(i => (i.TryGetProperty("price", out var p) ? Text(p, "id") : Text(i, "price_id")) is { } id
                    ? new CheckoutItem(id, i.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var n) ? n : 1) : null).OfType<CheckoutItem>().ToList()
                : [];
            if (prices.Count > 0) (tenant.Plan, tenant.AddOns) = BillingModel.PlanOf(catalog, limits, prices);
        }
        else if (!(s.ManualUntilUtc > now)) (tenant.Plan, tenant.AddOns) = (null, "");
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    private static DateTime? Period(JsonElement data, string name) =>
        data.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? Date(p, "ends_at") : null;
}

/// <summary>The few Paddle API calls the cloud makes (Bearer API key, <c>Paddle-Version: 1</c>): a customer portal session and changing a subscription's items.</summary>
internal sealed class PaddleApi(HttpClient http, PaddleOptions paddle)
{
    /// <summary>POST /customers/{id}/portal-sessions: a one-time link to Paddle's portal (card, invoices, cancel). Never stored: it carries a token.</summary>
    public async Task<string?> PortalUrlAsync(string customerId, string? subscriptionId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/customers/{Uri.EscapeDataString(customerId)}/portal-sessions",
            new { subscription_ids = subscriptionId is null ? Array.Empty<string>() : [subscriptionId] }, ct);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.TryGetProperty("data", out var data) && data.TryGetProperty("urls", out var urls) && urls.TryGetProperty("general", out var general)
               && general.TryGetProperty("overview", out var overview) ? overview.GetString() : null;
    }

    /// <summary>PATCH /subscriptions/{id}: the new plan and add-ons, prorated now. Paddle then sends subscription.updated, which sets the plan here.</summary>
    public async Task<bool> ChangeItemsAsync(string subscriptionId, IEnumerable<CheckoutItem> items, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Patch, $"/subscriptions/{Uri.EscapeDataString(subscriptionId)}",
            new { items = items.Select(i => new { price_id = i.PriceId, quantity = i.Quantity }), proration_billing_mode = "prorated_immediately" }, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>GET /products?include=prices, every page (<c>meta.pagination.next</c>): all products with their prices.</summary>
    public async Task<List<JsonElement>> ProductsAsync(CancellationToken ct)
    {
        var products = new List<JsonElement>();
        string? next = paddle.ApiUrl + "/products?include=prices&per_page=200";
        for (var page = 0; next is not null && page < 50; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, next, null, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            products.AddRange(json.GetProperty("data").EnumerateArray().Select(p => p.Clone()));
            next = json.TryGetProperty("meta", out var meta) && meta.TryGetProperty("pagination", out var p) && p.TryGetProperty("has_more", out var more)
                   && more.ValueKind == JsonValueKind.True && p.TryGetProperty("next", out var n) && n.GetString() is { } url
                   && url.StartsWith(paddle.ApiUrl + "/", StringComparison.Ordinal) ? url : null; // never follow a link off Paddle's API with our key
        }
        return products;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path.StartsWith("https://", StringComparison.Ordinal) ? path : paddle.ApiUrl + path)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", paddle.ApiKey);
        request.Headers.Add("Paddle-Version", "1");
        return await http.SendAsync(request, ct);
    }
}
