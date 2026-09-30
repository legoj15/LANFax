using LANFax.Core;

namespace LANFax.Tests;

/// <summary>
/// Every job the spooler finishes on the LAN Fax queue must be matched by a file the watcher claimed; an unmatched
/// finished job means print.pdf was overwritten before it was claimed.
/// </summary>
public class PrintLossMonitorTests
{
    private sealed class FakeJobs : IPrintJobSource
    {
        public event Action<int>? JobFinished;
        public event Action<string>? Unreliable;
        public bool Started;
        public bool IsRunning => Started;
        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Dispose() => Stop();
        public void Finish(int id) => JobFinished?.Invoke(id);
        public void Discard(string why) => Unreliable?.Invoke(why);
    }

    private readonly FakeJobs _jobs = new();
    private DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private bool _atPort;
    private readonly List<int> _lost = new();
    private readonly List<int> _matched = new();
    private readonly List<int> _doubtful = new();
    private readonly List<DateTime> _unpaired = new();
    private readonly List<string> _log = new();

    private PrintLossMonitor Make()
    {
        var m = new PrintLossMonitor(_jobs, () => _atPort, () => _now) { Grace = TimeSpan.FromSeconds(15), Log = _log.Add };
        m.Lost += _lost.Add;
        m.Matched += _matched.Add;
        m.Doubtful += _doubtful.Add;
        m.ClaimUnpaired += _unpaired.Add;
        m.Start();
        return m;
    }

    private void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    [Fact]
    public void StartStartsTheJobSource()
    {
        using var m = Make();
        Assert.True(_jobs.Started);
        m.Dispose();
        Assert.False(_jobs.Started);
    }

    [Fact]
    public void IsRunningFollowsTheJobSource()
    {
        // A source whose notification thread died (spooler restart, printer re-created) must be seen as not running,
        // so the app can start a new one instead of checking nothing for the rest of the session.
        using var m = Make();
        Assert.True(m.IsRunning);
        _jobs.Started = false;
        Assert.False(m.IsRunning);
    }

    [Fact]
    public void AFinishedJobWithItsClaimedFileIsNotALoss()
    {
        using var m = Make();
        _jobs.Finish(1);
        m.FileClaimed();
        Advance(60);
        Assert.Equal(0, m.Check());
        Assert.Empty(_lost);
    }

    [Fact]
    public void AClaimArrivingBeforeTheSpoolerSaysFinishedIsMatched()
    {
        using var m = Make();
        m.FileClaimed(); // the port closes the file before the job's status turns to printed
        Advance(1);
        _jobs.Finish(7);
        Advance(60);
        Assert.Equal(0, m.Check());
    }

    [Fact]
    public void TwoFinishedJobsAndOneClaimedFileIsOneLossAfterTheGrace()
    {
        using var m = Make();
        _jobs.Finish(1);
        _jobs.Finish(2);
        m.FileClaimed();
        Advance(5);
        Assert.Equal(0, m.Check()); // the claim may still be coming
        Advance(11);
        Assert.Equal(1, m.Check());
        Assert.Equal([1], _lost);
        Advance(60);
        Assert.Equal(0, m.Check()); // reported once
        Assert.Single(_lost);
    }

    [Fact]
    public void AFileStillWaitingAtThePortIsNotALossYet()
    {
        using var m = Make();
        _jobs.Finish(1);
        _atPort = true; // e.g. jobs\ unwritable: the watcher keeps retrying
        Advance(120);
        Assert.Equal(0, m.Check());
        _atPort = false;
        m.FileClaimed();
        Advance(60);
        Assert.Equal(0, m.Check());
        Assert.Empty(_lost);
    }

    [Fact]
    public void AnOldUnmatchedClaimDoesNotCoverALaterLoss()
    {
        using var m = Make();
        m.FileClaimed(); // a file dropped at the port by hand, no spooler job
        Advance(60);
        Assert.Equal(0, m.Check());
        _jobs.Finish(3);
        Advance(16);
        Assert.Equal(1, m.Check());
    }

