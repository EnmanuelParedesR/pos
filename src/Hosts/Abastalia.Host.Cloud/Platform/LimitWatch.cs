using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Persistence;

namespace Abastalia.Host.Cloud.Platform;

/// <summary>TK-311: what a limit refused (or a 5xx) in one minute, per reason, detail and masked IP. Written only while the database is awake.</summary>
public sealed class LimitRejection
{
    public long Id { get; set; }
    public DateTime MinuteUtc { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>The rate-limit policy or the route of a 5xx; "-" otherwise.</summary>
    public string Detail { get; set; } = "";
    /// <summary>The IP's /24 (IPv4) or /48 (IPv6), never the address; "-" for a 5xx.</summary>
    public string Network { get; set; } = "";
    /// <summary>A salted hash of the whole address (16 hex), so two IPs of one /24 stay apart.</summary>
    public string IpKey { get; set; } = "";
    public int Count { get; set; }
    public int RetryAfter { get; set; }
    /// <summary>Distinct stores that called from that IP in that minute (authenticated node calls).</summary>
    public int Stores { get; set; }

    internal static void Configure(ModelBuilder builder) => builder.Entity<LimitRejection>(e =>
    {
        e.ToTable("LimitRejections");
        e.Property(r => r.Reason).HasMaxLength(40);
        e.Property(r => r.Detail).HasMaxLength(100);
        e.Property(r => r.Network).HasMaxLength(50);
        e.Property(r => r.IpKey).HasMaxLength(16);
        e.HasIndex(r => r.MinuteUtc);
    });
}

public static class LimitReasons
{
    public const string LengthRequired = "Gate.LengthRequired", TooLarge = "Gate.TooLarge", Compressed = "Gate.Compressed",
        PerIpConcurrency = "Gate.PerIpConcurrency", PerIpRate = "Gate.PerIpRate",
        UnknownNode = "Node.Unknown", RevokedNode = "Node.Revoked", BadSignature = "Node.BadSignature",
        RateLimit = "RateLimit", Http5xx = "Http5xx";
}

/// <summary>TK-311 (D22): counts what the node gate, the node check and the rate limiters refuse, and 5xx, in memory. Each minute it writes one
/// log line per reason and IP (the Azure Monitor alerts read them, deploy/azure/main.bicep) and keeps the rows for the database, which get there
/// only when something else woke it (<see cref="SaveAsync"/>): a flood at night never keeps the serverless database awake.</summary>
/// <remarks>shortcut: the IP salt is per process, so the same IP gets a new key after a restart, and one instance only (B1); use a salt from
/// Key Vault if the console must follow an IP across deploys.</remarks>
public sealed class LimitWatch(TimeProvider clock, ILogger<LimitWatch> log)
{
    public const int MaxKeysPerMinute = 1000, MaxPending = 20_000, LoggedPerMinute = 50;
    public static readonly TimeSpan Keep = TimeSpan.FromDays(8);

    private static readonly byte[] Salt = RandomNumberGenerator.GetBytes(32);
    private readonly Lock _gate = new();
    private Dictionary<(string Reason, string Detail, string Network, string IpKey), (int Count, int RetryAfter)> _minute = [];
    private Dictionary<string, HashSet<Guid>> _stores = [];
    private List<LimitRejection> _pending = [];
    private int _dropped;
    private DateTime _lastPurge;

    public void Record(HttpContext http, string reason, string? detail = null, bool retryAfter = false, bool perIp = true)
    {
        var (network, ipKey) = perIp ? Mask(http.Connection.RemoteIpAddress) : ("-", "-");
        detail = string.IsNullOrEmpty(detail) ? "-" : detail.Length > 100 ? detail[..100] : detail;
        lock (_gate)
        {
            var key = (reason, detail, network, ipKey);
            if (!_minute.ContainsKey(key) && _minute.Count >= MaxKeysPerMinute) key = (reason, detail, "*", "other");
            var (count, after) = _minute.GetValueOrDefault(key);
            _minute[key] = (count + 1, after + (retryAfter ? 1 : 0));
        }
    }

    /// <summary>An authenticated node call from this IP: how many stores share it.</summary>
    public void SeenStore(HttpContext http, Guid storeId)
    {
        var (_, ipKey) = Mask(http.Connection.RemoteIpAddress);
        lock (_gate)
        {
            if (!_stores.TryGetValue(ipKey, out var stores))
            {
                if (_stores.Count >= MaxKeysPerMinute) return;
                _stores[ipKey] = stores = [];
            }
            stores.Add(storeId);
        }
    }

