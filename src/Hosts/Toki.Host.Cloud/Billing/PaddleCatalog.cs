using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Toki.Host.Cloud.Persistence;
using Toki.Host.Cloud.Quotas;

namespace Toki.Host.Cloud.Billing;

/// <summary>A Paddle product as last synced: its prices and our part of its <c>custom_data</c> (<c>toki</c>), kept as JSON (docs/cobro-paddle.md §Catálogo).</summary>
public sealed class PaddleProduct
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary><c>custom_data.toki</c>, or null when the product is not one of Toki's plans or add-ons.</summary>
    public string? TokiJson { get; set; }
    /// <summary>Its active prices (<see cref="CatalogPrice"/>[]).</summary>
    public string PricesJson { get; set; } = "[]";
    public DateTime SyncedUtc { get; set; }
}

/// <summary>A price as the panel and /precios show it. <paramref name="Amount"/> in the currency's smallest unit, as Paddle sends it ("1000" = US$10.00).</summary>
public sealed record CatalogPrice(string PriceId, BillingInterval? Interval, string Amount, string Currency);

/// <summary><c>custom_data.toki</c> of a Paddle product: which plan or add-on it is and what it sets. Anything missing falls back to
/// appsettings (<c>Quotas:&lt;Plan&gt;</c>, <c>Billing:Plans:&lt;Plan&gt;</c>), then to Default.</summary>
/// <param name="Quotas">Monthly limits by <see cref="Quotas.Meter"/> name.</param>
/// <param name="RateLimits">Per-minute limits: <c>panelPerMinute</c>, <c>assistantPerMinute</c>, <c>supplierInvoicesPerMinute</c>.</param>
/// <param name="Referral">TK-255: the first payment of this plan earns the referrer's reward (as <c>Billing:Referrals:QualifyingPlans</c>).</param>
public sealed record TokiProduct(string? Plan, string? AddOn, IReadOnlyDictionary<string, int>? Quotas, IReadOnlyDictionary<string, int>? RateLimits,
    int? Registers, IReadOnlyList<string>? Features, bool? Referral = null)
{
    public string? Code => Plan ?? AddOn;
}

public sealed record CatalogEntry(string ProductId, string Name, TokiProduct Toki, IReadOnlyList<CatalogPrice> Prices);

/// <summary>Toki's plans and add-ons as Paddle has them (names, prices, limits), in memory for every request; the database keeps the last sync.
/// Paddle is the source of truth: prices and limits are edited there, never in code.</summary>
/// <remarks>shortcut: one App Service instance; with two, each refreshes on its own timer and a webhook reaches only one of them.</remarks>
public sealed class BillingCatalog(IServiceScopeFactory scopes)
{
    private IReadOnlyList<CatalogEntry>? _entries;
    private readonly Lock _gate = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<CatalogEntry> Entries
    {
        get
        {
            if (_entries is { } loaded) return loaded;
            lock (_gate)
            {
                if (_entries is not null) return _entries;
                using var scope = scopes.CreateScope();
                return _entries = Build(scope.ServiceProvider.GetRequiredService<CloudDbContext>().PaddleProducts.AsNoTracking().ToList());
            }
        }
    }

    /// <summary>The product of a plan or add-on code, or null.</summary>
    public CatalogEntry? Of(string code) => Entries.FirstOrDefault(e => e.Toki.Code == code);

    public (string Code, BillingInterval Interval)? Find(string priceId) =>
        Entries.SelectMany(e => e.Prices.Where(p => p.PriceId == priceId && p.Interval is not null).Select(p => (e.Toki.Code!, p.Interval!.Value)))
            .Cast<(string, BillingInterval)?>().FirstOrDefault();

    public CatalogPrice? PriceOf(string code, BillingInterval interval) => Of(code)?.Prices.FirstOrDefault(p => p.Interval == interval);

    internal void Replace(IEnumerable<PaddleProduct> rows) => _entries = Build(rows);

