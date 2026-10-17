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
    private readonly List<TaliaEntry> _talia = [];

    /// <summary>TK-342: the chat with Talia, kept while the same user is signed in: another user at this PC starts with an empty one.</summary>
    public List<TaliaEntry> Talia(Guid userId)
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

/// <summary>One turn of the chat with Talia; an answer may carry its "Abrir …" screen and (TK-343) a proposal card, whose state outlives the window.</summary>
public sealed class TaliaEntry(TaliaTurn turn, TaliaAnswer? reply = null)
{
    public TaliaTurn Turn { get; } = turn;
    public TaliaAnswer? Reply { get; } = reply;
    public ProposalState Card { get; } = new();
}

/// <summary>TK-343: where a proposal card is (docs/talia-acciones.md §3): open until the person decides, then the outcome.</summary>
public sealed class ProposalState
{
    public enum Kinds { Pending, Running, Done, Failed, Closed, Cancelled, Expired, NotAllowed }

    public Kinds Kind { get; set; }

    /// <summary>The server's text for a failure, or (pending) a retryable problem: no connection, the supervisor not given.</summary>
    public string? Message { get; set; }
}