    /// <summary>Closes the minute: logs it and keeps its rows for the database. The worker calls it every minute.</summary>
    public void Roll()
    {
        Dictionary<(string Reason, string Detail, string Network, string IpKey), (int Count, int RetryAfter)> minute;
        Dictionary<string, HashSet<Guid>> stores;
        lock (_gate)
        {
            (minute, _minute) = (_minute, []);
            (stores, _stores) = (_stores, []);
        }
        if (minute.Count == 0) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var at = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var rows = minute.Select(kv => new LimitRejection
        {
            MinuteUtc = at, Reason = kv.Key.Reason, Detail = kv.Key.Detail, Network = kv.Key.Network, IpKey = kv.Key.IpKey, Count = kv.Value.Count,
            RetryAfter = kv.Value.RetryAfter, Stores = stores.GetValueOrDefault(kv.Key.IpKey)?.Count ?? 0,
        }).OrderByDescending(r => r.Count).ToList();
        // The alerts parse this line (deploy/azure/main.bicep): keep its words and order.
        foreach (var r in rows.Take(LoggedPerMinute))
            log.LogWarning("Limit watch {Reason} {Detail} net {Network} ip {IpKey}: {Count} rejections, {RetryAfter} with Retry-After, {Stores} stores",
                r.Reason, r.Detail, r.Network, r.IpKey, r.Count, r.RetryAfter, r.Stores);
        if (rows.Count > LoggedPerMinute)
            log.LogWarning("Limit watch: {Keys} more keys with {Count} rejections this minute", rows.Count - LoggedPerMinute, rows.Skip(LoggedPerMinute).Sum(r => r.Count));
        lock (_gate)
        {
            var room = Math.Max(0, MaxPending - _pending.Count);
            _pending.AddRange(rows.Take(room));
            _dropped += Math.Max(0, rows.Count - room);
        }
    }

    /// <summary>Writes the rows kept so far and, once an hour, deletes those older than <see cref="Keep"/>. Only call it while the database is
    /// awake anyway (the inbox applier's run, a staff page).</summary>
    public async Task SaveAsync(CloudDbContext db, CancellationToken ct)
    {
        Roll();
        List<LimitRejection> rows;
        int dropped;
        lock (_gate)
        {
            (rows, _pending) = (_pending, []);
            (dropped, _dropped) = (_dropped, 0);
        }
        if (dropped > 0) log.LogWarning("Limit watch dropped {Rows} rows: too many to keep until the database woke up", dropped);
        if (rows.Count > 0)
        {
            db.LimitRejections.AddRange(rows);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                lock (_gate) _pending.InsertRange(0, rows.Take(Math.Max(0, MaxPending - _pending.Count)).Select(r => { r.Id = 0; return r; }));
                throw;
            }
            finally
            {
                foreach (var r in rows) db.Entry(r).State = EntityState.Detached;
            }
        }
        var now = clock.GetUtcNow().UtcDateTime;
        if (now - _lastPurge < TimeSpan.FromHours(1)) return;
        _lastPurge = now;
        await db.LimitRejections.Where(r => r.MinuteUtc < now - Keep).ExecuteDeleteAsync(ct);
    }

    /// <summary>"190.80.12.0/24" (or the IPv6 /48) and a salted hash of the whole address.</summary>
    public static (string Network, string IpKey) Mask(IPAddress? ip)
    {
        if (ip is null) return ("?", "?");
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        var key = Convert.ToHexStringLower(HMACSHA256.HashData(Salt, bytes))[..16];
        var v4 = ip.AddressFamily == AddressFamily.InterNetwork;
        var prefix = new byte[bytes.Length];
        Array.Copy(bytes, prefix, v4 ? 3 : 6);
        return ($"{new IPAddress(prefix)}/{(v4 ? 24 : 48)}", key);
    }

    /// <summary>TK-311: every 5xx, by route (no IP). Inside the exception handler, so the endpoint is still known.</summary>
    public static void UseServerErrorWatch(WebApplication app)
    {
        var watch = app.Services.GetRequiredService<LimitWatch>();
        app.Use(async (http, next) =>
        {
            try
            {
                await next(http);
            }
            catch when (!http.RequestAborted.IsCancellationRequested)
            {
                watch.Record(http, LimitReasons.Http5xx, Route(http), perIp: false);
                throw;
            }
            if (http.Response.StatusCode >= 500) watch.Record(http, LimitReasons.Http5xx, Route(http), perIp: false);
        });
    }

    private static string Route(HttpContext http) => (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "-";
}

/// <summary>Closes each minute of <see cref="LimitWatch"/> (logs only, never the database).</summary>
/// <remarks>shortcut: rows not yet saved are lost on a restart (the log lines, which the alerts read, are not); the inbox applier saves them
/// every few minutes while stores send, so a deploy loses minutes at most.</remarks>
internal sealed class LimitWatchWorker(LimitWatch watch, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) watch.Roll();
        }
        catch (OperationCanceledException) { }
    }
}
