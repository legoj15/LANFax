using System.ComponentModel;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

/// <summary>A spooler job source whose start can fail (printer missing) and whose thread can die.</summary>
internal sealed class FakeJobSource : IPrintJobSource
{
    public event Action<int>? JobFinished;
    public event Action<string>? Unreliable;
    public Exception? FailStart;
    public bool Started;
    public bool IsRunning => Started;
    public void Start() { if (FailStart != null) throw FailStart; Started = true; }
    public void Stop() => Started = false;
    public void Dispose() => Stop();
    public void Finish(int id) => JobFinished?.Invoke(id);
    public void Discard(string why) => Unreliable?.Invoke(why);
}

internal sealed class PausableQueue : IPrintQueueControl
{
    public readonly List<string> Calls = new();
    public bool Paused;
    public void Pause() { Calls.Add("pause"); Paused = true; }
    public void Resume() { Calls.Add("resume"); Paused = false; }
    public bool IsPaused => Paused;
}

/// <summary>
/// The loss check must be startable again during a session: a printer installed or fixed after the app started, or a
/// spooler restart that killed its notifications, must not leave "never silent" off until the next restart.
/// </summary>
public class LossCheckHostTests
{
    private readonly List<FakeJobSource> _made = new();
    private readonly List<string> _log = new();
    private readonly List<int> _lost = new();
    private DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private Exception? _nextFail;

    private LossCheckHost Make() => new(() =>
    {
        var j = new FakeJobSource { FailStart = _nextFail };
        _made.Add(j);
        var m = new PrintLossMonitor(j, () => false, () => _now);
        m.Lost += _lost.Add;
        return m;
    }, checkEvery: null, _log.Add);

    [Fact]
    public void APrinterMissingAtStartIsPickedUpByALaterStart()
    {
        using var h = Make();
        _nextFail = new Win32Exception(1801); // ERROR_INVALID_PRINTER_NAME
        h.Restart();
        Assert.False(h.Running);
        Assert.Null(h.Current);
        Assert.Contains(_log, m => m.Contains("unavailable"));
        _nextFail = null;
        h.EnsureRunning(); // the printer was installed meanwhile
        Assert.True(h.Running);
        Assert.Contains(_log, m => m.Contains("running"));
    }

    [Fact]
    public void ARepeatedFailureIsLoggedOnce()
    {
        using var h = Make();
        _nextFail = new Win32Exception(1801);
        h.EnsureRunning();
        h.EnsureRunning();
        h.EnsureRunning();
        Assert.Equal(3, _made.Count);
        Assert.Single(_log, m => m.Contains("unavailable"));
    }

    [Fact]
    public void EnsureRunningRestartsADeadSourceAndLeavesALiveOneAlone()
    {
        using var h = Make();
        h.EnsureRunning();
        h.EnsureRunning();
        Assert.Single(_made);
        _made[0].Started = false; // its notification thread died
        h.EnsureRunning();
        Assert.Equal(2, _made.Count);
        Assert.True(h.Running);
    }

    [Fact]
    public void ClaimsGoToTheCurrentMonitorAndTheOldOneIsStopped()
    {
        using var h = Make();
        h.Restart();
        var first = h.Current!;
        h.Restart(); // e.g. after a printer reinstall
        Assert.False(_made[0].Started);
        Assert.NotSame(first, h.Current);
        _made[1].Finish(7);
        h.FileClaimed();
        _now = _now.AddSeconds(60);
        Assert.Equal(0, h.Current!.Check());
        _made[1].Finish(8); // no claim for this one
        _now = _now.AddSeconds(60);
        Assert.Equal(1, h.Current!.Check());
        Assert.Equal([8], _lost);
    }

    [Fact]
    public void AClaimWithNoMonitorIsHandedOnAsUnpaired()
    {
        // The kept-job sweep counts it against the jobs the loss check did not follow (not running, printer
        // missing at start): dropping it would make one of those jobs look replaced.
        var unpaired = new List<DateTime>();
        using var h = new LossCheckHost(() => new PrintLossMonitor(new FakeJobSource(), () => false, () => _now), null, _log.Add, unpaired.Add);
        h.FileClaimed();
        Assert.False(h.Running);
        Assert.Single(unpaired);
    }
}

/// <summary>
/// Every few seconds the app re-reads the printer: a port moved mid-session is announced, a printer that appears (or
/// is fixed) gets its loss check (re)started, and a queue paused behind the app's back is resumed while it watches.
/// </summary>
public class PrinterHealthTests
{
    private readonly PausableQueue _q = new();
    private readonly List<FakeJobSource> _made = new();
    private readonly List<PrinterStatus> _announced = new();
    private readonly List<string> _log = new();
    private PrinterStatus _status = new(PrinterState.Installed, @"C:\ProgramData\LANFax\spool\print.pdf");
    private bool _watching = true;

