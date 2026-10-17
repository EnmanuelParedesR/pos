using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Fiscal.Contracts;
using Abastalia.Inventory.Contracts;
using Abastalia.Sales.Contracts;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Settings;

/// <summary>The installed license, read from <c>License:File</c> (C:\ProgramData\Abastalia\license.jws). The status follows the clock:
/// Active until it expires, then 15 days of Grace, then Restricted, then Blocked (TK-279) once <see cref="LicenseData.BlockAfterDays"/> (30) have
/// passed since it expired, or at once when the license says Abastalia blocked the business. Without a license the store runs as Trial for 14 days
/// from its first sale (TK-196; 30 when the cloud says the business came recommended, TK-276), then Restricted (TK-220), then Blocked 30 days after
/// the trial ended. Installing a license lifts any of them at once, without reinstalling and keeping every sale. The clock is <see cref="LicenseClock"/>:
/// turning the PC's time back does not stretch it.</summary>
internal sealed class LicenseService : IFeatureService
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(15);
    public static readonly TimeSpan TrialPeriod = TimeSpan.FromDays(14);

    /// <summary>TK-279: days from expiry (or the trial's end) to Blocked when the license does not say (the trial never does).</summary>
    public const int DefaultBlockAfterDays = 30;

    /// <summary>Registers a store without a valid license may pair (the main PC is not counted).</summary>
    public const int TrialRegisters = 2;

    /// <summary>"1 caja" once Restricted: the main PC plus one register keep selling (a store whose main PC is a back-office server still sells).</summary>
    public const int RestrictedRegisters = 1;

    /// <summary>The vendor's public key (licenses issued offline by the owner, docs/licencias.md). Its private key never ships with the product.</summary>
    private const string AbastaliaPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9b9VXQE8pkSL0IgzQKC3xqvtAw+0
        YaalpiHS8yL+saMYiVCHRb/4/izVOJx1hJ7OfWYUTaP4loWuxZ2+QU17RQ==
        -----END PUBLIC KEY-----
        """;

    /// <summary>TK-249: the cloud's license key (EC P-256 in Azure Key Vault, non-exportable), for licenses of a paid subscription.
    /// Empty until the owner creates the key (docs/cobro-paddle.md §Licencias): paste its PEM here and ship an update.</summary>
    private const string AbastaliaCloudPublicKey = "";

    private readonly TimeProvider _clock;
    private readonly IServiceScopeFactory _scopes;
    private readonly ServiceKeys _secrets;
    private readonly IDgiiTime? _dgii;
    private readonly Guid _tenant;
    private readonly ECDsa[] _keys;
    private readonly Lock _gate = new();
    private bool _trialLoaded;
    private DateTime? _trialStartedUtc;
    private int? _trialDays;
    private LicenseClock? _time;

    /// <param name="tenant">TK-320: the business (a store's is <see cref="StoreTenant.Id"/>). A Lite business keeps its license in
    /// <c>tenants/{id}.jws</c> next to <c>License:File</c>, and its sealed values with its id in their names.</param>
    public LicenseService(IConfiguration config, IHostEnvironment environment, TimeProvider clock, IServiceScopeFactory scopes, ServiceKeys secrets, IDgiiTime? dgii = null,
        Guid tenant = default)
    {
        (_clock, _scopes, _secrets, _dgii, _tenant) = (clock, scopes, secrets, dgii, tenant);
        // Only test and development hosts may trust other keys; a store (Production) always checks against Abastalia's.
        var test = !environment.IsProduction();
        _keys = [.. new[]
            {
                test && config["License:PublicKey"] is { Length: > 0 } offline ? offline : AbastaliaPublicKey,
                test && config["License:CloudPublicKey"] is { Length: > 0 } cloud ? cloud : AbastaliaCloudPublicKey,
            }
            .Where(pem => pem.Length > 0)
            .Select(pem =>
            {
                var key = ECDsa.Create();
                key.ImportFromPem(pem);
                return key;
            })];
        FilePath = config["License:File"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Abastalia", "license.jws");
        if (tenant != StoreTenant.Id) FilePath = Path.Combine(Path.GetDirectoryName(FilePath)!, "tenants", $"{tenant:N}.jws");
        if (File.Exists(FilePath) && Jws.Verify(File.ReadAllText(FilePath), _keys) is { IsSuccess: true } saved)
            (_data, _current, _installedRnc) = (saved.Value, File.ReadAllText(FilePath).Trim(), Rnc.Normalize(saved.Value.Rnc));
    }

    public static readonly Error Older = Error.Conflict("License.Older", "Esta licencia es más antigua que la última instalada. Pídale a Abastalia la licencia vigente.");
    public static readonly Error OtherBusiness = Error.Validation("License.OtherBusiness",
        "Esta licencia es de otro negocio: su RNC no es el de esta tienda (Configuración → Negocio).");

    private LicenseData? _data;
    private string? _current;
    private volatile bool _rncChecked;
    private volatile string? _installedRnc;

    /// <summary>TK-368: the RNC (digits) the license installed on this PC names, also when that license is ignored because the business RNC is
    /// another one; null without a license or for one issued before R12. The business RNC cannot move away from it, and no e-CF is issued for another.</summary>
    public string? LicensedRnc => _installedRnc;

    /// <summary>The installed license as signed (JWS), or null.</summary>
    public string? Current => Data is null ? null : _current;

    /// <summary>R11 B-Low8: the heartbeat sends it so the cloud checks it itself.</summary>
    string? IFeatureService.LicenseJws => Current;

    public string FilePath { get; }

    /// <summary>R12: a license saved for another business's RNC is ignored (checked once, when first read: the database is ready by then).</summary>
    public LicenseData? Data
    {
        get
        {
            if (!_rncChecked)
            {
                if (_data is { Rnc: { } rnc } && !SameRnc(rnc, BusinessRnc())) _data = null;
                _rncChecked = true;
            }
            return _data;
        }
    }

    private string? BusinessRnc() => Read(sp => sp.GetRequiredService<SettingsDbContext>().Business.AsNoTracking().Select(b => b.Rnc).SingleOrDefault());

    private static bool SameRnc(string license, string? business) =>
        business is not null && license.Where(char.IsDigit).SequenceEqual(business.Where(char.IsDigit));

    /// <summary>The store's first sale (settings.Business), read once and then kept here; <see cref="StartTrialOnFirstSale"/> sets it.</summary>
    public DateTime? TrialStartedUtc
    {
        get
        {
            lock (_gate) return LoadTrial()._trialStartedUtc;
        }
    }

    /// <summary>TK-276: how long the trial lasts: 14 days, or what the cloud said for a business that came recommended (30).</summary>
    public TimeSpan TrialLength
    {
        get
        {
            lock (_gate) return LoadTrial()._trialDays is { } days ? TimeSpan.FromDays(days) : TrialPeriod;
        }
    }

    /// <summary>Reads the trial's start and length once per process. TK-369: they have a sealed twin in the keys folder (<see cref="TrialFile"/>),
    /// so clearing the start in the database (or setting it later, or giving it more days) does not restart or stretch the trial: the earlier start
    /// wins and the length is the sealed one. Without the twin (a store before TK-369, a backup restored on a new PC) the database's values are
    /// taken, the days at most <see cref="MaxTrialDays"/>, and sealed.</summary>
    private LicenseService LoadTrial()
    {
        if (_trialLoaded) return this;
        var trial = Read(sp => sp.GetRequiredService<SettingsDbContext>().Business.AsNoTracking().Select(b => new { b.TrialStartedUtc, b.TrialDays }).SingleOrDefault());
        if (ReadSealed(TrialFile)?.Split('|') is [var start, var days])
        {
            DateTime? sealedStart = long.TryParse(start, out var t) ? new DateTime(t, DateTimeKind.Utc) : null;
            _trialStartedUtc = new[] { trial?.TrialStartedUtc, sealedStart }.Where(d => d is not null).Min();
            _trialDays = int.TryParse(days, out var d) ? d : null;
        }
        else (_trialStartedUtc, _trialDays) = (trial?.TrialStartedUtc, trial?.TrialDays is { } d ? Math.Min(d, MaxTrialDays) : null);
        SealTrial();
        _trialLoaded = true;
        return this;
    }

    /// <summary>TK-276: the longest trial a signed grant may give (and the most a pre-TK-369 database is believed).</summary>
    public const int MaxTrialDays = 90;

    internal const string TrialFile = "license-trial.seen";

    private void SealTrial() => WriteSealed(TrialFile, $"{_trialStartedUtc?.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{_trialDays}");

    /// <summary>TK-279: loaded once from the latest time this store saw: the sealed copy in settings.Business (ignored when its seal does not
    /// match, e.g. edited by hand), R12 B1 its sealed twin in the keys folder (so wiping the database's copy or its seal gains nothing), the newest
    /// audit record (hash-chained) and the installed license's issue date (signed).</summary>
    public LicenseClock Time
    {
        get
        {
            lock (_gate)
            {
                return _time ??= new LicenseClock(_clock, Read(sp =>
                {
                    var saved = sp.GetRequiredService<SettingsDbContext>().Business.AsNoTracking().Select(b => new { b.ClockSeenUtc, b.ClockSeenSeal }).SingleOrDefault();
                    var audited = sp.GetRequiredService<InfraDbContext>().AuditRecords.Max(r => (DateTime?)r.DateUtc);
                    var kept = ReadSealed(SeenFile) is { } ticks && long.TryParse(ticks, out var t) ? new DateTime(t, DateTimeKind.Utc) : (DateTime?)null;
                    return new[] { LicenseClock.Unseal(saved?.ClockSeenUtc, saved?.ClockSeenSeal, ClockKey), kept, audited, Data?.IssuedUtc }.Max();
                }));
            }
        }
    }

    /// <summary>Keeps the latest time seen, sealed so that editing the database alone does not move it (<see cref="LicenseClockKeeper"/>).</summary>
    public async Task SaveTimeAsync(CancellationToken ct)
    {
        if (_time is null) return; // nothing read yet
        var seen = Time.SeenUtc;
        var seal = LicenseClock.Seal(seen, ClockKey);
        WriteSealed(SeenFile, seen.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await using var scope = _scopes.CreateTenantScope(_tenant);
        await scope.ServiceProvider.GetRequiredService<SettingsDbContext>().Business
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.ClockSeenUtc, seen).SetProperty(b => b.ClockSeenSeal, seal), ct);
    }

    /// <summary>A secret of this server kept outside the database (R11 A3), like the PIN pepper.</summary>
    private byte[] ClockKey => _secrets.GetOrCreate("license-clock");

    internal const string SeenFile = "license-clock.seen", IssuedFile = "license-issued.seen";

    /// <summary>R12: small values kept in the keys folder (outside the database), with an HMAC of this server's secret and the file's name.</summary>
    internal void WriteSealed(string name, string value)
    {
        Directory.CreateDirectory(_secrets.Folder);
        var path = Path.Combine(_secrets.Folder, SealedPrefix + name);
        File.WriteAllText(path + ".tmp", $"{value}|{SealOf(name, value)}");
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>The value, or null when the file is missing or its seal does not match.</summary>
    internal string? ReadSealed(string name)
    {
        var path = Path.Combine(_secrets.Folder, SealedPrefix + name);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        var bar = text.LastIndexOf('|');
        if (bar < 0) return null;
        var value = text[..bar];
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(text[(bar + 1)..]), Encoding.ASCII.GetBytes(SealOf(name, value))) ? value : null;
    }

    private string SealedPrefix => _tenant == StoreTenant.Id ? "" : $"{_tenant:N}.";

    private string SealOf(string name, string value) => Convert.ToBase64String(HMACSHA256.HashData(ClockKey, Encoding.UTF8.GetBytes($"{name}:{value}")));

    /// <summary>R12 (security M2): the newest license ever installed here, and whether it was a block: an older license never undoes it, nor does
    /// deleting license.jws. Before the first install with this version: the installed license itself.</summary>
    private (DateTime IssuedUtc, bool Blocked)? Newest
    {
        get
        {
            lock (_gate)
            {
                if (_newestLoaded) return _newest;
                _newest = ReadSealed(IssuedFile)?.Split('|') is [var ticks, var blocked] && long.TryParse(ticks, out var t)
                    ? (new DateTime(t, DateTimeKind.Utc), blocked == "1")
                    : _data is { } d ? (d.IssuedUtc, d.Blocked) : null;
                _newestLoaded = true;
                return _newest;
            }
        }
    }

    private (DateTime IssuedUtc, bool Blocked)? _newest;
    private bool _newestLoaded;

    /// <summary>On its own connection. The scope only disposes asynchronously (UnitOfWork).</summary>
    private T Read<T>(Func<IServiceProvider, T> read)
    {
        var scope = _scopes.CreateTenantScope(_tenant);
        try
        {
            return read(scope.ServiceProvider);
        }
        finally
        {
            scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public void TrialStarted(DateTime utc)
    {
        lock (_gate)
        {
            LoadTrial()._trialStartedUtc = utc;
            SealTrial();
        }
    }

    public void TrialLengthChanged(int days)
    {
        lock (_gate)
        {
            LoadTrial()._trialDays = days;
            SealTrial();
        }
    }

    public DateTime? TrialEndsUtc => TrialStartedUtc + TrialLength;

    public DateTime? EndsUtc => Data?.ExpiresUtc ?? TrialEndsUtc;

    /// <summary>TK-279: when the store goes Blocked by date: 30 days (or what the license says) after it expired or the trial ended.</summary>
    public DateTime? BlocksUtc => Data is { } d ? d.ExpiresUtc.AddDays(d.BlockAfterDays ?? DefaultBlockAfterDays) : TrialEndsUtc?.AddDays(DefaultBlockAfterDays);

    private DateTime? _dgiiSeen;

    /// <summary>The license clock, set by the DGII's time at each new answer (TK-156): never behind it and, R12, brought back down from a date
    /// far ahead (a PC that once booted in the future).</summary>
    private DateTime Now
    {
        get
        {
            if (_dgii?.LastDgiiUtc is { } dgii && dgii != _dgiiSeen)
            {
                _dgiiSeen = dgii;
                Time.Correct(dgii);
            }
            return Time.UtcNow;
        }
    }

    public LicenseStatus Status
    {
        get
        {
            var now = Now;
            if (BlockedBy(now) is not null) return LicenseStatus.Blocked;
            if (Data is null) return TrialEndsUtc is { } end && now > end ? LicenseStatus.Restricted : LicenseStatus.Trial;
            return now <= Data.ExpiresUtc ? LicenseStatus.Active : now <= Data.ExpiresUtc + GracePeriod ? LicenseStatus.Grace : LicenseStatus.Restricted;
        }
    }

    /// <summary>R12: why the store is Blocked: "Suspended" (Abastalia blocked the business, also when that license was deleted or replaced by an
    /// older one), "Trial" (30 days after the trial) or "Expired"; null when it is not.</summary>
    private string? BlockedBy(DateTime now) =>
        Data is { Blocked: true } || (Newest is { Blocked: true } newest && !(Data?.IssuedUtc > newest.IssuedUtc)) ? "Suspended"
        : now > BlocksUtc ? Data is null ? "Trial" : "Expired"
        : null;

    public string? BlockReason => BlockedBy(Now);

    public Error BlockedError => BlockReason switch { "Suspended" => Jws.Suspended, "Trial" => Jws.TrialOver, _ => Jws.Blocked };

    /// <summary>Days left of the trial (the day in course counts), or null outside the trial or before its first sale.</summary>
    public int? TrialDaysLeft => Status == LicenseStatus.Trial && TrialEndsUtc is { } end ? (int)Math.Ceiling((end - Now).TotalDays) : null;

    /// <summary>TK-220: "Soon" from day 10 of the trial (5 days left), "Last" from day 13 (2 left), "Ended" once Restricted; TK-279: "Blocked".</summary>
    public string? Warning => Status switch
    {
        LicenseStatus.Restricted => "Ended",
        LicenseStatus.Blocked => "Blocked",
        _ => TrialDaysLeft switch { <= 2 => "Last", <= 5 => "Soon", _ => null },
    };

    public bool IsEnabled(Feature feature) => Status switch
    {
        LicenseStatus.Trial => true, // everything on while trying it (docs/competencia-glecf.md §7)
        LicenseStatus.Active or LicenseStatus.Grace => Data!.Features.Contains(feature.ToString()),
        _ => false,
    };

    /// <summary>TK-277: cashiers in Restricted mode: the smallest plan's (Esencial). The trial, and a license that does not say, have no limit.</summary>
    public const int RestrictedCashiers = 2;

    public int LimitOf(Limit limit) => Status switch
    {
        LicenseStatus.Active or LicenseStatus.Grace when Data!.Limits.TryGetValue(limit.ToString(), out var n) => n,
        LicenseStatus.Restricted or LicenseStatus.Blocked => limit switch { Limit.Registers => RestrictedRegisters, _ => RestrictedCashiers },
        _ => limit switch { Limit.Registers => TrialRegisters, _ => int.MaxValue },
    };

    public Result<LicenseData> Verify(string jws) => Jws.Verify(jws, _keys);

    public Result<TrialGrant> VerifyTrial(string jws) => Jws.Verify<TrialGrant>(jws, Jws.TrialType, _keys);

    public Result<ServerTime> VerifyTime(string jws) => Jws.Verify<ServerTime>(jws, Jws.TimeType, _keys);

    /// <param name="newRnc">TK-368: the RNC the business moves to (support changed it); the caller saves it in its own transaction.</param>
    public Result<LicenseData> Install(string jws, out string? newRnc)
    {
        newRnc = null;
        var verified = Verify(jws);
        if (verified.IsFailure) return verified.Error!;
        var v = verified.Value;
        var business = BusinessRnc();
        // TK-368: a license for another RNC only fits when support moved this business's RNC to it (ReplacesRnc = the RNC it has now).
        var moves = v.Rnc is { } rnc && !SameRnc(rnc, business);
        if (moves && !(v.ReplacesRnc is { } old && SameRnc(old, business))) return OtherBusiness;
        if (Newest is { } newest && v.IssuedUtc < newest.IssuedUtc) return Older; // R12 (security M2): never undoes a newer one, e.g. a block
        // shortcut: if the caller's transaction then fails, the file names the new RNC and the database the old one: the license is ignored at the
        // next start until it is installed again (it still fits: it replaces the RNC the business has).
        if (moves) newRnc = Rnc.Normalize(v.Rnc);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, jws.Trim());
        WriteSealed(IssuedFile, $"{v.IssuedUtc.Ticks}|{(v.Blocked ? 1 : 0)}");
        lock (_gate) (_newest, _newestLoaded) = ((v.IssuedUtc, v.Blocked), true);
        (_data, _current, _rncChecked, _installedRnc) = (v, jws.Trim(), true, Rnc.Normalize(v.Rnc));
        Time.Observe(v.IssuedUtc);
        return v;
    }
}

/// <summary>TK-279: the time a license goes by. It never goes back: when the PC's clock is behind the latest time this store saw (beyond
/// <see cref="Tolerance"/>), time runs on from that instant with the machine's own uptime counter, so turning the clock back stretches neither a
/// license nor the trial, and <see cref="Behind"/> tells the owner "la hora de la PC está atrasada".</summary>
/// <remarks>shortcut: while the clock is behind, the hours the PC is off are not counted; the DGII's HTTP Date (TK-156) would add an outside time.</remarks>
internal sealed class LicenseClock(TimeProvider clock, DateTime? seenUtc)
{
    /// <summary>The clock may lag this much (time sync corrections) before it counts as turned back; as <c>ClockHealthCheck</c>.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private DateTime _seen = seenUtc ?? DateTime.MinValue;
    private long _stamp = clock.GetTimestamp();

    public bool Behind { get; private set; }

    /// <summary>The latest time seen (what <see cref="LicenseService.SaveTimeAsync"/> keeps).</summary>
    public DateTime SeenUtc
    {
        get
        {
            _ = UtcNow;
            lock (_gate) return _seen;
        }
    }

    public DateTime UtcNow
    {
        get
        {
            lock (_gate)
            {
                var wall = clock.GetUtcNow().UtcDateTime;
                var running = _seen == DateTime.MinValue ? wall : _seen + clock.GetElapsedTime(_stamp);
                Behind = wall < running - Tolerance;
                // Within the tolerance the PC's clock wins and small corrections never pile up; the latest instant only moves (and its uptime
                // stamp restarts) when it changes, so a clock set back a little again and again does not stop the time (R12).
                if (Behind) (_seen, _stamp) = (running, clock.GetTimestamp());
                else if (wall > _seen) (_seen, _stamp) = (wall, clock.GetTimestamp());
                return Behind ? running : wall;
            }
        }
    }

    /// <summary>The time a trusted outside source says it is now may bring the clock back down from this far ahead (R12).</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(1);

    /// <summary>A time known to have passed (a license's signed issue date). R12: the interval since the latest instant counts once.</summary>
    public void Observe(DateTime utc)
    {
        lock (_gate)
            if (_seen == DateTime.MinValue || utc > _seen + clock.GetElapsedTime(_stamp)) (_seen, _stamp) = (utc, clock.GetTimestamp());
    }

    /// <summary>R12: what a trusted outside source (the DGII's time at its answer) says it is now. Raises the clock like <see cref="Observe"/> and
    /// also brings it back down when it ran more than <see cref="MaxAhead"/> ahead (a PC that once booted with a date far in the future would
    /// otherwise stay Blocked even after paying).</summary>
    public void Correct(DateTime utc)
    {
        lock (_gate)
            if (_seen == DateTime.MinValue || utc > _seen + clock.GetElapsedTime(_stamp) || _seen + clock.GetElapsedTime(_stamp) - utc > MaxAhead)
                (_seen, _stamp) = (utc, clock.GetTimestamp());
    }

    public static string Seal(DateTime utc, byte[] key) => Convert.ToBase64String(HMACSHA256.HashData(key, BitConverter.GetBytes(utc.Ticks)));

    /// <summary>The saved time, or null when there is none or its seal does not match.</summary>
    public static DateTime? Unseal(DateTime? utc, string? seal, byte[] key) =>
        utc is { } t && seal is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(seal), Encoding.ASCII.GetBytes(Seal(t, key))) ? t : null;
}

/// <summary>TK-279: saves the license clock every 10 minutes and when the server stops (a crash loses at most that much).</summary>
internal sealed class LicenseClockKeeper(PerTenant<LicenseService> licenses, ILogger<LicenseClockKeeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await SaveAsync();
        }
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            foreach (var license in licenses.All) await license.SaveTimeAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not save the license clock");
        }
    }
}

/// <summary>TK-249: installs the license the cloud issued for a paid subscription (Sync brings it down). Only a valid one, and only when it was
/// issued after the installed one: an older copy never replaces a newer license, whoever signed it.</summary>
internal sealed class StoreLicense(LicenseService license, ICommandHandler<InstallLicense.Command, Ok> install, SettingsDbContext db) : IStoreLicense
{
    /// <summary>TK-276: a longer trial (a business that came recommended); never a shorter one, and 90 days at most. Saved with the business.
    /// R12 (security B3): only from a <see cref="TrialGrant"/> signed with a license key for this store's cloud business.</summary>
    public async Task<bool> ExtendTrialAsync(string grant, Guid tenantId, CancellationToken ct)
    {
        if (license.VerifyTrial(grant) is not { IsSuccess: true } verified || verified.Value.TenantId != tenantId) return false;
        var days = verified.Value.Days;
        if (days <= license.TrialLength.TotalDays || days > LicenseService.MaxTrialDays) return false;
        await db.Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.TrialDays, days), ct);
        license.TrialLengthChanged(days);
        return true;
    }

    public Task<string?> BusinessRncAsync(CancellationToken ct) => db.Business.AsNoTracking().Select(b => b.Rnc).SingleOrDefaultAsync(ct);

    public bool CorrectTime(string time, Guid tenantId, string nonce)
    {
        if (license.VerifyTime(time) is not { IsSuccess: true } verified
            || verified.Value.TenantId != tenantId || verified.Value.Nonce != nonce) return false;
        license.Time.Correct(DateTime.SpecifyKind(verified.Value.Utc, DateTimeKind.Utc));
        return true;
    }

    public async Task<bool> InstallFromCloudAsync(string jws, CancellationToken ct)
    {
        if (jws.Trim() == license.Current || license.Verify(jws) is not { IsSuccess: true } verified) return false;
        if (license.Data is { } installed && verified.Value.IssuedUtc <= installed.IssuedUtc) return false;
        return (await install.Handle(new InstallLicense.Command(jws), ct)).IsSuccess;
    }
}

/// <summary>TK-196: the trial's 14 days start with the store's first sale, not with the installation. Runs again harmlessly (the first one wins).
/// R9 #12: only a real sale counts: not one of the DGII certification or test environments, nor a simulated one with the sample products.</summary>
/// <remarks>TK-369: the date also has a sealed twin in the keys folder (<see cref="LicenseService.TrialFile"/>), so editing the database alone gains
/// nothing. shortcut: uninstalling with "borrar datos" deletes both and a new install starts a new trial; a cloud copy by RNC would close it.</remarks>
internal sealed class StartTrialOnFirstSale(SettingsDbContext db, LicenseService license, IFiscalDocuments fiscal, ICatalogQueries catalog)
    : IEventHandler<SaleRecordedV2>
{
    public async Task HandleAsync(SaleRecordedV2 e, CancellationToken ct)
    {
        if (license.TrialStartedUtc is not null) return;
        var issuedIn = await fiscal.IssuedInAsync(e.ENcf, ct);
        if (issuedIn == IssuedIn.Test) return;
        if (issuedIn == IssuedIn.Simulated && await catalog.AnySampleAsync([.. e.Lines.Select(l => l.ProductId)], ct)) return;
        // R12: by the license clock, like Status: a PC clock turned back before the first sale does not move the trial's start back.
        var now = license.Time.UtcNow;
        await db.Business.Where(b => b.TrialStartedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(b => b.TrialStartedUtc, now), ct);
        license.TrialStarted((await db.Business.AsNoTracking().SingleAsync(ct)).TrialStartedUtc ?? now);
    }
}

/// <summary>Installs the license the vendor sent. It is also how a trial is bought (TK-220): Abastalia staff send the signed license as a file or as
/// text to paste; it applies at once, without reinstalling, and keeps every sale. TK-249: a store connected to the cloud with a paid subscription
/// gets it by itself (<see cref="StoreLicense"/>); pasting stays for offline stores and special deals.</summary>
internal static class InstallLicense
{
    public sealed record Command(string License) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.License).NotEmpty().MaximumLength(10_000);
    }

    internal sealed class Handler(LicenseService license, IAuditLog audit, SettingsDbContext db) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var installed = license.Install(c.License, out var newRnc);
            if (installed.IsFailure) return installed.Error!;
            if (newRnc is not null) await db.Business.ExecuteUpdateAsync(s => s.SetProperty(b => b.Rnc, newRnc), ct); // TK-368: support moved the RNC
            await audit.RecordAsync("settings.license", "License", Guid.Empty,
                new { installed.Value.Plan, installed.Value.ExpiresUtc, installed.Value.Features, installed.Value.Limits, installed.Value.Rnc, installed.Value.ReplacesRnc }, ct);
            return Ok.Value;
        }
    }

    /// <param name="TrialEndsUtc">While there is no license: null until the first sale starts the 14 days.</param>
    /// <param name="Warning">Null, "Soon" (trial day 10 on), "Last" (day 13 on), "Ended" (Restricted) or "Blocked" (TK-279).</param>
    /// <param name="BlocksUtc">TK-279: when it goes Blocked by date (null before the trial starts).</param>
    /// <param name="ClockBehind">TK-279: the PC's clock is behind the latest time this store saw; the license counts from that time.</param>
    /// <param name="BlockReason">R12: while Blocked, "Expired", "Trial" or "Suspended" (Abastalia blocked the business), so the register says which.</param>
    public sealed record Features(LicenseStatus Status, string? Plan, DateTime? ExpiresUtc, IReadOnlyList<string> Enabled, int Registers,
        DateTime? TrialEndsUtc, int? TrialDaysLeft, string? Warning, DateTime? BlocksUtc, bool ClockBehind, string? BlockReason);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/settings/license", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) => (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
            // The register shows or hides menus with this; the server still checks every gated action itself.
            app.MapGet("/api/settings/features", (LicenseService license) => Results.Ok(new Features(license.Status, license.Data?.Plan, license.Data?.ExpiresUtc,
                    Enum.GetValues<Feature>().Where(license.IsEnabled).Select(f => f.ToString()).ToList(), license.LimitOf(Limit.Registers),
                    license.Data is null ? license.TrialEndsUtc : null, license.TrialDaysLeft, license.Warning, license.BlocksUtc, license.Time.Behind, license.BlockReason)))
                .RequireAuthorization().WithTags("Settings");
        }
    }
}
