using System.Diagnostics;
using System.Text.Json;
using Shouldly;

namespace Toki.UnitTests.Platform;

/// <summary>TK-251: deploy/installer/store-settings.ps1 (called by build.ps1) leaves the published apps able to reach their server and the cloud.</summary>
public sealed class StoreSettingsScriptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("toki-settings-").FullName;

    [Fact]
    public void The_app_reaches_the_installed_server_and_the_server_the_production_cloud()
    {
        var app = Copy(@"src\Client\Toki.Client.Wpf\appsettings.json", "app.json");
        var server = Copy(@"src\Hosts\Toki.Host.Local\appsettings.json", "server.json");

        var info = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                $". '{Repo(@"deploy\installer\store-settings.ps1")}'; Set-StoreSettings -AppSettings '{app}' -ServerSettings '{server}' -FeedUrl 'https://feed.example' -Channel 'pilot' -SignerThumbprints 'AB12'" },
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        using (var p = Process.Start(info)!)
        {
            var stderr = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.ShouldBe(0, stderr);
        }

        using var appJson = JsonDocument.Parse(File.ReadAllText(app));
        using var serverJson = JsonDocument.Parse(File.ReadAllText(server));
        using var production = JsonDocument.Parse(File.ReadAllText(Repo(@"src\Hosts\Toki.Host.Local\appsettings.Production.json")));
        var api = new Uri(appJson.RootElement.GetProperty("Api").GetProperty("Url").GetString()!);
        var listens = new Uri(production.RootElement.GetProperty("Kestrel").GetProperty("Endpoints").GetProperty("Https").GetProperty("Url").GetString()!);
        (api.Scheme, api.Host, api.Port).ShouldBe(("https", "localhost", listens.Port)); // what the installed server listens on
        serverJson.RootElement.GetProperty("Cloud").GetProperty("Url").GetString().ShouldBe("https://app-toki-prod-jhzq7n.azurewebsites.net");
        foreach (var json in new[] { appJson, serverJson })
        {
            json.RootElement.GetProperty("Updates").GetProperty("Channel").GetString().ShouldBe("pilot");
            // R11 C3: the update task of both roles only installs setups signed by this certificate.
            json.RootElement.GetProperty("Updates").GetProperty("SignerThumbprints").EnumerateArray().Select(t => t.GetString()).ShouldBe(["AB12"]);
        }
        serverJson.RootElement.GetProperty("Database").GetProperty("Provider").GetString().ShouldBe("SqlServer"); // the rest is kept
        // R11 C1: the store's own SQL Server instance, which the setup installs (development keeps .\SQLEXPRESS in launchSettings).
        serverJson.RootElement.GetProperty("Database").GetProperty("ConnectionString").GetString().ShouldStartWith(@"Server=.\TOKI;");
    }

    private string Copy(string source, string name)
    {
        var path = Path.Combine(_root, name);
        File.Copy(Repo(source), path);
        return path;
    }

    private static string Repo(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Toki.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Toki.slnx not found"), relative);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } // best effort
    }
}
