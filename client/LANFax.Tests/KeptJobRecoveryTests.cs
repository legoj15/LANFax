using System.ComponentModel;
using LANFax.Core;

namespace LANFax.Tests;

/// <summary>
/// The LAN Fax printer keeps printed jobs (PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS), so a document whose print.pdf was
/// replaced before anyone claimed it can be printed again: after a crash or kill (nobody claimed anything while the
/// app was dead) and when the running loss check finds a replaced file. Jobs the app is done with are deleted, or
/// written to a ledger when they cannot be (another user's job), so nothing is ever printed twice on purpose.
/// </summary>
public class KeptJobRecoveryTests : IDisposable
{
    private sealed class FakeStore : IKeptJobStore
    {
        public readonly List<KeptJob> Jobs = new();
        public readonly List<string> Calls = new();
        public Func<int, Exception?> DeleteFails = _ => null;
        public Func<int, Exception?> RestartFails = _ => null;
        public IReadOnlyList<KeptJob> Printed() => Jobs.ToList();
        public void Delete(int id)
        {
            Calls.Add($"delete {id}");
            if (DeleteFails(id) is { } e) throw e;
            Jobs.RemoveAll(j => j.Id == id);
        }
        public void Restart(int id)
        {
            Calls.Add($"restart {id}");
            if (RestartFails(id) is { } e) throw e;
            Jobs.RemoveAll(j => j.Id == id); // printing again: no longer "printed" until it finishes
        }
        public KeptJob Add(int id, int position, string doc = "doc", string user = "me", int pages = 0)
        {
            var j = new KeptJob(id, position, doc + id, user, new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc).AddMinutes(id), pages);
            Jobs.Add(j);
            return j;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-kept-" + Guid.NewGuid().ToString("N"));
    private readonly FakeStore _store = new();
    private readonly List<string> _log = new();
    private DateTime _now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string LedgerPath => Path.Combine(_dir, "kept-jobs.json");

    private KeptJobRecovery Make() => new(_store, new KeptJobLedger(LedgerPath), () => _now) { Log = _log.Add };

    private static readonly Func<int, bool> NonePending = _ => false;

    [Fact]
    public void AtStartEveryUnaccountedJobButTheLeftoversOwnerIsPrintedAgain()
    {
        // Crash, three documents printed while nobody claimed: the port holds only the last one (claimed at start),
        // the first two are printed again so the watcher claims them one by one.
        _store.Add(11, position: 2, pages: 2);
        _store.Add(10, position: 1, pages: 1);
        _store.Add(12, position: 3, pages: 3);
        var r = Make().RecoverAtStart(leftoverAtPort: true, NonePending, leftoverPages: 3);
        Assert.Equal(["delete 12", "restart 10", "restart 11"], _store.Calls);
        Assert.Equal(2, r.Reprinted);
        Assert.Empty(r.Unrecovered);
    }

    [Fact]
    public void AtStartTheLeftoversOwnerIsFoundByItsPageCountNotItsQueuePosition()
    {
        // Measured (probe -Crash, 2026-09-30): jobs spooled at the same time do not print in queue order; the
        // leftover had 2 pages while the last job by position had 3. Deleting by position lost a document silently.
        _store.Add(10, position: 1, pages: 1);
        _store.Add(11, position: 2, pages: 2);
        _store.Add(12, position: 3, pages: 3);
        var r = Make().RecoverAtStart(leftoverAtPort: true, NonePending, leftoverPages: 2);
        Assert.Equal(["delete 11", "restart 10", "restart 12"], _store.Calls);
        Assert.Equal(2, r.Reprinted);
    }

    [Theory]
    [InlineData(1)]    // two jobs with the leftover's page count
    [InlineData(7)]    // no job with it
    [InlineData(null)] // the leftover's pages could not be read
    public void AtStartALeftoverWhoseOwnerCannotBeToldHasEveryJobPrintedAgain(int? leftoverPages)
    {
        // One of them is a duplicate of the leftover: a second send dialog, never a silent loss.
        _store.Add(10, position: 1, pages: 1);
        _store.Add(11, position: 2, pages: 1);
        var r = Make().RecoverAtStart(leftoverAtPort: true, NonePending, leftoverPages);
        Assert.Equal(["restart 10", "restart 11"], _store.Calls);
        Assert.Equal(2, r.Reprinted);
        Assert.Contains(_log, l => l.Contains("duplicate"));
    }

