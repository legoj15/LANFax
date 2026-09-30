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

/// <summary>What <see cref="InstanceGuard.Acquire"/> decided, holding the mutex and signal the owner keeps for its lifetime.</summary>
public sealed class InstanceClaim(InstanceRole role, Mutex? mutex, EventWaitHandle? showSignal) : IDisposable
{
    public InstanceRole Role { get; } = role;
    /// <summary>The owner's per-session "show yourself" event (null unless <see cref="Role"/> is Run).</summary>
    public EventWaitHandle? ShowSignal { get; } = showSignal;

    public void Dispose()
    {
        ShowSignal?.Dispose();
        mutex?.Dispose();
    }
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
    /// <summary>How long a second start waits for the owner's signal to appear before blaming another session.</summary>
    public static readonly TimeSpan SignalPatience = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SignalRetry = TimeSpan.FromMilliseconds(100);

    /// <param name="ownsMutex">This process created the machine-wide mutex.</param>
    /// <param name="signalledLocal">The running instance's per-session event exists here and was set.</param>
    public static InstanceRole Decide(bool ownsMutex, bool signalledLocal) =>
        ownsMutex ? InstanceRole.Run
        : signalledLocal ? InstanceRole.ShowRunningHere
        : InstanceRole.RunningInAnotherSession;

    /// <summary>
    /// Takes the machine-wide mutex and, the moment it is owned, creates the per-session show signal, so a second start
    /// in this session finds it. A non-owner signals the owner, retrying for <paramref name="patience"/> to cover the
    /// instant between the owner's mutex and its signal; only then is it "another session". Another user's mutex with
    /// a DACL this user may not open is "not first" too, never a crash.
    /// </summary>
    public static InstanceClaim Acquire(string mutexName, string showEventName, TimeSpan patience)
    {
        Mutex? mutex;
        bool first;
        try { mutex = new Mutex(true, mutexName, out first); }
        catch (UnauthorizedAccessException) { mutex = null; first = false; }
        if (first) return new InstanceClaim(InstanceRole.Run, mutex, new EventWaitHandle(false, EventResetMode.AutoReset, showEventName));
        mutex?.Dispose();
        return new InstanceClaim(Resolve(false, () => TrySignal(showEventName), patience, Thread.Sleep), null, null);
    }

    /// <summary><see cref="Decide"/>, giving the owner's signal up to <paramref name="patience"/> to appear.</summary>
    public static InstanceRole Resolve(bool ownsMutex, Func<bool> trySignal, TimeSpan patience, Action<TimeSpan> sleep)
    {
        if (ownsMutex) return InstanceRole.Run;
        for (var waited = TimeSpan.Zero; ; waited += SignalRetry)
        {
            if (trySignal()) return InstanceRole.ShowRunningHere;
            if (waited >= patience) return InstanceRole.RunningInAnotherSession;
            sleep(SignalRetry);
        }
    }

    /// <summary>Sets the running tray's show event in this session; false when there is none (or it cannot be opened).</summary>
    public static bool TrySignal(string showEventName)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(showEventName);
            return ev.Set();
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) { return false; }
    }
}
