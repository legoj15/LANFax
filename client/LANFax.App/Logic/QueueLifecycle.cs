using System.ComponentModel;
using System.Runtime.InteropServices;
using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>
/// Keeps the LAN Fax queue paused whenever no watcher claims print.pdf, so jobs wait in the queue instead of
/// overwriting each other at the port: held at start while a leftover print.pdf is claimed (<see cref="Hold"/>),
/// resumed once the watcher runs (<see cref="Started"/>, and again after a printer install: <see cref="Resumed"/>),
/// kept resumed while it runs (<see cref="Reconcile"/>: the install script, or a person, may pause it behind the
/// app's back), paused on exit or when the watcher cannot start. A crash or kill skips the pause; the next start
/// prints again whatever the printer kept (<see cref="KeptJobRecovery"/>). Failures never stop the app: each is
/// logged, and a denied pause/resume shows one balloon per run (a missing printer has its own balloon already).
/// Thread-safe (the health check runs off the UI thread).
/// </summary>
public sealed class QueueLifecycle(IPrintQueueControl queue, string printerName, Action<string> log, Action<string> balloon)
{
    private const int ErrorAccessDenied = 5;
    private readonly object _gate = new();
    private bool _warned;
    private bool _paused;
    private bool _watching;
    private bool _closed; // Exiting ran: nothing may resume the queue any more
    private string? _lastCheckError;
    private string? _lastReconcileError; // a resume that keeps failing is logged once until it changes or succeeds

    public static string DeniedText(string printerName) =>
        $"LANFax cannot pause or resume the \"{printerName}\" printer, so documents printed while LANFax is closed may be lost. Click here to repair it (an administrator must approve).";

    /// <summary>A pause or resume was refused (access denied) this run: the printer's permissions need a repair.</summary>
    public bool Denied { get; private set; }

    /// <summary>App start, before a leftover print.pdf is claimed: hold jobs a crash left printing.</summary>
    public void Hold() { lock (_gate) if (!_closed) Run("pause", queue.Pause, paused: true, watching: false); }

    /// <summary>The watcher is running: let the queue print.</summary>
    public void Started() { lock (_gate) if (!_closed) Run("resume", queue.Resume, paused: false, watching: true); }

    /// <summary>The printer was (re)installed while the app runs: take the queue back.</summary>
    public void Resumed() { lock (_gate) if (!_closed) Run("resume", queue.Resume, paused: false, watching: true); }

    /// <summary>Nobody will claim print.pdf: hold the jobs in the queue.</summary>
    public void WatcherFailed() { lock (_gate) if (!_closed) Run("pause", queue.Pause, paused: true, watching: false); }

    /// <summary>The app is closing (tray Exit, logoff, shutdown). Once only; afterwards nothing resumes the queue.</summary>
    public void Exiting()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            if (_paused && !_watching) return;
            Run("pause", queue.Pause, paused: true, watching: false);
            _watching = false; // even when the pause failed: nothing claims print.pdf from here on
        }
    }

    /// <summary>
    /// While the watcher runs, a queue found paused is resumed (logged). Before start, after exit, or with a watcher
    /// that failed, nothing is touched. A failing check, or a resume that keeps failing, is logged once until its
    /// message changes.
    /// </summary>
    public void Reconcile()
    {
        lock (_gate)
        {
            if (!_watching) return;
            bool paused;
            try { paused = queue.IsPaused; _lastCheckError = null; }
            catch (Exception e) when (e is ExternalException or DllNotFoundException or EntryPointNotFoundException)
            {
                if (e.Message != _lastCheckError) log($"queue \"{printerName}\": check failed: {e.Message}");
                _lastCheckError = e.Message;
                return;
            }
            if (!paused) { _lastReconcileError = null; return; }
            try
            {
                queue.Resume();
                log($"queue \"{printerName}\": found paused while LANFax watches the port; resumed");
                _paused = false;
                _lastReconcileError = null;
            }
            catch (Exception e) when (e is ExternalException or DllNotFoundException or EntryPointNotFoundException)
            {
                if (e.Message != _lastReconcileError)
                    log($"queue \"{printerName}\": found paused while LANFax watches the port; resume failed: {e.Message}");
                _lastReconcileError = e.Message;
                Warn(e);
            }
        }
    }

    private void Run(string what, Action call, bool paused, bool watching)
    {
        try
        {
            call();
            _paused = paused;
            _watching = watching;
            log($"queue \"{printerName}\": {what}d");
        }
        catch (Exception e) when (e is ExternalException or DllNotFoundException or EntryPointNotFoundException)
        {
            log($"queue \"{printerName}\": {what} failed: {e.Message}");
            if (!paused) _watching = watching; // the watcher runs whether or not the queue could be resumed
            Warn(e);
        }
    }

    private void Warn(Exception e)
    {
        if (e is not Win32Exception { NativeErrorCode: ErrorAccessDenied }) return;
        Denied = true;
        if (_warned) return;
        _warned = true;
        balloon(DeniedText(printerName));
    }
}
