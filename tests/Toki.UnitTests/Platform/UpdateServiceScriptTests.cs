using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Toki.Host.Local;

namespace Toki.UnitTests.Platform;

/// <summary>R10 #1, #4: update.json is only a hint to the SYSTEM task. The service writes the feed's SHA-256 into it, and
/// deploy/installer/update-service.ps1 refuses a bad version, a setup outside the installed feed, or no way to verify it.</summary>
public sealed class UpdateServiceScriptTests : IDisposable
{
    private const string Feed = "https://releases.toki.example/stable";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _root = Directory.CreateTempSubdirectory("toki-update-").FullName;

    [Fact]
    public void The_marker_carries_the_feed_sha256_and_the_setup_url_built_from_the_feed()
    {
        var json = UpdateMarker.Create(Feed, "1.2.3", Hash + "  TokiSetup-1.2.3.exe\r\n", true, new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc)).ToJson();

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("Sha256").GetString().ShouldBe(Hash.ToUpperInvariant());
        doc.RootElement.GetProperty("SetupUrl").GetString().ShouldBe($"{Feed}/TokiSetup-1.2.3.exe");
        doc.RootElement.GetProperty("CheckedUtc").GetString().ShouldBe("2026-10-16T12:00:00Z");
        Should.Throw<InvalidOperationException>(() => UpdateMarker.Create(Feed, "1.2.3", "<html>not found</html>", true, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil", Feed + "/TokiSetup-1.2.3.exe", Hash, "trae una versi")]
    [InlineData("1.2.3", "https://evil.example/TokiSetup-1.2.3.exe", Hash, "no es " + Feed)]
    [InlineData("1.2.3", Feed + "/TokiSetup-1.2.3.exe", "", "no trae SHA-256")]
    public void The_script_refuses_a_marker_it_cannot_trust(string version, string setupUrl, string sha256, string error)
    {
        var (exit, stderr) = Run(version, setupUrl, sha256, DateTime.UtcNow);

        exit.ShouldBe(1);
        stderr.ShouldContain(error);
        Directory.Exists(Path.Combine(_root, "updates")).ShouldBeFalse(); // refused before downloading anything
    }

    [Fact]
    public void A_stale_marker_is_left_for_the_next_check_and_a_fresh_utc_one_is_not_read_as_local_time()
    {
        Run("bad", Feed, Hash, DateTime.UtcNow.AddHours(-2)).Exit.ShouldBe(0);
        Run("bad", Feed, Hash, DateTime.UtcNow).Exit.ShouldBe(1); // fresh: reaches validation in any time zone
    }

    private (int Exit, string Stderr) Run(string version, string setupUrl, string sha256, DateTime checkedUtc)
    {
        File.WriteAllText(Path.Combine(_root, "update.json"), JsonSerializer.Serialize(new UpdateMarker(version, setupUrl, sha256, true, checkedUtc)));
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), JsonSerializer.Serialize(new { Updates = new { FeedUrl = Feed + "/", SignerThumbprints = Array.Empty<string>() } }));
        var info = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Script(), "-DataRoot", _root, "-SettingsPath", Path.Combine(_root, "appsettings.json") },
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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Toki.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Toki.slnx not found"), "deploy", "installer", "update-service.ps1");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
