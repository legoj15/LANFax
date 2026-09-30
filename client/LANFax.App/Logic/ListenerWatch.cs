namespace LANFax.App.Logic;

public enum ListenerAction { None, Restart, RestartAndAnnounce }

/// <summary>
/// The tray's periodic listener check: a print pipe listener that is no longer running (it stopped on an unexpected
/// fault, which it reported once) is started again. The first restart of a session gets a balloon, later ones are
/// only logged, so a listener that keeps failing does not balloon every 30 s. A listener retrying a taken name is
/// still running and is left alone (it reports that itself). Once the tray stopped it on purpose (session end, Exit)
/// it is never restarted.
/// </summary>
public sealed class ListenerWatch
{
    private bool _announced;
    private bool _stopped;

    /// <summary>The tray stopped the listener deliberately: from now on <see cref="Observe"/> never restarts it.</summary>
    public void StoppedOnPurpose() => _stopped = true;

    public ListenerAction Observe(bool running)
    {
        if (running || _stopped) return ListenerAction.None;
        if (_announced) return ListenerAction.Restart;
        _announced = true;
        return ListenerAction.RestartAndAnnounce;
    }
}
