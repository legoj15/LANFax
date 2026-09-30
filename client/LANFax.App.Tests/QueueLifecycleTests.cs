using System.ComponentModel;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

/// <summary>
/// The LAN Fax queue is paused whenever no watcher runs (jobs wait in the queue instead of overwriting each other at
/// the port) and resumed once the watcher is claiming files.
/// </summary>
public class QueueLifecycleTests
{
    private sealed class FakeQueue : IPrintQueueControl
    {
        public readonly List<string> Calls = new();
        public Exception? FailWith;
        public Exception? CheckFailsWith;
        public bool Paused;
        public void Pause() { Calls.Add("pause"); if (FailWith != null) throw FailWith; Paused = true; }
        public void Resume() { Calls.Add("resume"); if (FailWith != null) throw FailWith; Paused = false; }
        public bool IsPaused { get { if (CheckFailsWith != null) throw CheckFailsWith; return Paused; } }
    }

    private readonly FakeQueue _q = new();
    private readonly List<string> _log = new();
    private readonly List<string> _balloons = new();

    private QueueLifecycle Make() => new(_q, "LAN Fax", _log.Add, _balloons.Add);

    [Fact]
    public void StartedResumesAndExitingPauses()
    {
        var l = Make();
        l.Started();
        l.Exiting();
        Assert.Equal(["resume", "pause"], _q.Calls);
        Assert.Empty(_balloons);
    }

    [Fact]
    public void ExitingTwiceOrAfterSessionEndPausesOnce()
    {
        var l = Make();
        l.Started();
        l.Exiting();
        l.Exiting();
        Assert.Equal(["resume", "pause"], _q.Calls);
    }

    [Fact]
    public void AccessDeniedIsLoggedWithOneBalloonAndDoesNotThrow()
    {
        _q.FailWith = new Win32Exception(5); // ERROR_ACCESS_DENIED
        var l = Make();
        l.Started();
        l.Resumed(); // e.g. after a printer reinstall: fails again, no second balloon
        l.Exiting();
        Assert.Single(_balloons);
        Assert.Contains("LAN Fax", _balloons[0]);
        Assert.Contains("pause", _balloons[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, _log.Count);
    }

    [Fact]
    public void AMissingPrinterIsLoggedWithoutABalloon()
    {
        // The missing-printer balloon (with its install offer) already covers this.
        _q.FailWith = new Win32Exception(1801); // ERROR_INVALID_PRINTER_NAME
        var l = Make();
        l.Started();
        Assert.Empty(_balloons);
        Assert.Single(_log);
    }

    [Fact]
    public void ResumedAfterAnInstallResumesAgain()
    {
        // The install script leaves the queue paused; the running app takes it over.
        var l = Make();
        l.Started();
        l.Resumed();
        Assert.Equal(["resume", "resume"], _q.Calls);
    }

    [Fact]
    public void AWatcherThatCannotStartPausesTheQueue()
    {
        // Nobody claims print.pdf: jobs must wait in the queue, not overwrite each other at the port.
        var l = Make();
        l.WatcherFailed();
        l.Exiting();
        Assert.Equal(["pause"], _q.Calls);
    }

    [Fact]
    public void HoldAtStartPausesThenStartedResumes()
    {
        // A queue left running by a crash is held while a leftover print.pdf is claimed, then let go.
        var l = Make();
        l.Hold();
        l.Started();
        Assert.Equal(["pause", "resume"], _q.Calls);
    }

    [Fact]
    public void ReconcileResumesAQueueFoundPausedWhileWatching()
    {
        // Paused behind the app's back (the install script, another user's LANFax exiting): documents would wait
        // in the queue with no word to anyone.
        var l = Make();
        l.Started();
        _q.Paused = true;
        l.Reconcile();
        Assert.Equal(["resume", "resume"], _q.Calls);
        Assert.False(_q.Paused);
        Assert.Contains(_log, m => m.Contains("found paused"));
        l.Reconcile(); // running now: nothing to do
        Assert.Equal(2, _q.Calls.Count);
    }

    [Fact]
    public void ReconcileLeavesTheQueueAloneWhenNoWatcherRuns()
    {
        var l = Make();
        _q.Paused = true;
        l.Reconcile(); // not started yet
        l.Hold();
        l.Reconcile();
        l.WatcherFailed();
        l.Reconcile();
        var exited = Make();
        exited.Started();
        exited.Exiting();
        exited.Reconcile();
        Assert.Equal(["pause", "pause", "resume", "pause"], _q.Calls);
    }

    [Fact]
    public void AFailingReconcileIsLoggedOnceUntilItChanges()
    {
        var l = Make();
        l.Started();
        _q.CheckFailsWith = new Win32Exception(1801);
        l.Reconcile();
        l.Reconcile();
        l.Reconcile();
        Assert.Single(_log, m => m.Contains("check failed"));
        Assert.Empty(_balloons);
    }

    [Fact]
    public void NothingResumesTheQueueOnceTheAppIsExiting()
    {
        // The start hand-over or an install can finish on another thread just after Exit paused the queue; resuming
        // then would leave it printing with nobody claiming.
        var l = Make();
        l.Hold();
        l.Exiting();
        l.Started();
        l.Resumed();
        _q.Paused = true;
        l.Reconcile();
        Assert.Equal(["pause"], _q.Calls);
        Assert.True(_q.Paused);
    }

    [Fact]
    public void AResumeThatKeepsFailingIsLoggedOnceNotEveryTick()
    {
        // Paused by the install script, resume denied: two log lines every 10 s tick would be ~17,000 a day.
        var l = Make();
        l.Started();
        _q.Paused = true;
        _q.FailWith = new Win32Exception(5);
        for (int i = 0; i < 5; i++) l.Reconcile();
        Assert.Single(_log, m => m.Contains("found paused"));
        Assert.Single(_log, m => m.Contains("resume failed"));
        Assert.Single(_balloons);
        _q.FailWith = null; // repaired
        l.Reconcile();
        Assert.False(_q.Paused);
        Assert.Equal(2, _log.Count(m => m.Contains("found paused")));
        _q.Paused = true; // paused again later: said again
        l.Reconcile();
        Assert.Equal(3, _log.Count(m => m.Contains("found paused")));
    }

    [Fact]
    public void ADeniedPauseIsRememberedAndTheBalloonOffersARepair()
    {
        _q.FailWith = new Win32Exception(5);
        var l = Make();
        Assert.False(l.Denied);
        l.Hold();
        Assert.True(l.Denied);
        Assert.Contains("repair", _balloons[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reinstall", _balloons[0], StringComparison.OrdinalIgnoreCase);
    }
}
