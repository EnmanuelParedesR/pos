using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Abastalia.UiTests;

/// <summary>A process of this repo that serves HTTP on a free port of 127.0.0.1: a host built next to these tests (`dotnet X.dll`),
/// or the panel's `vite preview`. Its output is kept for the failure message.</summary>
public sealed class WebProcess : IAsyncDisposable
{
    private readonly StringBuilder _output = new();
    private readonly Process _process;

    public Uri Base { get; }

    private WebProcess(Process process, Uri @base) => (_process, Base) = (process, @base);

    /// <summary>A host project of src/ (Abastalia.Client.Web, Abastalia.Host.Cloud), built with the same configuration as these tests.</summary>
    public static Task<WebProcess> HostAsync(string folder, string project, Dictionary<string, string> environment, string readyPath = "/")
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var dll = Path.Combine(RepoRoot, "src", folder, project, "bin", configuration, "net10.0", project + ".dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("Build tests/Abastalia.UiTests before running it.", dll);
        var port = FreePort();
        environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        return StartAsync(new ProcessStartInfo("dotnet", $"\"{dll}\"") { WorkingDirectory = Path.GetDirectoryName(dll)! }, environment, port, readyPath);
    }

    /// <summary>Node serving from src/Cloud/panel (vite preview) on <paramref name="port"/>, which the arguments must name.</summary>
    public static Task<WebProcess> NodeAsync(string script, string arguments, int port) =>
        StartAsync(new ProcessStartInfo("node", $"\"{Path.Combine(Panel, script)}\" {arguments}") { WorkingDirectory = Panel }, [], port, "/");

    /// <summary>Runs a short Node command in src/Cloud/panel to the end (a vite build).</summary>
    public static async Task RunNodeAsync(string script, string arguments, Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo("node", $"\"{Path.Combine(Panel, script)}\" {arguments}")
        { WorkingDirectory = Panel, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var (k, v) in environment) info.Environment[k] = v;
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"node {script} {arguments} failed:\n{await output}\n{await errors}");
    }

    public static string RepoRoot { get; } = FindRepoRoot();
    private static string Panel => Path.Combine(RepoRoot, "src", "Cloud", "panel");

    private static async Task<WebProcess> StartAsync(ProcessStartInfo info, Dictionary<string, string> environment, int port, string readyPath)
    {
        (info.RedirectStandardOutput, info.RedirectStandardError, info.UseShellExecute) = (true, true, false);
        foreach (var (k, v) in environment) info.Environment[k] = v;
        var web = new WebProcess(Process.Start(info)!, new Uri($"http://127.0.0.1:{port}"));
        web._process.OutputDataReceived += (_, e) => { lock (web._output) web._output.AppendLine(e.Data); };
        web._process.ErrorDataReceived += (_, e) => { lock (web._output) web._output.AppendLine(e.Data); };
        web._process.BeginOutputReadLine();
        web._process.BeginErrorReadLine();

        using var http = new HttpClient { BaseAddress = web.Base, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (web._process.HasExited) throw new InvalidOperationException($"{info.Arguments} exited while starting:\n{web.Output()}");
            try
            {
                if ((await http.GetAsync(readyPath)).StatusCode == HttpStatusCode.OK) return web;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250); // polling a process that is starting: there is nothing to await instead
        }
        await web.DisposeAsync();
        throw new TimeoutException($"{info.Arguments} did not answer {readyPath} within 90 s:\n{web.Output()}");
    }

    public string Output()
    {
        lock (_output) return _output.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose();
    }

    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Abastalia.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Abastalia.slnx not found");
    }
}
