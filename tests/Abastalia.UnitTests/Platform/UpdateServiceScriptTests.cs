using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Abastalia.Host.Local;

namespace Abastalia.UnitTests.Platform;

/// <summary>R10 #1, #4, R11 C3, C13: update.json is only a hint to the SYSTEM task. The service writes the feed's SHA-256 into it, and
/// deploy/installer/update-service.ps1 refuses a bad version, a setup outside the installed https feed, an older version, no Abastalia signer
/// configured (unless the pilot flag allows the SHA-256) and a planted link where it downloads.</summary>
public sealed class UpdateServiceScriptTests : IDisposable
{
    private const string Feed = "https://releases.abastalia.example/stable";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private readonly string _root = Directory.CreateTempSubdirectory("abastalia-update-").FullName;

    [Fact]
    public void The_marker_carries_the_feed_sha256_and_the_setup_url_built_from_the_feed()
    {
        var json = UpdateMarker.Create(Feed, "1.2.3", Hash + "  AbastaliaSetup-1.2.3.exe\r\n", true, new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc)).ToJson();

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("Sha256").GetString().ShouldBe(Hash.ToUpperInvariant());
        doc.RootElement.GetProperty("SetupUrl").GetString().ShouldBe($"{Feed}/AbastaliaSetup-1.2.3.exe");
        doc.RootElement.GetProperty("CheckedUtc").GetString().ShouldBe("2026-10-16T12:00:00Z");
        Should.Throw<InvalidOperationException>(() => UpdateMarker.Create(Feed, "1.2.3", "<html>not found</html>", true, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil", Feed + "/AbastaliaSetup-1.2.3.exe", Hash, "trae una versi")]
    [InlineData("1.2.3", "https://evil.example/AbastaliaSetup-1.2.3.exe", Hash, "no es " + Feed)]
    [InlineData("1.2.3", Feed + "/AbastaliaSetup-1.2.3.exe", "", "no trae SHA-256")]
    public void The_script_refuses_a_marker_it_cannot_trust(string version, string setupUrl, string sha256, string error)
    {
        var (exit, stderr) = Run(version, setupUrl, sha256, DateTime.UtcNow, allowUnsigned: true);

        exit.ShouldBe(1);
        stderr.ShouldContain(error);
        Directory.Exists(Path.Combine(_root, "updates")).ShouldBeFalse(); // refused before downloading anything
    }

    [Fact]
    public void C3_without_a_Abastalia_signer_nothing_is_installed_unless_the_pilot_flag_allows_it()
    {
        var (exit, stderr) = Run("1.2.3", Feed + "/AbastaliaSetup-1.2.3.exe", Hash, DateTime.UtcNow);
        exit.ShouldBe(1);
        stderr.ShouldContain("No hay certificado de Abastalia");

        (exit, stderr) = Run("1.2.3", "http://releases.abastalia.example/stable/AbastaliaSetup-1.2.3.exe", Hash, DateTime.UtcNow, feed: "http://releases.abastalia.example/stable",
            thumbprints: [Thumbprint]);
        exit.ShouldBe(1);
        stderr.ShouldContain("https");
    }

    [Fact]
    public void C13_an_older_or_the_same_version_is_never_installed()
    {
        var (exit, stderr) = Run("1.2.3", Feed + "/AbastaliaSetup-1.2.3.exe", Hash, DateTime.UtcNow, thumbprints: [Thumbprint], installed: "1.2.3");
        exit.ShouldBe(1);
        stderr.ShouldContain("que la instalada");
    }

    [Fact]
    public void C13_a_link_planted_where_the_setup_is_downloaded_is_refused_and_what_it_points_to_is_kept()
    {
        var target = Directory.CreateTempSubdirectory("abastalia-target-").FullName;
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        Junction(Path.Combine(_root, "updates"), target);
        try
        {
            var (exit, stderr) = Run("1.2.3", Feed + "/AbastaliaSetup-1.2.3.exe", Hash, DateTime.UtcNow, thumbprints: [Thumbprint]);
            exit.ShouldBe(1);
            stderr.ShouldContain("reparse point");
            File.Exists(Path.Combine(target, "keep.txt")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(Path.Combine(_root, "updates")); // the link only
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void A_stale_marker_is_left_for_the_next_check_and_a_fresh_utc_one_is_not_read_as_local_time()
    {
        Run("bad", Feed, Hash, DateTime.UtcNow.AddHours(-2)).Exit.ShouldBe(0);
        Run("bad", Feed, Hash, DateTime.UtcNow).Exit.ShouldBe(1); // fresh: reaches validation in any time zone
    }

    [Fact]
    public void TK254_a_register_waits_when_the_cloud_says_so_and_otherwise_goes_to_the_feed()
    {
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), JsonSerializer.Serialize(new { Updates = new { FeedUrl = Feed, SignerThumbprints = new[] { Thumbprint } } }));
        var policy = Path.Combine(_root, "update-policy.json");
        (int, string) Register()
        {
            var info = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList =
                {
                    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Script(), "-Role", "register", "-DataRoot", _root,
                    "-SettingsPath", Path.Combine(_root, "appsettings.json"), "-InstalledVersion", "1.0.0", "-PolicyPath", policy,
                },
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(info)!;
            var stderr = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stderr);
        }

        File.WriteAllText(policy, """{"Channel":"pilot","MaxVersion":""}""");
        Register().Item1.ShouldBe(0); // held back: nothing downloaded
        File.WriteAllText(policy, """{"Channel":"pilot","MaxVersion":"1.2.0"}""");
        Register().Item1.ShouldBe(1); // allowed: it reads the (unreachable, in tests) feed
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.ShouldBe(0);
    }

    private (int Exit, string Stderr) Run(string version, string setupUrl, string sha256, DateTime checkedUtc, string feed = Feed,
        string[]? thumbprints = null, bool allowUnsigned = false, string installed = "1.0.0")
    {
        File.WriteAllText(Path.Combine(_root, "update.json"), JsonSerializer.Serialize(new UpdateMarker(version, setupUrl, sha256, true, checkedUtc)));
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), JsonSerializer.Serialize(new
        {
            Updates = new { FeedUrl = feed + "/", SignerThumbprints = thumbprints ?? [], AllowUnsignedUpdates = allowUnsigned },
        }));
        var info = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList =
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Script(), "-DataRoot", _root, "-SettingsPath", Path.Combine(_root, "appsettings.json"),
                "-InstalledVersion", installed,
            },
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        using var p = Process.Start(info)!;
        var stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stderr);
    }

    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Abastalia.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Abastalia.slnx not found"), "deploy", "installer", "update-service.ps1");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
