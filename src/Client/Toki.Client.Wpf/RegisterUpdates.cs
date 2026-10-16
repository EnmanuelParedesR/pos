using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Toki.Client.UI.State;

namespace Toki.Client.Wpf;

/// <summary>TK-254 on top of R11 C6: the SYSTEM task "Toki\Actualizar" (update-service.ps1 -Role register) installs a signed version while Toki
/// is closed. The app leaves it the store's channel and the cloud's cap in C:\ProgramData\Toki\update-policy.json; the task only uses them to
/// pick among Toki-signed releases of the installed feed or to wait, never to change where it downloads or how it verifies.</summary>
internal sealed class RegisterUpdates(string log) : IAppUpdater
{
    public static string PolicyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "update-policy.json");

    public string Version =>
        (typeof(RegisterUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];

    public void Follow(string channel, string? maxVersion)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
            File.WriteAllText(PolicyPath, JsonSerializer.Serialize(new { Channel = channel, MaxVersion = maxVersion }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) // the task then follows appsettings, as before
        {
            try { File.AppendAllText(log, $"{DateTime.UtcNow:O} Actualización: {e.Message}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }

    public void CloseToUpdate() => Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
}
