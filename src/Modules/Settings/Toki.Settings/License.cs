using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
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
/// Active until it expires, then 15 days of Grace, then Restricted. Without a license the store runs as Trial.</summary>
internal sealed class LicenseService : IFeatureService
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(15);

    /// <summary>Registers a store without a valid license may pair (the main PC is not counted).</summary>
    public const int TrialRegisters = 2;

    /// <summary>The vendor's public key. Its private key never ships with the product.</summary>
    private const string TokiPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE9b9VXQE8pkSL0IgzQKC3xqvtAw+0
        YaalpiHS8yL+saMYiVCHRb/4/izVOJx1hJ7OfWYUTaP4loWuxZ2+QU17RQ==
        -----END PUBLIC KEY-----
        """;

    private readonly TimeProvider _clock;
    private readonly ECDsa _key = ECDsa.Create();

    public LicenseService(IConfiguration config, IHostEnvironment environment, TimeProvider clock)
    {
        _clock = clock;
        // Only test and development hosts may trust another key; a store (Production) always checks against Toki's.
        _key.ImportFromPem(!environment.IsProduction() && config["License:PublicKey"] is { Length: > 0 } other ? other : TokiPublicKey);
        FilePath = config["License:File"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "license.jws");
        if (File.Exists(FilePath) && Jws.Verify(File.ReadAllText(FilePath), _key) is { IsSuccess: true } saved) Data = saved.Value;
    }

    public string FilePath { get; }
    public LicenseData? Data { get; private set; }

    public LicenseStatus Status
    {
        get
        {
            if (Data is null) return LicenseStatus.Trial;
            var now = _clock.GetUtcNow().UtcDateTime;
            return now <= Data.ExpiresUtc ? LicenseStatus.Active : now <= Data.ExpiresUtc + GracePeriod ? LicenseStatus.Grace : LicenseStatus.Restricted;
        }
    }

    private bool IsCurrent => Status is LicenseStatus.Active or LicenseStatus.Grace;

    public bool IsEnabled(Feature feature) => IsCurrent && Data!.Features.Contains(feature.ToString());

    public int LimitOf(Limit limit) => IsCurrent && Data!.Limits.TryGetValue(limit.ToString(), out var n) ? n : limit switch { Limit.Registers => TrialRegisters, _ => 0 };

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

/// <summary>Installs the license the vendor sent (until Sync brings it down by itself, phase 2).</summary>
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

    public sealed record Features(LicenseStatus Status, string? Plan, DateTime? ExpiresUtc, IReadOnlyList<string> Enabled, int Registers);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/settings/license", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) => (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
            // The register shows or hides menus with this; the server still checks every gated action itself.
            app.MapGet("/api/settings/features", (LicenseService license) => Results.Ok(new Features(license.Status, license.Data?.Plan, license.Data?.ExpiresUtc,
                    Enum.GetValues<Feature>().Where(license.IsEnabled).Select(f => f.ToString()).ToList(), license.LimitOf(Limit.Registers))))
                .RequireAuthorization().WithTags("Settings");
        }
    }
}