    [Fact]
    public void ManyJobsEachClaimedInAnyOrderAreNoLoss()
    {
        using var m = Make();
        for (int i = 1; i <= 5; i++)
        {
            if (i % 2 == 0) { m.FileClaimed(); _jobs.Finish(i); }
            else { _jobs.Finish(i); m.FileClaimed(); }
            Advance(0.5);
        }
        Advance(60);
        Assert.Equal(0, m.Check());
    }

    [Fact]
    public void DiscardedNotificationsResetTheCountAndAreLogged()
    {
        using var m = Make();
        _jobs.Finish(1);
        _jobs.Finish(2);
        _jobs.Discard("spooler discarded notifications");
        Advance(60);
        Assert.Equal(0, m.Check()); // cannot tell any more: no false alarm
        Assert.Contains(_log, l => l.Contains("spooler discarded notifications"));
    }

    [Fact]
    public void AClaimIsMatchedToTheNewestUnclaimedJob()
    {
        // The port keeps only the last file written, so the claim is that job's; the older one was replaced. The
        // match was made after job 1 finished, so job 1's loss does not cast doubt on it.
        using var m = Make();
        _jobs.Finish(1);
        _jobs.Finish(2);
        Assert.True(m.IsPending(1) && m.IsPending(2));
        m.FileClaimed();
        Assert.False(m.IsPending(2));
        Assert.True(m.IsFollowed(2)); // matched, not yet confirmed
        Assert.True(m.IsPending(1));
        Advance(16);
        m.Check();
        Assert.Equal([1], _lost);
        Assert.Equal([2], _matched);
        Assert.Empty(_doubtful);
        Assert.False(m.IsPending(1));
        Assert.False(m.IsFollowed(1) || m.IsFollowed(2));
    }

    [Fact]
    public void AClaimBeforeItsJobIsMatchedWhenTheJobFinishes()
    {
        using var m = Make();
        m.FileClaimed();
        _jobs.Finish(7);
        Assert.False(m.IsPending(7));
        Assert.True(m.IsFollowed(7));
        Advance(16);
        m.Check();
        Assert.Equal([7], _matched);
        Assert.Empty(_unpaired);
    }

    [Fact]
    public void AMatchIsConfirmedOnlyOnceTheGraceHasPassed()
    {
        // A claim may be the file of a job whose "finished" is still on its way, so the pairing is not trusted (and
        // the kept job not deleted) until no later job can have been the claimed file's owner.
        using var m = Make();
        _jobs.Finish(1);
        m.FileClaimed();
        Assert.Empty(_matched);
        Advance(10);
        m.Check();
        Assert.Empty(_matched);
        Advance(6);
        m.Check();
        Assert.Equal([1], _matched);
        Assert.False(m.IsFollowed(1));
    }

    [Fact]
    public void AClaimTakenBeforeALaterJobReportedIsDoubtedWhenThatJobIsLost()
    {
        // Job 1 finished; a stall let job 2 overwrite print.pdf; the claim (job 2's file) came before job 2's
        // "finished" and was paired with job 1. Job 2 then has no claim. Which one's file was lost cannot be told:
        // both are handed on to be printed again (duplicates rather than a silent loss), neither is deleted.
        using var m = Make();
        _jobs.Finish(1);
        m.FileClaimed();
        _jobs.Finish(2);
        Advance(16);
        Assert.Equal(1, m.Check());
        Assert.Equal([2], _lost);
        Assert.Equal([1], _doubtful);
        Assert.Empty(_matched);
    }

    [Fact]
    public void AJobPairedWithAnEarlierClaimIsDoubtedWhenALaterJobIsLost()
    {
        using var m = Make();
        m.FileClaimed();
        _jobs.Finish(1); // paired with the claim, which may be job 2's file
        _jobs.Finish(2);
        Advance(16);
        m.Check();
        Assert.Equal([2], _lost);
        Assert.Equal([1], _doubtful);
        Assert.Empty(_matched);
    }