    [Fact]
    public void AtStartWithNothingAtThePortEveryUnaccountedJobIsPrintedAgain()
    {
        _store.Add(10, 1);
        _store.Add(11, 2);
        var r = Make().RecoverAtStart(leftoverAtPort: false, NonePending);
        Assert.Equal(["restart 10", "restart 11"], _store.Calls);
        Assert.Equal(2, r.Reprinted);
    }

    [Fact]
    public void AtStartAJobTheRunningCheckAlreadyFollowsIsLeftToIt()
    {
        _store.Add(10, 1);
        _store.Add(11, 2);
        var r = Make().RecoverAtStart(leftoverAtPort: false, id => id == 11);
        Assert.Equal(["restart 10"], _store.Calls);
        Assert.Equal(1, r.Reprinted);
    }

    [Fact]
    public void AJobThatCannotBePrintedAgainIsReportedAndNeverReportedTwice()
    {
        // Another user's document: this user may not restart or delete it. Reported once, remembered in the ledger.
        _store.Add(10, 1, doc: "invoice", user: "bob");
        _store.RestartFails = _ => new Win32Exception(5);
        _store.DeleteFails = _ => new Win32Exception(5);
        var r = Make().RecoverAtStart(leftoverAtPort: false, NonePending);
        var job = Assert.Single(r.Unrecovered);
        Assert.Equal("invoice10", job.Document);
        Assert.Equal(0, r.Reprinted);
        Assert.True(File.Exists(LedgerPath));

        var again = Make().RecoverAtStart(leftoverAtPort: false, NonePending); // next start, same ledger
        Assert.Empty(again.Unrecovered);
        Assert.Equal(["restart 10", "delete 10"], _store.Calls);
    }

    [Fact]
    public void LedgerEntriesForJobsNoLongerKeptAreDropped()
    {
        _store.Add(10, 1);
        _store.DeleteFails = _ => new Win32Exception(5);
        var rec = Make();
        rec.Claimed(10);
        rec.Drain();
        Assert.Contains("10", File.ReadAllText(LedgerPath));
        _store.Jobs.Clear(); // someone with the right deleted it
        Make().RecoverAtStart(leftoverAtPort: false, NonePending);
        Assert.DoesNotContain("10", File.ReadAllText(LedgerPath));
    }

    [Fact]
    public void AReusedJobIdIsNotMistakenForALedgerEntry()
    {
        // Job ids restart after a spooler reset; the ledger keys on id and submit time.
        var old = _store.Add(10, 1);
        _store.DeleteFails = _ => new Win32Exception(5);
        var rec = Make();
        rec.Claimed(10);
        rec.Drain();
        _store.Jobs.Clear();
        _store.Jobs.Add(old with { Submitted = old.Submitted.AddDays(1) });
        _store.DeleteFails = _ => null;
        var r = Make().RecoverAtStart(leftoverAtPort: false, NonePending);
        Assert.Equal(1, r.Reprinted);
    }

    [Fact]
    public void AClaimedJobIsDeletedWhenDrained()
    {
        _store.Add(10, 1);
        var rec = Make();
        rec.Claimed(10);
        Assert.Empty(_store.Calls); // never on the watcher's thread
        rec.Drain();
        Assert.Equal(["delete 10"], _store.Calls);
        Assert.False(File.Exists(LedgerPath));
    }

    [Fact]
    public void AClaimedJobAlreadyGoneIsNotAnError()
    {
        _store.DeleteFails = _ => new Win32Exception(87); // ERROR_INVALID_PARAMETER: no such job
        var rec = Make();
        rec.Claimed(10);
        rec.Drain();
        Assert.False(File.Exists(LedgerPath));
    }

    [Fact]
    public void ALostJobIsPrintedAgainAtMostTwiceThenReported()
    {
        _store.Add(10, 1);
        var rec = Make();
        Assert.True(rec.Lost(10));
        _store.Add(10, 1); // printed again, and replaced again
        Assert.True(rec.Lost(10));
        _store.Add(10, 1);
        Assert.False(rec.Lost(10)); // give up: the caller reports it
        Assert.Equal(["restart 10", "restart 10", "delete 10"], _store.Calls);
    }

