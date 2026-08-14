namespace BarcodePrinter.Client.Core;

/// <summary>
/// Whether the API is currently reachable, as observed by the requests the
/// application is already making (§12: the status bar answers "is the system
/// up?").
///
/// Observed rather than polled: every call already tells us the answer, and a
/// heartbeat would add traffic while still being able to disagree with the call
/// the operator just made.
///
/// This exists because a HARDCODED "Connected" indicator is worse than none.
/// During an outage it is a green light next to a screen saying the server
/// cannot be reached, and an operator who believes the light keeps printing.
/// </summary>
public sealed class ConnectionStatus
{
    private bool _isOnline = true;

    /// <summary>Raised whenever the state actually changes — not on every
    /// request, or the status bar would repaint continuously.</summary>
    public event EventHandler<bool>? Changed;

    public bool IsOnline => _isOnline;

    /// <summary>Last time a request succeeded. Shown when offline so the
    /// operator knows how long it has been down.</summary>
    public DateTime? LastSuccessLocal { get; private set; }

    public void ReportSuccess()
    {
        LastSuccessLocal = DateTime.Now;
        Set(true);
    }

    public void ReportUnreachable() => Set(false);

    private void Set(bool online)
    {
        if (_isOnline == online)
        {
            return;
        }
        _isOnline = online;
        Changed?.Invoke(this, online);
    }
}
