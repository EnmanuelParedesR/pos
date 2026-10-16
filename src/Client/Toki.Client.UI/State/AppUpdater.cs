namespace Toki.Client.UI.State;

/// <summary>TK-254: the register PC's updates. The setup's SYSTEM task "Toki\Actualizar" (update-service.ps1 -Role register) installs a signed
/// version while the app is closed; this only tells it the store's channel and the cloud's cap, and closes the app when the owner asks.
/// The web host has none: nothing is reported.</summary>
public interface IAppUpdater
{
    /// <summary>The version running, "1.4.0".</summary>
    string Version { get; }
    /// <summary>Leaves the channel and the highest version allowed (null = no cap, "" = wait) where the update task reads them.</summary>
    void Follow(string channel, string? maxVersion);
    /// <summary>Closes the register so the update task can install the new version (it runs every hour); then Toki is opened again.</summary>
    void CloseToUpdate();
}