    [Fact]
    public void ALostJobThatIsNotKeptIsReported()
    {
        // KEEPPRINTEDJOBS off (an older install): nothing to print again, the caller shows the loss balloon.
        var rec = Make();
        Assert.False(rec.Lost(10));
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public void ALostJobThatCannotBeRestartedIsReported()
    {
        _store.Add(10, 1);
        _store.RestartFails = _ => new Win32Exception(5);
        var rec = Make();
        Assert.False(rec.Lost(10));
        Assert.Equal(["restart 10", "delete 10"], _store.Calls);
    }

    [Fact]
    public void SweepDeletesJobsNobodyFollowedWhenEachHasAClaim()
    {
        // The loss check missed them (discarded notifications, not running): as many files were claimed as they
        // are, so none was replaced.
        _store.Add(10, 1);
        _store.Add(11, 2);
        _store.Add(12, 3);
        var rec = Make();
        rec.ClaimUnpaired(_now);
        rec.ClaimUnpaired(_now);
        rec.Sweep(id => id == 12);
        Assert.Empty(_store.Calls);
        _now = _now.AddSeconds(59);
        rec.Sweep(id => id == 12);
        Assert.Empty(_store.Calls);
        _now = _now.AddSeconds(2);
        Assert.Empty(rec.Sweep(id => id == 12));
        Assert.Equal(["delete 10", "delete 11"], _store.Calls);
    }

    [Fact]
    public void SweepPrintsAgainJobsNobodyFollowedWhenTheClaimsDoNotAddUp()
    {
        // Two jobs, one claimed file: one was replaced, and which one cannot be told. Both are printed again
        // (a duplicate rather than a silent loss); nothing is deleted.
        _store.Add(10, 1);
        _store.Add(11, 2);
        var rec = Make();
        rec.ClaimUnpaired(_now);
        rec.Sweep(NonePending);
        _now = _now.AddSeconds(61);
        Assert.Empty(rec.Sweep(NonePending));
        Assert.Equal(["restart 10", "restart 11"], _store.Calls);
        Assert.Contains(_log, l => l.Contains("printed again"));
    }

    [Fact]
    public void SweepDecidesOnlyOnceEveryJobNobodyFollowedHasSettled()
    {
        // Deciding on the first job alone would spend the one claim on it, though it may be the second's file.
        _store.Add(10, 1);
        var rec = Make();
        rec.ClaimUnpaired(_now);
        rec.Sweep(NonePending);
        _now = _now.AddSeconds(30);
        _store.Add(11, 2);
        rec.Sweep(NonePending);
        _now = _now.AddSeconds(31);
        rec.Sweep(NonePending);
        Assert.Empty(_store.Calls);
        _now = _now.AddSeconds(30);
        rec.Sweep(NonePending);
        Assert.Equal(["restart 10", "restart 11"], _store.Calls);
    }

    [Fact]
    public void SweepReportsJobsItCannotPrintAgain()
    {
        _store.Add(10, 1, doc: "invoice", user: "bob");
        _store.RestartFails = _ => new Win32Exception(5);
        _store.DeleteFails = _ => new Win32Exception(5);
        var rec = Make();
        rec.Sweep(NonePending);
        _now = _now.AddSeconds(61);
        var job = Assert.Single(rec.Sweep(NonePending));
        Assert.Equal("invoice10", job.Document);
        _now = _now.AddSeconds(61);
        Assert.Empty(rec.Sweep(NonePending)); // remembered: reported once
    }

    [Fact]
    public void AClaimNoJobNeededIsForgottenAndDoesNotCoverALaterLoss()
    {
        var rec = Make();
        rec.ClaimUnpaired(_now); // e.g. a file dropped at the port by hand
        _now = _now.AddSeconds(61);
        rec.Sweep(NonePending);
        _store.Add(10, 1);
        rec.Sweep(NonePending);
        _now = _now.AddSeconds(61);
        rec.Sweep(NonePending);
        Assert.Equal(["restart 10"], _store.Calls);
    }

    [Fact]
    public void AtExitJobsNobodyFollowedAreDeletedWhenEachHasAClaim()
    {
        // Without this, a quick Exit after printing would print them all again at the next start.
        _store.Add(10, 1);
        _store.Add(11, 2);
        var rec = Make();
        rec.ClaimUnpaired(_now);
        rec.ClaimUnpaired(_now);
        rec.SettleAtExit(NonePending);
        Assert.Equal(["delete 10", "delete 11"], _store.Calls);
    }

    [Fact]
    public void AtExitJobsNobodyFollowedAreLeftForTheNextStartWhenTheClaimsDoNotAddUp()
    {
        _store.Add(10, 1);
        _store.Add(11, 2);
        var rec = Make();
        rec.ClaimUnpaired(_now);
        rec.SettleAtExit(id => id == 12);
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public void AtStartJobsEachWithAClaimSinceStartAreDeletedNotPrintedAgain()
    {
        // The pause was refused, so the queue kept printing while the leftover was claimed: two more jobs printed
        // and were claimed before the loss check started. Three jobs, three claims: nothing was replaced.
        _store.Add(10, 1);
        _store.Add(11, 2);
        _store.Add(12, 3);
        var rec = Make();
        for (int i = 0; i < 3; i++) rec.ClaimUnpaired(_now);
        var r = rec.RecoverAtStart(leftoverAtPort: true, NonePending);
        Assert.Equal(["delete 10", "delete 11", "delete 12"], _store.Calls);
        Assert.Equal(0, r.Reprinted);
        _store.Add(13, 4); // the claims were spent at start: a later job is not covered by them
        _now = _now.AddSeconds(1);
        rec.Sweep(NonePending);
        _now = _now.AddSeconds(61);
        rec.Sweep(NonePending);
        Assert.Equal("restart 13", _store.Calls[^1]);
    }

    [Fact]
    public void ReprintSaysWhetherTheJobWasKeptAndWhetherItPrintsAgain()
    {
        _store.Add(10, 1);
        _store.Add(11, 2, user: "bob");
        _store.RestartFails = id => id == 11 ? new Win32Exception(5) : null;
        var rec = Make();
        Assert.Equal(ReprintOutcome.PrintingAgain, rec.Reprint(10));
        Assert.Equal(ReprintOutcome.Refused, rec.Reprint(11));
        Assert.Equal(ReprintOutcome.NotKept, rec.Reprint(12));
    }

    [Fact]
    public void SweepDrainsClaimedJobs()
    {
        _store.Add(10, 1);
        var rec = Make();
        rec.Claimed(10);
        rec.Sweep(NonePending);
        Assert.Equal(["delete 10"], _store.Calls);
    }

    [Fact]
    public void JobsItDeletesItselfAreKnownBeforeTheSpoolerReportsThem()
    {
        // Deleting a kept job printed long ago makes the spooler report PRINTED|DELETING for it; a job source that
        // never saw it print counts that as a finish, and the loss check would call it replaced 15 s later
        // (seen live: probe --crash, the leftover's owner).
        _store.Add(10, 1, pages: 1);
        _store.Add(11, 2, pages: 2);
        var seenDuringDelete = new List<bool>();
        var rec = Make();
        _store.DeleteFails = id => { seenDuringDelete.Add(rec.DeletedHere(id)); return null; };
        rec.RecoverAtStart(leftoverAtPort: true, NonePending, leftoverPages: 2);
        Assert.Equal([true], seenDuringDelete);
        Assert.True(rec.DeletedHere(11));
        Assert.False(rec.DeletedHere(10)); // printed again, not deleted
        rec.Claimed(10);
        rec.Drain();
        Assert.True(rec.DeletedHere(10));
    }

    [Fact]
    public void ACorruptLedgerIsLoggedAndTreatedAsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LedgerPath, "{ not json");
        _store.Add(10, 1);
        var r = Make().RecoverAtStart(leftoverAtPort: false, NonePending);
        Assert.Equal(1, r.Reprinted);
        Assert.Contains(_log, l => l.Contains("ledger"));
    }

