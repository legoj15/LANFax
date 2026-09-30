using System.ComponentModel;
using System.Runtime.InteropServices;
using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>
/// Owns the one <see cref="PrintLossMonitor"/> of the session and can start it again: a printer missing at start
/// (installed later), re-created or fixed, or a spooler restart that killed the job notifications must not leave the
/// loss check off until the app restarts. The watcher's claims go to whichever monitor is current.
/// </summary>
/// <param name="create">A new, not yet started monitor (its Lost handler attached).</param>
/// <param name="checkEvery">Passed to <see cref="PrintLossMonitor.Start"/>; null in tests (they call Check).</param>
/// <param name="unpaired">A claim made while no monitor runs (the kept-job sweep counts it).</param>
public sealed class LossCheckHost(Func<PrintLossMonitor> create, TimeSpan? checkEvery, Action<string> log, Action<DateTime>? unpaired = null) : IDisposable
{
    private readonly object _gate = new();
    private PrintLossMonitor? _current;
    private string? _lastError;

    /// <summary>The running monitor, if any.</summary>
    public PrintLossMonitor? Current => Volatile.Read(ref _current);

    public bool Running => Current is { IsRunning: true };

    /// <summary>Starts a monitor unless one is running.</summary>
    public void EnsureRunning()
    {
        if (!Running) Restart();
    }

    /// <summary>Stops the current monitor (if any) and starts a new one. A failure is logged once until it changes.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            var old = Interlocked.Exchange(ref _current, null);
            old?.Dispose();
            var m = create();
            try
            {
                m.Start(checkEvery);
            }
            catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
            {
                m.Dispose();
                if (e.Message != _lastError) log($"loss check unavailable: {e.Message}");
                _lastError = e.Message;
                return;
            }
            log(_lastError != null || old != null ? "loss check running again" : "loss check running");
            _lastError = null;
            Volatile.Write(ref _current, m);
        }
    }

    /// <summary>The watcher took a file off the port. Never blocks (it runs inside the watcher's claim loop).</summary>
    public void FileClaimed()
    {
        if (Current is { } m) m.FileClaimed();
        else unpaired?.Invoke(DateTime.UtcNow);
    }

    public void Dispose()
    {
        lock (_gate) Interlocked.Exchange(ref _current, null)?.Dispose();
    }
}

/// <summary>
/// Holds the start-up hand-over: the queue stays paused until a print.pdf left from the last run is off the port,
/// then <paramref name="release"/> runs once (loss check, kept-job recovery, queue resume). Resuming while the
/// leftover is still at the port would let the first held job overwrite it, and that job's claim would be counted
/// for it, so nothing would ever say so. Tried at start and on every health tick. A hold that lasts
/// <see cref="StuckAfter"/> (print.pdf open in another program) is announced once through <paramref name="stuck"/>:
/// every document printed meanwhile waits in the paused queue. Thread-safe.
/// </summary>
public sealed class StartGate(Func<bool> portIdle, Action release, Action<string> log, Action? stuck = null, Func<DateTime>? clock = null)
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private bool _released;
    private bool _told;
    private bool _toldStuck;
    private DateTime? _heldSince;

    /// <summary>How long the hold may last before it is announced.</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromSeconds(60);

    public bool Released { get { lock (_gate) return _released; } }

    /// <summary>Releases once the port is idle; true once released.</summary>
    public bool TryRelease()
    {
        lock (_gate)
        {
            if (_released) return true;
            if (!portIdle())
            {
                if (!_told) log("spool: print.pdf from the last run is still at the port; the queue stays held until it is claimed");
                _told = true;
                var now = _clock();
                _heldSince ??= now;
                if (!_toldStuck && now - _heldSince.Value >= StuckAfter)
                {
                    _toldStuck = true;
                    log($"spool: queue held for {StuckAfter.TotalSeconds:0} s by a print.pdf that cannot be claimed; announced");
                    try { stuck?.Invoke(); } catch (Exception e) { log("spool: announcing the held queue failed: " + e.Message); }
                }
                return false;
            }
            _released = true;
        }
        release();
        return true;
    }
}

/// <summary>
/// Re-reads the printer every few seconds while the app runs. A port that moves away from the spool file (any user
/// with the printer's administer right can move it) is announced once per change; a printer that appears or is fixed
/// gets a fresh loss check (a re-created printer leaves the old one's handle stale); and while the watcher runs, the
/// loss check is kept alive and a paused queue is resumed (<see cref="QueueLifecycle.Reconcile"/>).
/// </summary>
/// <param name="initial">The state the app saw (and announced) at start.</param>
public sealed class PrinterHealth(
    Func<PrinterStatus> status, PrinterState initial, Func<bool> watching,
    QueueLifecycle queue, LossCheckHost loss, Action<PrinterStatus> misconfigured, Action<string> log)
{
    private PrinterState _last = initial;

    /// <summary>Checked on every tick while the printer is installed and watched, if set.</summary>
    public KeepPrintedJobsGuard? KeepJobs { get; init; }

    public void Tick()
    {
        var s = status();
        var was = _last;
        _last = s.State;
        if (s.State != was) log($"printer: {was} -> {s.State}{(s.ActualPort is { } p ? " (" + p + ")" : "")}");
        if (s.State == PrinterState.Misconfigured && was != PrinterState.Misconfigured) misconfigured(s);
        if (s.State != PrinterState.Installed || !watching()) return;
        if (was != PrinterState.Installed) loss.Restart();
        else loss.EnsureRunning();
        queue.Reconcile();
        KeepJobs?.Check();
    }
}

/// <summary>
/// Kept-job recovery (reprinting replaced documents, and the crash recovery at start) needs the printer's "Keep
/// printed documents" setting, which anyone with the printer's administer right can clear from its Advanced tab.
/// Checked by the health tick: a cleared setting is set back (logged); if that is refused it is announced once per
/// change through <paramref name="announce"/> (the repair is the install script). A failed read is logged once.
/// </summary>
public sealed class KeepPrintedJobsGuard(IKeepPrintedJobs printer, string printerName, Action<string> log, Action<string> announce)
{
    private readonly object _gate = new();
    private string? _lastReadError;
    private bool _announced;

    public void Check()
    {
        lock (_gate)
        {
            bool keeps;
            try { keeps = printer.KeepsPrintedJobs; _lastReadError = null; }
            catch (Exception e) when (e is ExternalException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                if (e.Message != _lastReadError) log($"printer \"{printerName}\": keep printed documents: check failed: {e.Message}");
                _lastReadError = e.Message;
                return;
            }
            if (keeps) { _announced = false; return; }
            try
            {
                printer.KeepPrintedJobs();
                log($"printer \"{printerName}\": \"Keep printed documents\" was off; set back on");
                _announced = false;
            }
            catch (Exception e) when (e is ExternalException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                if (_announced) return;
                _announced = true;
                log($"printer \"{printerName}\": \"Keep printed documents\" is off and cannot be set back: {e.Message}");
                announce(NotificationText.KeepPrintedJobsOff(printerName));
            }
        }
    }
}
