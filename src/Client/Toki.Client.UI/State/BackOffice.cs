namespace Toki.Client.UI.State;

/// <summary>The back office's remembered choices while the app is open: the collapsed menu and the business name for its header.</summary>
public sealed class BackOffice
{
    public bool MenuCollapsed { get; set; }
    public string? BusinessName { get; set; }
}
