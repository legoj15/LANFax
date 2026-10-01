namespace LANFax.App.Logic;

public enum InstanceRole
{
    /// <summary>This process owns the machine-wide mutex: it runs the tray, the spool watcher and the journal.</summary>
    Run,
    /// <summary>The running instance is in this session and was asked to show its monitor window.</summary>
    ShowRunningHere,
    /// <summary>Another Windows session owns the spool: say so and exit.</summary>
    RunningInAnotherSession,
}

/// <summary>What the running tray does when a second start in its session signals it.</summary>
public enum ShowSignalAction
{
    /// <summary>Show (or raise) the monitor window.</summary>
    ShowWindow,
    /// <summary>Exit is waiting for a send in flight: no window, but a balloon so the second start is not met with nothing.</summary>
    ExitPendingBalloon,
    /// <summary>Already shutting down: nothing.</summary>
    Ignore,
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
    /// <summary>The balloon a second start gets while the tray waits for a send in flight before exiting.</summary>
    public const string ExitPendingText = "LANFax is exiting, waiting for a fax to finish";
    /// <summary>How long a second start waits for the owner's signal to appear before blaming another session.</summary>
    public static readonly TimeSpan SignalPatience = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SignalRetry = TimeSpan.FromMilliseconds(100);

    /// <param name="ownsMutex">This process created the machine-wide mutex.</param>
    /// <param name="signalledLocal">The running instance's per-session event exists here and was set.</param>
    public static InstanceRole Decide(bool ownsMutex, bool signalledLocal) =>
        ownsMutex ? InstanceRole.Run
        : signalledLocal ? InstanceRole.ShowRunningHere
        : InstanceRole.RunningInAnotherSession;

    /// <param name="exiting">The tray is stopping (past any wait).</param>
    /// <param name="exitPending">Exit is waiting for a send in flight (up to 30 s).</param>
    public static ShowSignalAction OnShowSignal(bool exiting, bool exitPending) =>
        exiting ? ShowSignalAction.Ignore
        : exitPending ? ShowSignalAction.ExitPendingBalloon
        : ShowSignalAction.ShowWindow;

    /// <summary>
    /// Takes the machine-wide mutex and, the moment it is owned, creates the per-session show signal, so a second start
    /// in this session finds it. A non-owner signals the owner, retrying for <paramref name="patience"/> to cover the
    /// instant between the owner's mutex and its signal, and takes the mutex itself if the owner releases it meanwhile
    /// (a start during the owner's shutdown); only then is it "another session". Another user's mutex with a DACL this
    /// user may not open is "not first" too, never a crash.
    /// </summary>
    public static InstanceClaim Acquire(string mutexName, string showEventName, TimeSpan patience)
    {
        Mutex? mutex;
        bool first;
        try { mutex = new Mutex(true, mutexName, out first); }
        catch (UnauthorizedAccessException) { mutex = null; first = false; }
        if (first) return Owner(mutex, showEventName);
        bool TryOwn()
        {
            try
            {
                mutex ??= new Mutex(false, mutexName);
                try { return mutex.WaitOne(0); }
                catch (AbandonedMutexException) { return true; } // the owner went without releasing it: ours now
            }
            catch (UnauthorizedAccessException) { return false; } // another user's mutex: not ours to take
        }
        var role = Resolve(TryOwn, () => TrySignal(showEventName), patience, Thread.Sleep);
        if (role == InstanceRole.Run) return Owner(mutex, showEventName);
        mutex?.Dispose();
        return new InstanceClaim(role, null, null);
    }

    private static InstanceClaim Owner(Mutex? mutex, string showEventName) =>
        new(InstanceRole.Run, mutex, new EventWaitHandle(false, EventResetMode.AutoReset, showEventName));

    /// <summary><see cref="Decide"/>, giving the owner's signal up to <paramref name="patience"/> to appear.</summary>
    public static InstanceRole Resolve(bool ownsMutex, Func<bool> trySignal, TimeSpan patience, Action<TimeSpan> sleep) =>
        ownsMutex ? InstanceRole.Run : Resolve(() => false, trySignal, patience, sleep);

    /// <summary>
    /// A start that did not get the mutex: within <paramref name="patience"/> it either signals the owner in this
    /// session or, when the owner is shutting down and releases the mutex, takes it and becomes the owner. Only a
    /// mutex still held elsewhere with no signal here at the end is "another session".
    /// </summary>
    public static InstanceRole Resolve(Func<bool> tryOwn, Func<bool> trySignal, TimeSpan patience, Action<TimeSpan> sleep)
    {
        for (var waited = TimeSpan.Zero; ; waited += SignalRetry)
        {
            if (trySignal()) return InstanceRole.ShowRunningHere;
            if (tryOwn()) return InstanceRole.Run;
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