    private static List<CatalogEntry> Build(IEnumerable<PaddleProduct> rows) =>
        [.. rows.Select(r => (Row: r, Toki: r.TokiJson is null ? null : JsonSerializer.Deserialize<TokiProduct>(r.TokiJson, Json)))
            .Where(x => x.Toki?.Code is { Length: > 0 })
            .Select(x => new CatalogEntry(x.Row.Id, x.Row.Name, x.Toki!, JsonSerializer.Deserialize<List<CatalogPrice>>(x.Row.PricesJson, Json) ?? []))];
}

/// <summary>Reads Toki's products and prices from Paddle (<c>GET /products?include=prices</c>) and replaces the stored catalog.</summary>
internal sealed class PaddleCatalogSync(CloudDbContext db, PaddleApi api, BillingCatalog catalog, TenantPlans plans, TimeProvider clock)
{
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = (await api.ProductsAsync(ct)).Select(p => Row(p, now)).ToList();
        await db.PaddleProducts.ExecuteDeleteAsync(ct);
        db.PaddleProducts.AddRange(rows);
        await db.SaveChangesAsync(ct);
        catalog.Replace(rows);
        plans.ForgetAll(); // limits may have changed with the products
        return rows.Count(r => r.TokiJson is not null);
    }

    private static PaddleProduct Row(JsonElement product, DateTime now)
    {
        string? toki = product.TryGetProperty("custom_data", out var custom) && custom.ValueKind == JsonValueKind.Object
                       && custom.TryGetProperty("toki", out var t) && t.ValueKind == JsonValueKind.Object ? t.GetRawText() : null;
        var prices = product.TryGetProperty("prices", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(p => Text(p, "status") is null or "active").Select(Price).OfType<CatalogPrice>().ToList()
            : [];
        return new PaddleProduct
        {
            Id = Text(product, "id") ?? "", Name = Text(product, "name") ?? "", TokiJson = toki,
            PricesJson = JsonSerializer.Serialize(prices, BillingCatalog.Json), SyncedUtc = now,
        };
    }

    private static CatalogPrice? Price(JsonElement p)
    {
        if (Text(p, "id") is not { } id || !p.TryGetProperty("unit_price", out var unit) || unit.ValueKind != JsonValueKind.Object) return null;
        BillingInterval? interval = p.TryGetProperty("billing_cycle", out var cycle) && cycle.ValueKind == JsonValueKind.Object
            ? Text(cycle, "interval") switch { "month" => BillingInterval.Monthly, "year" => BillingInterval.Annual, _ => null }
            : null;
        return new CatalogPrice(id, interval, Text(unit, "amount") ?? "0", Text(unit, "currency_code") ?? "USD");
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Syncs the catalog on start, every <c>Billing:Paddle:CatalogSyncMinutes</c> (60) and soon after a <c>product.*</c> or <c>price.*</c>
/// webhook. Off with billing off, or with <c>Billing:Paddle:SyncInBackground</c> false (tests sync themselves).</summary>
internal sealed class CatalogSyncWorker(IServiceScopeFactory scopes, PaddleOptions paddle, IConfiguration config, CatalogSignal signal, ILogger<CatalogSyncWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!paddle.IsConfigured || !config.GetValue("Billing:Paddle:SyncInBackground", true)) return;
        var every = TimeSpan.FromMinutes(config.GetValue("Billing:Paddle:CatalogSyncMinutes", 60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PaddleCatalogSync>().SyncAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(e, "The Paddle catalog sync failed; the last one stays");
            }
            await signal.WaitAsync(every, stoppingToken);
        }
    }
}

/// <summary>A webhook asks for a sync now instead of at the next hour.</summary>
public sealed class CatalogSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public int Requests { get; private set; }

    public void Request()
    {
        Requests++;
        if (_signal.CurrentCount == 0) try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);

    public void Dispose() => _signal.Dispose();
}