    private (PrinterHealth health, LossCheckHost loss, QueueLifecycle queue) Make(PrinterState initial)
    {
        var loss = new LossCheckHost(() =>
        {
            var j = new FakeJobSource();
            _made.Add(j);
            return new PrintLossMonitor(j, () => false);
        }, null, _log.Add);
        var queue = new QueueLifecycle(_q, "LAN Fax", _log.Add, _ => { });
        var health = new PrinterHealth(() => _status, initial, () => _watching, queue, loss, _announced.Add, _log.Add)
        {
            KeepJobs = new KeepPrintedJobsGuard(_keeper, "LAN Fax", _log.Add, _ => { }),
        };
        return (health, loss, queue);
    }

    private sealed class Keeper : IKeepPrintedJobs
    {
        public bool Keeps = true;
        public bool KeepsPrintedJobs => Keeps;
        public void KeepPrintedJobs() => Keeps = true;
    }

    private readonly Keeper _keeper = new();

    [Fact]
    public void AnInstalledPrinterHasKeepPrintedJobsSetBack()
    {
        var (h, _, queue) = Make(PrinterState.Installed);
        queue.Started();
        _keeper.Keeps = false;
        h.Tick();
        Assert.True(_keeper.Keeps);
    }

    [Fact]
    public void APortMovedMidSessionIsAnnouncedOncePerChange()
    {
        var (h, _, _) = Make(PrinterState.Installed);
        _status = new(PrinterState.Misconfigured, "LPT1:");
        h.Tick();
        h.Tick();
        Assert.Single(_announced);
        Assert.Equal("LPT1:", _announced[0].ActualPort);
        _status = new(PrinterState.Installed, @"C:\ProgramData\LANFax\spool\print.pdf");
        h.Tick();
        _status = new(PrinterState.Misconfigured, "FILE:");
        h.Tick();
        Assert.Equal(2, _announced.Count);
    }

    [Fact]
    public void AMisconfigurationAlreadyAnnouncedAtStartIsNotRepeated()
    {
        _status = new(PrinterState.Misconfigured, "LPT1:");
        var (h, _, _) = Make(PrinterState.Misconfigured);
        h.Tick();
        Assert.Empty(_announced);
    }

    [Fact]
    public void APrinterInstalledLaterGetsItsLossCheckAndItsQueueResumed()
    {
        var (h, loss, queue) = Make(PrinterState.Missing);
        queue.Started();
        _q.Paused = true; // the install script leaves it paused
        h.Tick();
        Assert.True(loss.Running);
        Assert.False(_q.Paused);
    }

    [Fact]
    public void AFixedPrinterRestartsTheLossCheckOnce()
    {
        var (h, loss, queue) = Make(PrinterState.Misconfigured);
        queue.Started();
        loss.Restart();
        Assert.Single(_made);
        h.Tick(); // Misconfigured -> Installed: the printer may have been re-created, its old handle stale
        Assert.Equal(2, _made.Count);
        h.Tick(); // steady: left alone
        Assert.Equal(2, _made.Count);
        Assert.True(loss.Running);
    }

    [Fact]
    public void NothingIsStartedOrResumedWhileTheWatcherIsNotRunning()
    {
        _watching = false;
        var (h, loss, _) = Make(PrinterState.Missing);
        _q.Paused = true;
        h.Tick();
        Assert.False(loss.Running);
        Assert.True(_q.Paused);
        Assert.Empty(_q.Calls);
    }

    [Fact]
    public void AMissingPrinterStartsNothing()
    {
        _status = new(PrinterState.Missing, null);
        var (h, loss, queue) = Make(PrinterState.Installed);
        queue.Started();
        h.Tick();
        Assert.False(loss.Running);
        Assert.Empty(_made);
        Assert.Empty(_announced);
    }
}

/// <summary>
/// At start the queue is held while a print.pdf left from the last run is claimed. If that takes longer than the
/// start-up wait (antivirus scanning it), the queue stays held until the port is clear: resuming early lets the first
/// held job overwrite the leftover, and its claim would then be counted for that job, so nothing would say so.
/// </summary>
public class StartGateTests
{
    private bool _idle;
    private int _released;
    private readonly List<string> _log = new();

    private StartGate Make() => new(() => _idle, () => _released++, _log.Add);

    [Fact]
    public void ABusyPortKeepsTheQueueHeldUntilItIsClear()
    {
        var g = Make();
        Assert.False(g.TryRelease());
        Assert.False(g.TryRelease());
        Assert.False(g.Released);
        Assert.Equal(0, _released);
        Assert.Single(_log, l => l.Contains("held"));
        _idle = true;
        Assert.True(g.TryRelease());
        Assert.True(g.Released);
        Assert.Equal(1, _released);
    }

    [Fact]
    public void AStartHeldTooLongIsAnnouncedOnce()
    {
        // A leftover print.pdf held open by another program (a PDF viewer) keeps every new document waiting in the
        // paused queue: the log alone would leave the user wondering where their prints went.
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        int stuck = 0;
        var g = new StartGate(() => _idle, () => _released++, _log.Add, () => stuck++, () => now) { StuckAfter = TimeSpan.FromSeconds(30) };
        Assert.False(g.TryRelease());
        now = now.AddSeconds(29);
        g.TryRelease();
        Assert.Equal(0, stuck);
        now = now.AddSeconds(2);
        g.TryRelease();
        g.TryRelease();
        Assert.Equal(1, stuck);
        _idle = true;
        Assert.True(g.TryRelease());
    }

