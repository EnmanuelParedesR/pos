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
/// from its first sale (TK-196), then Restricted (TK-220), then Blocked 30 days after the trial ended. Installing a license lifts any of them at
/// once, without reinstalling and keeping every sale. The clock is <see cref="LicenseClock"/>: turning the PC's time back does not stretch it.</summary>
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
    private readonly ECDsa[] _keys;
    private readonly Lock _gate = new();
    private bool _trialLoaded;
    private DateTime? _trialStartedUtc;
    private LicenseClock? _time;

    public LicenseService(IConfiguration config, IHostEnvironment environment, TimeProvider clock, IServiceScopeFactory scopes, ServiceKeys secrets)
    {
        (_clock, _scopes, _secrets) = (clock, scopes, secrets);
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
        if (File.Exists(FilePath) && Jws.Verify(File.ReadAllText(FilePath), _keys) is { IsSuccess: true } saved) (Data, Current) = (saved.Value, File.ReadAllText(FilePath).Trim());
    }

    /// <summary>The installed license as signed (JWS), or null.</summary>
    public string? Current { get; private set; }

    /// <summary>R11 B-Low8: the heartbeat sends it so the cloud checks it itself.</summary>
    string? IFeatureService.LicenseJws => Current;

    public string FilePath { get; }
    public LicenseData? Data { get; private set; }

    /// <summary>The store's first sale (settings.Business), read once and then kept here; <see cref="StartTrialOnFirstSale"/> sets it.</summary>
    public DateTime? TrialStartedUtc
    {
        get
        {
            lock (_gate)
            {
                if (_trialLoaded) return _trialStartedUtc;
                _trialStartedUtc = Read(sp => sp.GetRequiredService<SettingsDbContext>().Business.AsNoTracking().Select(b => b.TrialStartedUtc).SingleOrDefault());
                _trialLoaded = true;
                return _trialStartedUtc;
            }
        }
    }

    /// <summary>TK-279: loaded once from the latest time this store saw: the sealed copy in settings.Business (ignored when its seal does not
    /// match, e.g. edited by hand), the newest audit record (hash-chained) and the installed license's issue date (signed).</summary>
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
                    return new[] { LicenseClock.Unseal(saved?.ClockSeenUtc, saved?.ClockSeenSeal, ClockKey), audited, Data?.IssuedUtc }.Max();
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
        await using var scope = _scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SettingsDbContext>().Business
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.ClockSeenUtc, seen).SetProperty(b => b.ClockSeenSeal, seal), ct);
    }

    /// <summary>A secret of this server kept outside the database (R11 A3), like the PIN pepper.</summary>
    private byte[] ClockKey => _secrets.GetOrCreate("license-clock");

    /// <summary>On its own connection. The scope only disposes asynchronously (UnitOfWork).</summary>
    private T Read<T>(Func<IServiceProvider, T> read)
    {
        var scope = _scopes.CreateAsyncScope();
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
        lock (_gate) (_trialStartedUtc, _trialLoaded) = (utc, true);
    }

    public DateTime? TrialEndsUtc => TrialStartedUtc + TrialPeriod;

    public DateTime? EndsUtc => Data?.ExpiresUtc ?? TrialEndsUtc;

    /// <summary>TK-279: when the store goes Blocked by date: 30 days (or what the license says) after it expired or the trial ended.</summary>
    public DateTime? BlocksUtc => Data is { } d ? d.ExpiresUtc.AddDays(d.BlockAfterDays ?? DefaultBlockAfterDays) : TrialEndsUtc?.AddDays(DefaultBlockAfterDays);

    public LicenseStatus Status
    {
        get
        {
            var now = Time.UtcNow;
            if (Data is { Blocked: true } || now > BlocksUtc) return LicenseStatus.Blocked;
            if (Data is null) return TrialEndsUtc is { } end && now > end ? LicenseStatus.Restricted : LicenseStatus.Trial;
            return now <= Data.ExpiresUtc ? LicenseStatus.Active : now <= Data.ExpiresUtc + GracePeriod ? LicenseStatus.Grace : LicenseStatus.Restricted;
        }
    }

    /// <summary>Days left of the trial (the day in course counts), or null outside the trial or before its first sale.</summary>
    public int? TrialDaysLeft => Status == LicenseStatus.Trial && TrialEndsUtc is { } end ? (int)Math.Ceiling((end - Time.UtcNow).TotalDays) : null;

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

    public int LimitOf(Limit limit) => Status switch
    {
        LicenseStatus.Active or LicenseStatus.Grace when Data!.Limits.TryGetValue(limit.ToString(), out var n) => n,
        LicenseStatus.Restricted or LicenseStatus.Blocked => limit switch { Limit.Registers => RestrictedRegisters, _ => 0 },
        _ => limit switch { Limit.Registers => TrialRegisters, _ => 0 },
    };

    public Result<LicenseData> Verify(string jws) => Jws.Verify(jws, _keys);

    public Result<LicenseData> Install(string jws)
    {
        var verified = Verify(jws);
        if (verified.IsFailure) return verified.Error!;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, jws.Trim());
        (Data, Current) = (verified.Value, jws.Trim());
        Time.Observe(verified.Value.IssuedUtc);
        return verified.Value;
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
                // Within the tolerance the PC's clock wins and the latest instant stays put, so small corrections never pile up.
                (_seen, _stamp) = (Behind ? running : wall > _seen ? wall : _seen, clock.GetTimestamp());
                return Behind ? running : wall;
            }
        }
    }

    /// <summary>A time known to have passed (a license's signed issue date).</summary>
    public void Observe(DateTime utc)
    {
        lock (_gate)
            if (utc > _seen) _seen = utc;
    }

    public static string Seal(DateTime utc, byte[] key) => Convert.ToBase64String(HMACSHA256.HashData(key, BitConverter.GetBytes(utc.Ticks)));

    /// <summary>The saved time, or null when there is none or its seal does not match.</summary>
    public static DateTime? Unseal(DateTime? utc, string? seal, byte[] key) =>
        utc is { } t && seal is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(seal), Encoding.ASCII.GetBytes(Seal(t, key))) ? t : null;
}

