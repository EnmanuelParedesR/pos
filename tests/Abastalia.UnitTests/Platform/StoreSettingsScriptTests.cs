using System.Diagnostics;
using System.Text.Json;
using Shouldly;

namespace Abastalia.UnitTests.Platform;

/// <summary>TK-251: deploy/installer/store-settings.ps1 (called by build.ps1) leaves the published apps able to reach their server and the cloud.</summary>
public sealed class StoreSettingsScriptTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("abastalia-settings-").FullName;

    [Fact, Trait("Category", "Windows")]
    public void The_app_reaches_the_installed_server_and_the_server_the_production_cloud()
    {
        var (app, server) = Run("-SignerThumbprints 'AB12'");

        using var appJson = JsonDocument.Parse(File.ReadAllText(app));
        using var serverJson = JsonDocument.Parse(File.ReadAllText(server));
        using var production = JsonDocument.Parse(File.ReadAllText(Repo(@"src\Hosts\Abastalia.Host.Local\appsettings.Production.json")));
        var api = new Uri(appJson.RootElement.GetProperty("Api").GetProperty("Url").GetString()!);
        var listens = new Uri(production.RootElement.GetProperty("Kestrel").GetProperty("Endpoints").GetProperty("Https").GetProperty("Url").GetString()!);
        (api.Scheme, api.Host, api.Port).ShouldBe(("https", "localhost", listens.Port)); // what the installed server listens on
        serverJson.RootElement.GetProperty("Cloud").GetProperty("Url").GetString().ShouldBe("https://app-abastalia-prod-5qsy5n.azurewebsites.net");
        foreach (var json in new[] { appJson, serverJson })
        {
            json.RootElement.GetProperty("Updates").GetProperty("Channel").GetString().ShouldBe("pilot");
            // R11 C3: the update task of both roles only installs setups signed by this certificate.
            json.RootElement.GetProperty("Updates").GetProperty("SignerThumbprints").EnumerateArray().Select(t => t.GetString()).ShouldBe(["AB12"]);
            json.RootElement.GetProperty("Updates").GetProperty("AllowUnsignedUpdates").GetBoolean().ShouldBeFalse();
        }
        serverJson.RootElement.GetProperty("Database").GetProperty("Provider").GetString().ShouldBe("SqlServer"); // the rest is kept
        // R11 C1: the store's own SQL Server instance, which the setup installs (development keeps .\SQLEXPRESS in launchSettings).
        serverJson.RootElement.GetProperty("Database").GetProperty("ConnectionString").GetString().ShouldStartWith(@"Server=.\ABASTALIA;");
    }

    [Fact, Trait("Category", "Windows")]
    public void A_pilot_build_without_certificate_keeps_accepting_unsigned_updates()
    {
        // TK-292: every update replaces the installed appsettings, so the pilot setting has to come with the build (build.ps1 -AllowUnsignedUpdates).
        var (app, server) = Run("-AllowUnsignedUpdates");
        foreach (var path in new[] { app, server })
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            json.RootElement.GetProperty("Updates").GetProperty("AllowUnsignedUpdates").GetBoolean().ShouldBeTrue();
            json.RootElement.GetProperty("Updates").GetProperty("SignerThumbprints").GetArrayLength().ShouldBe(0);
        }
    }

    [Fact, Trait("Category", "Windows")]
    public void R12_13_the_build_refuses_unsigned_updates_outside_the_pilot_channel()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only: runs build.ps1 with pwsh.");
        // Refused before anything is published: the check is at the top of build.ps1.
        var info = new ProcessStartInfo("pwsh")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Repo(@"deploy\installer\build.ps1"), "-Version", "1.0.0", "-Channel", "stable", "-AllowUnsignedUpdates" },
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        info.Environment.Remove("ABASTALIA_SIGN_THUMBPRINT");
        using var p = Process.Start(info)!;
        var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.ShouldNotBe(0);
        output.ShouldContain("-AllowUnsignedUpdates solo va con -Channel pilot.");
    }

    private (string App, string Server) Run(string arguments)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only: runs store-settings.ps1 with powershell.exe (the Windows CI job runs it).");
        var app = Copy(@"src\Client\Abastalia.Client.Wpf\appsettings.json", "app.json");
        var server = Copy(@"src\Hosts\Abastalia.Host.Local\appsettings.json", "server.json");
        var info = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                $". '{Repo(@"deploy\installer\store-settings.ps1")}'; Set-StoreSettings -AppSettings '{app}' -ServerSettings '{server}' -FeedUrl 'https://feed.example' -Channel 'pilot' {arguments}" },
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        using var p = Process.Start(info)!;
        var stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.ShouldBe(0, stderr);
        return (app, server);
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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Abastalia.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Abastalia.slnx not found"), relative.Replace('\\', Path.DirectorySeparatorChar)); // the CI runs these on Linux too
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } // best effort
    }
}
