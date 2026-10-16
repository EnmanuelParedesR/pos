using Microsoft.Playwright;

namespace Abastalia.UiTests;

/// <summary>TK-270: Playwright traces (screenshots + DOM snapshots) and failed visual shots go to bin/&lt;config&gt;/net10.0/traces,
/// which CI uploads when the job fails. Open a trace with `playwright.ps1 show-trace &lt;file&gt;.zip`.</summary>
public static class Traces
{
    public static string Folder { get; } = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "traces")).FullName;

    public static Task StartAsync(IBrowserContext context) =>
        context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = false });

    public static Task StopAsync(IBrowserContext context, string name) =>
        context.Tracing.StopAsync(new() { Path = Path.Combine(Folder, name + ".zip") });
}