    [Fact]
    public void AStoreThatCannotBeReadIsLoggedNotThrown()
    {
        var rec = new KeptJobRecovery(new ThrowingStore(), new KeptJobLedger(LedgerPath), () => _now) { Log = _log.Add };
        var r = rec.RecoverAtStart(leftoverAtPort: true, NonePending);
        Assert.Equal(0, r.Reprinted);
        rec.Sweep(NonePending);
        Assert.False(rec.Lost(3));
        Assert.Contains(_log, l => l.Contains("1801") || l.Contains("printer", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ThrowingStore : IKeptJobStore
    {
        public IReadOnlyList<KeptJob> Printed() => throw new Win32Exception(1801);
        public void Delete(int id) => throw new Win32Exception(1801);
        public void Restart(int id) => throw new Win32Exception(1801);
    }
}

/// <summary>
/// The spooler reports a job's status many times; "finished" fires once per printing of a job: PRINTED then
/// PRINTED|DELETED is one finish, a job printed again (restarted) finishes again, and the id set stays bounded
/// without ever forgetting a job it may still hear about.
/// </summary>
public class FinishedJobsTests
{
    private const uint Printing = 0x10, Printed = 0x80, Deleting = 0x4, Deleted = 0x100;

    [Fact]
    public void PrintedThenPrintedDeletedIsOneFinish()
    {
        var f = new FinishedJobs();
        Assert.False(f.OnStatus(1, Printing));
        Assert.True(f.OnStatus(1, Printed | Deleting));
        Assert.False(f.OnStatus(1, Printed | Deleting | Deleted));
    }

    [Fact]
    public void OnlyTheDeletedStatusSeenIsStillOneFinish()
    {
        // Notifications coalesce: the first status seen may already carry DELETED.
        var f = new FinishedJobs();
        Assert.True(f.OnStatus(1, Printed | Deleted));
        Assert.False(f.OnStatus(1, Printed | Deleted));
    }

    [Fact]
    public void ARestartedJobFinishesAgain()
    {
        var f = new FinishedJobs();
        Assert.True(f.OnStatus(5, Printed));
        Assert.False(f.OnStatus(5, Printed));
        Assert.False(f.OnStatus(5, Printing)); // restarted
        Assert.True(f.OnStatus(5, Printed));
    }

    [Fact]
    public void ManyJobsNeverFinishTwiceAndTheSetStaysSmall()
    {
        // The old set was cleared at 4096 ids, so the next PRINTED|DELETED of the job that crossed it fired again.
        var f = new FinishedJobs();
        int fired = 0;
        for (int id = 1; id <= 10_000; id++)
        {
            if (f.OnStatus(id, Printed | Deleting)) fired++;
            if (f.OnStatus(id, Printed | Deleting | Deleted)) fired++;
        }
        Assert.Equal(10_000, fired);
        Assert.Equal(0, f.Count);
    }

    [Fact]
    public void ARefreshReportsKeptJobsWithoutFinishingThem()
    {
        // After discarded notifications the next read is a full refresh: every kept job in the queue is reported
        // with its current (printed) status. Those finished long ago (another user's, remembered in the ledger)
        // are not new finishes; counting them made the loss check call them replaced 15 s later.
        var f = new FinishedJobs();
        Assert.False(f.OnStatus(1, Printed, refresh: true));
        Assert.False(f.OnStatus(1, Printed));
        Assert.False(f.OnStatus(1, 0x800)); // restarted
        Assert.True(f.OnStatus(1, Printed));
    }

    [Fact]
    public void ARestartedKeptJobFinishesAgainWithTheStatusesMeasuredLive()
    {
        // Probe -Crash -ShowStatuses, 2026-09-30: a restarted kept job reports 0x800 (RESTART, no PRINTED), then
        // 0x0, 0x10, 0x2010 while printing, then 0x80 and 0x84/0x184 when it is deleted.
        var f = new FinishedJobs();
        Assert.True(f.OnStatus(88, Printed));
        uint[] again = [0x800, 0x800, 0x0, 0x10, 0x2010, 0x2010, 0x0];
        foreach (var st in again) Assert.False(f.OnStatus(88, st));
        Assert.True(f.OnStatus(88, Printed));
        Assert.False(f.OnStatus(88, Printed));
        Assert.False(f.OnStatus(88, Printed | Deleting));
        Assert.False(f.OnStatus(88, Printed | Deleting | Deleted));
    }

    [Fact]
    public void KeptJobsBeyondTheBoundForgetOnlyTheOldest()
    {
        var f = new FinishedJobs();
        for (int id = 1; id <= FinishedJobs.Capacity + 100; id++) Assert.True(f.OnStatus(id, Printed));
        Assert.True(f.Count <= FinishedJobs.Capacity);
        Assert.False(f.OnStatus(FinishedJobs.Capacity + 100, Printed)); // the newest are still known
    }
}
