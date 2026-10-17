using Abastalia.Client.UI.Api;

namespace Abastalia.Client.UI.State;

/// <summary>The back office's remembered choices while the app is open: the collapsed menu and the business name for its header.</summary>
public sealed class BackOffice
{
    public bool MenuCollapsed { get; set; }
    public string? BusinessName { get; set; }

    /// <summary>TK-342: Talia's window is open (it closes when the person opens a screen from it).</summary>
    public bool TaliaOpen { get; set; }

    private Guid _taliaUser;
    private readonly List<(TaliaTurn Turn, string? Screen)> _talia = [];

    /// <summary>TK-342: the chat with Talia, kept while the same user is signed in: another user at this PC starts with an empty one.</summary>
    public List<(TaliaTurn Turn, string? Screen)> Talia(Guid userId)
    {
        if (_taliaUser != userId)
        {
            _taliaUser = userId;
            _talia.Clear();
            TaliaOpen = false;
        }
        return _talia;
    }
}