/// <summary>TK-279: saves the license clock every 10 minutes and when the server stops (a crash loses at most that much).</summary>
internal sealed class LicenseClockKeeper(LicenseService license, ILogger<LicenseClockKeeper> log) : BackgroundService
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
            await license.SaveTimeAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not save the license clock");
        }
    }
}

/// <summary>TK-249: installs the license the cloud issued for a paid subscription (Sync brings it down). Only a valid one, and only when it was
/// issued after the installed one: an older copy never replaces a newer license, whoever signed it.</summary>
internal sealed class StoreLicense(LicenseService license, ICommandHandler<InstallLicense.Command, Ok> install) : IStoreLicense
{
    public async Task<bool> InstallFromCloudAsync(string jws, CancellationToken ct)
    {
        if (jws.Trim() == license.Current || license.Verify(jws) is not { IsSuccess: true } verified) return false;
        if (license.Data is { } installed && verified.Value.IssuedUtc <= installed.IssuedUtc) return false;
        return (await install.Handle(new InstallLicense.Command(jws), ct)).IsSuccess;
    }
}

/// <summary>TK-196: the trial's 14 days start with the store's first sale, not with the installation. Runs again harmlessly (the first one wins).
/// R9 #12: only a real sale counts: not one of the DGII certification or test environments, nor a simulated one with the sample products.</summary>
/// <remarks>shortcut: the date lives in the store's own database; an owner who edits it by hand gets more days. A cloud copy (heartbeat) is the check if it matters.</remarks>
internal sealed class StartTrialOnFirstSale(SettingsDbContext db, LicenseService license, IFiscalDocuments fiscal, ICatalogQueries catalog)
    : IEventHandler<SaleRecordedV2>
{
    public async Task HandleAsync(SaleRecordedV2 e, CancellationToken ct)
    {
        if (license.TrialStartedUtc is not null) return;
        var issuedIn = await fiscal.IssuedInAsync(e.ENcf, ct);
        if (issuedIn == IssuedIn.Test) return;
        if (issuedIn == IssuedIn.Simulated && await catalog.AnySampleAsync([.. e.Lines.Select(l => l.ProductId)], ct)) return;
        await db.Business.Where(b => b.TrialStartedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(b => b.TrialStartedUtc, e.OccurredUtc), ct);
        license.TrialStarted((await db.Business.AsNoTracking().SingleAsync(ct)).TrialStartedUtc ?? e.OccurredUtc);
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

    internal sealed class Handler(LicenseService license, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var installed = license.Install(c.License);
            if (installed.IsFailure) return installed.Error!;
            await audit.RecordAsync("settings.license", "License", Guid.Empty,
                new { installed.Value.Plan, installed.Value.ExpiresUtc, installed.Value.Features, installed.Value.Limits }, ct);
            return Ok.Value;
        }
    }

    /// <param name="TrialEndsUtc">While there is no license: null until the first sale starts the 14 days.</param>
    /// <param name="Warning">Null, "Soon" (trial day 10 on), "Last" (day 13 on), "Ended" (Restricted) or "Blocked" (TK-279).</param>
    /// <param name="BlocksUtc">TK-279: when it goes Blocked by date (null before the trial starts).</param>
    /// <param name="ClockBehind">TK-279: the PC's clock is behind the latest time this store saw; the license counts from that time.</param>
    public sealed record Features(LicenseStatus Status, string? Plan, DateTime? ExpiresUtc, IReadOnlyList<string> Enabled, int Registers,
        DateTime? TrialEndsUtc, int? TrialDaysLeft, string? Warning, DateTime? BlocksUtc, bool ClockBehind);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/settings/license", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) => (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
            // The register shows or hides menus with this; the server still checks every gated action itself.
            app.MapGet("/api/settings/features", (LicenseService license) => Results.Ok(new Features(license.Status, license.Data?.Plan, license.Data?.ExpiresUtc,
                    Enum.GetValues<Feature>().Where(license.IsEnabled).Select(f => f.ToString()).ToList(), license.LimitOf(Limit.Registers),
                    license.Data is null ? license.TrialEndsUtc : null, license.TrialDaysLeft, license.Warning, license.BlocksUtc, license.Time.Behind)))
                .RequireAuthorization().WithTags("Settings");
        }
    }
}