    [Fact]
    public void ReleaseRunsOnce()
    {
        _idle = true;
        var g = Make();
        Assert.True(g.TryRelease());
        Assert.True(g.TryRelease());
        Assert.Equal(1, _released);
    }
}

/// <summary>
/// Kept-job recovery depends on the printer keeping printed jobs. Anyone with the printer's administer right (the
/// installer's Users ACE) can clear "Keep printed documents": the health check sets it back, or says so once.
/// </summary>
public class KeepPrintedJobsGuardTests
{
    private sealed class FakePrinter : IKeepPrintedJobs
    {
        public bool Keeps = true;
        public Exception? ReadFails, SetFails;
        public int Sets;
        public bool KeepsPrintedJobs => ReadFails != null ? throw ReadFails : Keeps;
        public void KeepPrintedJobs() { Sets++; if (SetFails != null) throw SetFails; Keeps = true; }
    }

    private readonly FakePrinter _p = new();
    private readonly List<string> _log = new();
    private readonly List<string> _told = new();

    private KeepPrintedJobsGuard Make() => new(_p, "LAN Fax", _log.Add, _told.Add);

    [Fact]
    public void ASettingLeftOnIsLeftAlone()
    {
        var g = Make();
        g.Check();
        Assert.Equal(0, _p.Sets);
        Assert.Empty(_log);
    }

    [Fact]
    public void AClearedSettingIsSetBackAndLogged()
    {
        var g = Make();
        _p.Keeps = false;
        g.Check();
        Assert.True(_p.Keeps);
        Assert.Contains(_log, l => l.Contains("keep printed", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_told);
    }

    [Fact]
    public void ASettingThatCannotBeSetBackIsAnnouncedOncePerChange()
    {
        var g = Make();
        _p.Keeps = false;
        _p.SetFails = new Win32Exception(5);
        g.Check();
        g.Check();
        Assert.Single(_told);
        Assert.Single(_log, l => l.Contains("cannot", StringComparison.OrdinalIgnoreCase));
        _p.SetFails = null;
        g.Check(); // repaired
        _p.Keeps = false;
        _p.SetFails = new Win32Exception(5);
        g.Check();
        Assert.Equal(2, _told.Count);
    }

    [Fact]
    public void AFailedReadIsLoggedOnceAndNothingElseHappens()
    {
        var g = Make();
        _p.ReadFails = new Win32Exception(1801);
        g.Check();
        g.Check();
        Assert.Single(_log);
        Assert.Equal(0, _p.Sets);
        Assert.Empty(_told);
    }
}

public class KeepPrintedJobsTextTests
{
    [Fact]
    public void NamesThePrinterAndOffersTheRepair()
    {
        var t = NotificationText.KeepPrintedJobsOff("LAN Fax");
        Assert.Contains("LAN Fax", t);
        Assert.Contains("Click here", t);
        Assert.True(t.Length < 256);
        var h = NotificationText.HeldAtStart("LAN Fax", @"C:\ProgramData\LANFax\spool\print.pdf");
        Assert.Contains("print.pdf", h);
        Assert.True(h.Length < 256);
    }
}

public class UnrecoveredTextTests
{
    [Fact]
    public void NamesTheDocumentsAndSaysToPrintThemAgain()
    {
        var one = NotificationText.ForUnrecovered("LAN Fax", ["invoice.pdf"]);
        Assert.Contains("invoice.pdf", one);
        Assert.Contains("print", one, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LAN Fax", one);
        var many = NotificationText.ForUnrecovered("LAN Fax", ["a", "b", "c", "d", "e"]);
        Assert.Contains("5 documents", many);
        Assert.True(many.Length < 256, "balloon text is cut at 255 characters");
        var running = NotificationText.ForUnrecovered("LAN Fax", ["invoice.pdf"], whileRunning: true);
        Assert.DoesNotContain("not running", running);
        Assert.Contains("may have been replaced", running);
    }
}

public class LostJobTextTests
{
    [Fact]
    public void OnlyAWatchedCorrectlyInstalledPrinterSaysPrintAgain()
    {
        Assert.Contains("print it again", NotificationText.ForLostJob("LAN Fax", PrinterState.Installed, watching: true));
        // Anywhere else "print it again" cannot help: the port or the watcher is the problem, announced on its own.
        Assert.Null(NotificationText.ForLostJob("LAN Fax", PrinterState.Misconfigured, watching: true));
        Assert.Null(NotificationText.ForLostJob("LAN Fax", PrinterState.Missing, watching: true));
        Assert.Null(NotificationText.ForLostJob("LAN Fax", PrinterState.Installed, watching: false));
    }
}
