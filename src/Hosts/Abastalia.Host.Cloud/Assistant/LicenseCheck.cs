using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Persistence;

namespace Abastalia.Host.Cloud.Assistant;

/// <summary>R11 B-Low8: whether a business may use the paid extras (the AI invoice reader), decided here and not on the store's word. Allowed while the cloud subscription is paid (TK-249), with a
/// license Abastalia signed (the JWS the heartbeat carries, verified with Abastalia's public key) until its expiry plus the 15-day grace, or within the trial:
/// 14 days (R12 #10: the referred trial, 30, for a business that came with a code) from the business's first sale as the cloud's replica has it (or no sale yet). A store that says it is Restricted or Blocked is believed; a business blocked from the console gets none (TK-279).
/// The signed license only counts for the business it names (<see cref="LicenseData.TenantId"/>, TK-268): another business's, or one naming none, does not.</summary>
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

    private readonly int? _referredTrialDays;

    /// <param name="referrals">R12 #10: a referred business (its code kept, referrals on) gets <see cref="Billing.ReferralOptions.ReferredTrialDays"/>
    /// instead of 14 days, as its store does (/api/nodes/license).</param>
    public LicenseCheck(IConfiguration config, IHostEnvironment env, TimeProvider clock, Billing.ReferralOptions? referrals = null, Billing.PaddleOptions? paddle = null)
    {
        _clock = clock;
        _referredTrialDays = referrals is not null && paddle is not null && Billing.ReferralEndpoints.On(paddle, referrals) ? referrals.ReferredTrialDays : null;
        _key.ImportFromPem(!env.IsProduction() && config["License:PublicKey"] is { Length: > 0 } other ? other : AbastaliaPublicKey);
    }

    public async Task<bool> AllowsPaidExtrasAsync(CloudDbContext db, Guid tenantId, CancellationToken ct)
    {
        var beats = await db.NodeHeartbeats.AsNoTracking().Where(h => h.TenantId == tenantId).Select(h => new { h.SentUtc, h.LicenseStatus, h.LicenseJws }).ToListAsync(ct);
        if (beats.MaxBy(b => b.SentUtc)?.LicenseStatus is "Restricted" or "Blocked") return false;
        var now = _clock.GetUtcNow().UtcDateTime;
        // TK-249: what the business paid through the cloud (Paddle or staff override) counts as a license the cloud itself signed.
        var paid = await db.TenantSubscriptions.AsNoTracking().Where(s => s.TenantId == tenantId)
            .Select(s => new { s.PaidThroughUtc, s.ManualUntilUtc, s.BlockedUtc, s.ReferredByCode }).FirstOrDefaultAsync(ct);
        if (paid?.BlockedUtc is not null) return false; // TK-279: blocked from the console
        if (new[] { paid?.PaidThroughUtc, paid?.ManualUntilUtc }.Max() is { } until && now <= until + Grace) return true;
        if (beats.Any(b => b.LicenseJws is { } jws && ExpiresUtc(jws, tenantId) is { } expires && now <= expires + Grace)) return true;
        var firstSale = await db.Sales.Where(s => s.TenantId == tenantId).MinAsync(s => (DateTime?)s.SoldUtc, ct);
        var trial = paid?.ReferredByCode is not null && _referredTrialDays is { } days ? TimeSpan.FromDays(days) : Trial;
        return firstSale is null || now <= firstSale + trial;
    }

    /// <summary>The expiry of a compact JWS (ES256) signed with Abastalia's key for <paramref name="tenantId"/>, or null when it is not one.</summary>
    /// <remarks>R12 #10, transition: a license issued before TK-268 named no business; it counts until it expires (the next one issued names it).</remarks>
    internal DateTime? ExpiresUtc(string jws, Guid tenantId) =>
        Abastalia.BuildingBlocks.Security.Jws.Verify(jws, _key) is { IsSuccess: true } ok
        && (ok.Value.TenantId == tenantId || ok.Value.TenantId is null && Utc(ok.Value.IssuedUtc) < UnboundLicensesBefore)
            ? Utc(ok.Value.ExpiresUtc)
            : null;

    /// <summary>When TK-268 started naming the business in each license.</summary>
    public static readonly DateTime UnboundLicensesBefore = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