    [Fact]
    public void AConfirmedMatchIsNotDoubtedByALaterLoss()
    {
        using var m = Make();
        _jobs.Finish(1);
        m.FileClaimed();
        Advance(16);
        m.Check();
        _jobs.Finish(2);
        Advance(16);
        m.Check();
        Assert.Equal([1], _matched);
        Assert.Equal([2], _lost);
        Assert.Empty(_doubtful);
    }

    [Fact]
    public void AnExpiredUnmatchedClaimIsHandedOn()
    {
        // Nobody finished for it in time (a job the loss check did not follow): the kept-job sweep counts it.
        using var m = Make();
        var at = _now;
        m.FileClaimed();
        Advance(16);
        m.Check();
        Assert.Equal([at], _unpaired);
    }

    [Fact]
    public void AResetHandsEverythingInFlightOnAndPairsNothingForTheGrace()
    {
        // Counts are unreliable after discarded notifications: pending and matched jobs stop being followed (the
        // kept-job sweep accounts for them against the claims handed on), and for the next Grace a claim may be a
        // job the discarded batch reported, so nothing is paired.
        using var m = Make();
        _jobs.Finish(1);
        m.FileClaimed(); // matched with 1, not yet confirmed
        _jobs.Finish(2); // pending
        m.FileClaimed(); // matched with 2
        m.FileClaimed(); // unmatched
        _jobs.Discard("the spooler discarded job notifications");
        Assert.Equal(3, _unpaired.Count);
        Assert.False(m.IsFollowed(1) || m.IsFollowed(2));
        m.FileClaimed();
        _jobs.Finish(3);
        Assert.Equal(4, _unpaired.Count);
        Assert.False(m.IsFollowed(3));
        Advance(16);
        Assert.Equal(0, m.Check());
        Assert.Empty(_matched);
        Assert.Empty(_doubtful);
        _jobs.Finish(4); // past the Grace: followed again
        m.FileClaimed();
        Advance(16);
        m.Check();
        Assert.Equal([4], _matched);
    }

    [Fact]
    public void ADisposedMonitorHandsItsClaimsInFlightOn()
    {
        // A monitor replaced mid-session (dead notification thread, printer re-created) must not take its claims
        // with it: its jobs are no longer followed, and the kept-job sweep needs the claims to account for them.
        var m = Make();
        _jobs.Finish(1);
        m.FileClaimed(); // paired, not yet trusted
        m.FileClaimed(); // unmatched
        m.Dispose();
        Assert.Equal(2, _unpaired.Count);
        Assert.Empty(_matched);
    }

    [Fact]
    public void AtExitMatchesOldEnoughAreConfirmedAndTheRestLeftKept()
    {
        using var m = Make();
        _jobs.Finish(1);
        m.FileClaimed();
        Advance(3);
        _jobs.Finish(2);
        m.FileClaimed(); // too fresh: its job's "finished" might still come
        m.SettleForExit();
        Assert.Equal([1], _matched);
        _jobs.Finish(3); // pending: its loss would cast doubt on the match of job 2
        Advance(3);
        m.SettleForExit();
        Assert.Equal([1], _matched);
    }

    [Fact]
    public void AJobTheAppDeletedItselfIsNotCounted()
    {
        using var m = Make();
        m.IgnoreJob = id => id == 67;
        _jobs.Finish(67); // PRINTED|DELETING of a kept job the app just deleted
        Assert.False(m.IsPending(67));
        Advance(60);
        Assert.Equal(0, m.Check());
        m.FileClaimed(); // a later claim is not paired with it either
        _jobs.Finish(68);
        Advance(60);
        Assert.Equal(0, m.Check());
    }

    [Fact]
    public void AFaultySubscriberDoesNotBreakTheCheck()
    {
        using var m = Make();
        m.Lost += _ => throw new InvalidOperationException("subscriber bug");
        _jobs.Finish(1);
        Advance(20);
        Assert.Equal(1, m.Check());
        Assert.Equal([1], _lost);
    }
}
