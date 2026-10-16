using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Persistence;

namespace Abastalia.Host.Cloud.Assistant;

/// <summary>R11 B-Low8: whether a business may use the paid extras (the AI invoice reader), decided here and not on the store's word. Allowed while the cloud subscription is paid (TK-249), with a
/// license Abastalia signed (the JWS the heartbeat carries, verified with Abastalia's public key) until its expiry plus the 15-day grace, or within the trial:
/// 14 days from the business's first sale as the cloud's replica has it (or no sale yet). A store that says it is Restricted is believed.</summary>
/// <remarks>shortcut: the license is not tied to the business (LicenseData.Store is a name): another business's license would pass. Bind it to the
/// tenant id when licenses are issued from the console.</remarks>
internal sealed class LicenseCheck
{
    public static readonly TimeSpan Grace = TimeSpan.FromDays(15);
    public static readonly TimeSpan Trial = TimeSpan.FromDays(14);

    /// <summary>Same key as the store's (Abastalia.Settings LicenseService). Only test and development hosts may trust another (<c>License:PublicKey</c>).</summary>
    private const string AbastaliaPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9b9VXQE8pkSL0IgzQKC3xqvtAw+0
        YaalpiHS8yL+saMYiVCHRb/4/izVOJx1hJ7OfWYUTaP4loWuxZ2+QU17RQ==
        -----END PUBLIC KEY-----
        """;

    private readonly ECDsa _key = ECDsa.Create();
    private readonly TimeProvider _clock;

    public LicenseCheck(IConfiguration config, IHostEnvironment env, TimeProvider clock)
    {
        _clock = clock;
        _key.ImportFromPem(!env.IsProduction() && config["License:PublicKey"] is { Length: > 0 } other ? other : AbastaliaPublicKey);
    }

    public async Task<bool> AllowsPaidExtrasAsync(CloudDbContext db, Guid tenantId, CancellationToken ct)
    {
        var beats = await db.NodeHeartbeats.AsNoTracking().Where(h => h.TenantId == tenantId).Select(h => new { h.SentUtc, h.LicenseStatus, h.LicenseJws }).ToListAsync(ct);
        if (beats.MaxBy(b => b.SentUtc)?.LicenseStatus == "Restricted") return false;
        var now = _clock.GetUtcNow().UtcDateTime;
        // TK-249: what the business paid through the cloud (Paddle or staff override) counts as a license the cloud itself signed.
        var paid = await db.TenantSubscriptions.AsNoTracking().Where(s => s.TenantId == tenantId).Select(s => new { s.PaidThroughUtc, s.ManualUntilUtc }).FirstOrDefaultAsync(ct);
        if (new[] { paid?.PaidThroughUtc, paid?.ManualUntilUtc }.Max() is { } until && now <= until + Grace) return true;
        if (beats.Any(b => b.LicenseJws is { } jws && ExpiresUtc(jws) is { } expires && now <= expires + Grace)) return true;
        var firstSale = await db.Sales.Where(s => s.TenantId == tenantId).MinAsync(s => (DateTime?)s.SoldUtc, ct);
        return firstSale is null || now <= firstSale + Trial;
    }

    /// <summary>The expiry of a compact JWS (ES256) signed with Abastalia's key, or null when it is not one.</summary>
    internal DateTime? ExpiresUtc(string jws) =>
        Abastalia.BuildingBlocks.Security.Jws.Verify(jws, _key) is { IsSuccess: true } ok
            ? ok.Value.ExpiresUtc.Kind == DateTimeKind.Local ? ok.Value.ExpiresUtc.ToUniversalTime() : DateTime.SpecifyKind(ok.Value.ExpiresUtc, DateTimeKind.Utc)
            : null;
}
