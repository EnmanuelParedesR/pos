namespace Abastalia.Client.UI.State;

/// <summary>Where the store server is (TK-251): the main PC this register was pointed at when it joined a store (saved in
/// <see cref="ILocalStore"/>, outside the app folder that updates replace), else the one in appsettings.</summary>
public sealed class ServerAddress(Uri configured)
{
    private readonly Uri _configured = configured;

    public Uri Current { get; set; } = configured;

    /// <summary>The main PC by name or IP, on the configured scheme and port (https and 5443 once installed); null when it is not a host name or IP.</summary>
    public Uri? With(string? host)
    {
        host = host?.Trim();
        return string.IsNullOrEmpty(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown ? null : new UriBuilder(_configured) { Host = host }.Uri;
    }
}
