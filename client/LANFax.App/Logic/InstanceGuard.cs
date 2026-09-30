namespace LANFax.App.Logic;

public enum InstanceRole
{
    /// <summary>This process owns the machine-wide mutex: it runs the tray, the spool watcher and the journal.</summary>
    Run,
    /// <summary>The running instance is in this session and was asked to show its "already running" balloon.</summary>
    ShowRunningHere,
    /// <summary>Another Windows session owns the spool: say so and exit.</summary>
    RunningInAnotherSession,
}

/// <summary>
/// One LANFax per machine, not per session: the spool file and the journal are machine-wide, so a second session's
/// tray would race the first for print jobs and could show one user's document on another user's desktop.
/// </summary>
public static class InstanceGuard
{
    public const string MutexName = @"Global\LANFax.Tray";
    /// <summary>Per session: lets a second start in the same session reach the running tray.</summary>
    public const string ShowEventName = @"Local\LANFax.Tray.ShowRunning";
    public const string OtherSessionText = "LANFax is already running in another session";

    /// <param name="ownsMutex">This process created the machine-wide mutex.</param>
    /// <param name="signalledLocal">The running instance's per-session event exists here and was set.</param>
    public static InstanceRole Decide(bool ownsMutex, bool signalledLocal) =>
        ownsMutex ? InstanceRole.Run
        : signalledLocal ? InstanceRole.ShowRunningHere
        : InstanceRole.RunningInAnotherSession;
}
