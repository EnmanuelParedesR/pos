using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Sales.Contracts;
using Toki.SharedKernel;

namespace Toki.Settings;

/// <summary>The license as the vendor signed it. <see cref="Features"/> holds <see cref="Feature"/> names; <see cref="Limits"/> by <see cref="Limit"/> name.</summary>
public sealed record LicenseData(string Store, string Plan, IReadOnlyList<string> Features, IReadOnlyDictionary<string, int> Limits, DateTime IssuedUtc, DateTime ExpiresUtc);

/// <summary>Compact JWS with ES256 (ECDSA P-256, SHA-256). Only the vendor's private key signs; the store has only the public key,
/// so there is no license generator in the client (the old system shipped one).</summary>
public static class Jws
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Sign(LicenseData data, ECDsa privateKey)
    {
        var header = B64("""{"alg":"ES256","typ":"JWT"}"""u8);
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(data, Options));
        var signature = privateKey.SignData(Encoding.ASCII.GetBytes($"{header}.{body}"), HashAlgorithmName.SHA256);
        return $"{header}.{body}.{B64(signature)}";
    }

    public static Result<LicenseData> Verify(string jws, ECDsa publicKey)
    {
        var parts = jws.Trim().Split('.');
        if (parts.Length != 3) return LicenseErrors.Invalid;
        try
        {
            using var header = JsonDocument.Parse(Bytes(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "ES256") return LicenseErrors.Invalid;
            if (!publicKey.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Bytes(parts[2]), HashAlgorithmName.SHA256)) return LicenseErrors.Invalid;
            return JsonSerializer.Deserialize<LicenseData>(Bytes(parts[1]), Options) is { } data ? data : LicenseErrors.Invalid;
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return LicenseErrors.Invalid;
        }
    }

    private static string B64(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Bytes(string b64url)
    {
        var s = b64url.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

internal static class LicenseErrors
{
    public static readonly Error Invalid = Error.Validation("License.Invalid", "La licencia no es válida o no la firmó Toki.");
}

/// <summary>The installed license, read from <c>License:File</c> (C:\ProgramData\Toki\license.jws). The status follows the clock:
/// Active until it expires, then 15 days of Grace, then Restricted. Without a license the store runs as Trial for 14 days from its first
/// sale (TK-196), then Restricted (TK-220). Installing a license lifts either at once, without reinstalling and keeping every sale.</summary>
internal sealed class LicenseService : IFeatureService
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(15);
    public static readonly TimeSpan TrialPeriod = TimeSpan.FromDays(14);

    /// <summary>Registers a store without a valid license may pair (the main PC is not counted).</summary>
    public const int TrialRegisters = 2;

    /// <summary>"1 caja" once Restricted: the main PC plus one register keep selling (a store whose main PC is a back-office server still sells).</summary>
    public const int RestrictedRegisters = 1;

    /// <summary>The vendor's public key. Its private key never ships with the product.</summary>
    private const string TokiPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9b9VXQE8pkSL0IgzQKC3xqvtAw+0
        YaalpiHS8yL+saMYiVCHRb/4/izVOJx1hJ7OfWYUTaP4loWuxZ2+QU17RQ==
        -----END PUBLIC KEY-----
        """;

    private readonly TimeProvider _clock;
    private readonly IServiceScopeFactory _scopes;
    private readonly ECDsa _key = ECDsa.Create();
    private readonly Lock _gate = new();
    private bool _trialLoaded;
    private DateTime? _trialStartedUtc;

    public LicenseService(IConfiguration config, IHostEnvironment environment, TimeProvider clock, IServiceScopeFactory scopes)
    {
        (_clock, _scopes) = (clock, scopes);
        // Only test and development hosts may trust another key; a store (Production) always checks against Toki's.
        _key.ImportFromPem(!environment.IsProduction() && config["License:PublicKey"] is { Length: > 0 } other ? other : TokiPublicKey);
        FilePath = config["License:File"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "license.jws");
        if (File.Exists(FilePath) && Jws.Verify(File.ReadAllText(FilePath), _key) is { IsSuccess: true } saved) Data = saved.Value;
    }

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
                // Once per process, on its own connection. The scope only disposes asynchronously (UnitOfWork).
                var scope = _scopes.CreateAsyncScope();
                try
                {
                    _trialStartedUtc = scope.ServiceProvider.GetRequiredService<SettingsDbContext>().Business.AsNoTracking().Select(b => b.TrialStartedUtc).SingleOrDefault();
                }
                finally
                {
                    scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                _trialLoaded = true;
                return _trialStartedUtc;
            }
        }
    }

    public void TrialStarted(DateTime utc)
    {
        lock (_gate) (_trialStartedUtc, _trialLoaded) = (utc, true);
    }

    public DateTime? TrialEndsUtc => TrialStartedUtc + TrialPeriod;

    public DateTime? EndsUtc => Data?.ExpiresUtc ?? TrialEndsUtc;

    public LicenseStatus Status
    {
        get
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (Data is null) return TrialEndsUtc is { } end && now > end ? LicenseStatus.Restricted : LicenseStatus.Trial;
            return now <= Data.ExpiresUtc ? LicenseStatus.Active : now <= Data.ExpiresUtc + GracePeriod ? LicenseStatus.Grace : LicenseStatus.Restricted;
        }
    }

    /// <summary>Days left of the trial (the day in course counts), or null outside the trial or before its first sale.</summary>
    public int? TrialDaysLeft => Status == LicenseStatus.Trial && TrialEndsUtc is { } end ? (int)Math.Ceiling((end - _clock.GetUtcNow().UtcDateTime).TotalDays) : null;

    /// <summary>TK-220: "Soon" from day 10 of the trial (5 days left), "Last" from day 13 (2 left), "Ended" once Restricted.</summary>
    public string? Warning => Status == LicenseStatus.Restricted ? "Ended" : TrialDaysLeft switch { <= 2 => "Last", <= 5 => "Soon", _ => null };

    public bool IsEnabled(Feature feature) => Status switch
    {
        LicenseStatus.Trial => true, // everything on while trying it (docs/competencia-glecf.md §7)
        LicenseStatus.Active or LicenseStatus.Grace => Data!.Features.Contains(feature.ToString()),
        _ => false,
    };

    public int LimitOf(Limit limit) => Status switch
    {
        LicenseStatus.Active or LicenseStatus.Grace when Data!.Limits.TryGetValue(limit.ToString(), out var n) => n,
        LicenseStatus.Restricted => limit switch { Limit.Registers => RestrictedRegisters, _ => 0 },
        _ => limit switch { Limit.Registers => TrialRegisters, _ => 0 },
    };

    public Result<LicenseData> Install(string jws)
    {
        var verified = Jws.Verify(jws, _key);
        if (verified.IsFailure) return verified.Error!;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, jws.Trim());
        Data = verified.Value;
        return verified.Value;
    }
}

/// <summary>TK-196: the trial's 14 days start with the store's first sale, not with the installation. Runs again harmlessly (the first one wins).</summary>
/// <remarks>shortcut: the date lives in the store's own database; an owner who edits it by hand gets more days. A cloud copy (heartbeat) is the check if it matters.</remarks>
internal sealed class StartTrialOnFirstSale(SettingsDbContext db, LicenseService license) : IEventHandler<SaleRecordedV2>
{
    public async Task HandleAsync(SaleRecordedV2 e, CancellationToken ct)
    {
        if (license.TrialStartedUtc is not null) return;
        await db.Business.Where(b => b.TrialStartedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(b => b.TrialStartedUtc, e.OccurredUtc), ct);
        license.TrialStarted((await db.Business.AsNoTracking().SingleAsync(ct)).TrialStartedUtc ?? e.OccurredUtc);
    }
}

/// <summary>Installs the license the vendor sent (until Sync brings it down by itself, phase 2). It is also how a trial is bought (TK-220):
/// Toki staff send the signed license as a file or as text to paste; it applies at once, without reinstalling, and keeps every sale.</summary>
/// <remarks>shortcut: no payment in the app; the owner pays Toki by WhatsApp or transfer and gets the license back.</remarks>
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
    /// <param name="Warning">Null, "Soon" (trial day 10 on), "Last" (day 13 on) or "Ended" (Restricted).</param>
    public sealed record Features(LicenseStatus Status, string? Plan, DateTime? ExpiresUtc, IReadOnlyList<string> Enabled, int Registers,
        DateTime? TrialEndsUtc, int? TrialDaysLeft, string? Warning);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/settings/license", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) => (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
            // The register shows or hides menus with this; the server still checks every gated action itself.
            app.MapGet("/api/settings/features", (LicenseService license) => Results.Ok(new Features(license.Status, license.Data?.Plan, license.Data?.ExpiresUtc,
                    Enum.GetValues<Feature>().Where(license.IsEnabled).Select(f => f.ToString()).ToList(), license.LimitOf(Limit.Registers),
                    license.Data is null ? license.TrialEndsUtc : null, license.TrialDaysLeft, license.Warning)))
                .RequireAuthorization().WithTags("Settings");
        }
    }
}
