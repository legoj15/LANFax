using System.Text;
using LANFax.Core;

namespace LANFax.Tests;

public class SpoolWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-spool-" + Guid.NewGuid().ToString("N"));
    private readonly List<(Guid id, string path, DateTime at)> _events = new();
    private readonly List<(string reason, Exception error)> _errors = new();

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private readonly List<string> _claimed = new();

    /// <summary>Production retry cadence (10 ms while the file is locked); the idle poll shortened from 2 s.</summary>
    private SpoolWatcher Make()
    {
        var w = new SpoolWatcher(_dir)
        {
            RetryEvery = TimeSpan.FromMilliseconds(10),
            PollEvery = TimeSpan.FromMilliseconds(400),
        };
        w.NewPrintJob += (id, p) => { lock (_events) _events.Add((id, p, DateTime.UtcNow)); };
        w.Error += (r, e) => { lock (_errors) _errors.Add((r, e)); };
        w.Claimed += p => { lock (_claimed) _claimed.Add(p); };
        return w;
    }

    private const int SharingViolation = unchecked((int)0x80070020);

    private string RejectedDir => Path.Combine(_dir, "rejected");

    private int Count { get { lock (_events) return _events.Count; } }
    private int ErrorCount { get { lock (_errors) return _errors.Count; } }

    /// <summary>
    /// A minimal complete file: the %PDF header, padding to <paramref name="len"/> bytes, then one <c>%%EOF</c>.
    /// </summary>
    private static byte[] Pdf(int len = 16, byte fill = (byte)'x')
    {
        var b = Enumerable.Repeat(fill, Math.Max(len, 5)).ToArray();
        Encoding.ASCII.GetBytes("%PDF-").CopyTo(b, 0);
        return [.. b, .. Encoding.ASCII.GetBytes("\n%%EOF\n")];
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 15000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException("condition not met"); await Task.Delay(25); }
    }

    [Fact]
    public async Task MissingDirIsCreatedAndFileIsMoved()
    {
        Assert.False(Directory.Exists(_dir));
        using var w = Make();
        w.Start();
        Assert.True(Directory.Exists(_dir));
        var src = Path.Combine(_dir, "print.pdf");
        var pdf = Pdf(40);
        File.WriteAllBytes(src, pdf);
        await WaitFor(() => Count == 1);
        var (id, path, _) = _events[0];
        Assert.Equal(Path.Combine(_dir, "jobs", id.ToString() + ".pdf"), path);
        Assert.Equal(pdf, File.ReadAllBytes(path));
        Assert.False(File.Exists(src));
        await Task.Delay(1000); // two more polls: nothing new
        Assert.Equal(1, Count);
        Assert.Equal(0, ErrorCount);
    }

    [Fact]
    public async Task LockedAndGrowingFileWaitsUntilReleased()
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        DateTime release;
        using (var fs = new FileStream(src, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            fs.Write(Encoding.ASCII.GetBytes("%PDF-"));
            for (int i = 0; i < 6; i++)
            {
                fs.Write(new byte[1000]);
                fs.Flush();
                await Task.Delay(150);
            }
            fs.Write(Encoding.ASCII.GetBytes("%%EOF\n"));
            Assert.Equal(0, Count); // still locked after several age waits and rechecks
            release = DateTime.UtcNow;
        }
        await WaitFor(() => Count == 1);
        Assert.True(_events[0].at >= release, "raised before the writer released the file");
        Assert.Equal(6011, new FileInfo(_events[0].path).Length);
        await Task.Delay(1000);
        Assert.Equal(1, Count);
    }

    [Fact]
    public async Task SecondJobAfterFirstIsHandled()
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        File.WriteAllBytes(src, Pdf(10, (byte)'1'));
        await WaitFor(() => Count == 1);
        File.WriteAllBytes(src, Pdf(12, (byte)'2'));
        await WaitFor(() => Count == 2);
        Assert.NotEqual(_events[0].id, _events[1].id);
        Assert.Equal(Pdf(12, (byte)'2'), File.ReadAllBytes(_events[1].path));
        Assert.Equal(Pdf(10, (byte)'1'), File.ReadAllBytes(_events[0].path));
    }

    [Fact]
    public async Task FileDeletedWhileBeingWrittenIsTolerated()
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        using (var fs = new FileStream(src, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete))
        {
            fs.Write(Pdf());
            await Task.Delay(100); // the watcher is retrying against the writer's handle
            File.Delete(src); // delete pending until the writer closes
        }
        await Task.Delay(500);
        Assert.Equal(0, Count);
        Assert.Equal(0, ErrorCount);
        File.WriteAllBytes(src, Pdf(20));
        await WaitFor(() => Count == 1);
    }

    [Fact]
    public void StopIsIdempotent()
    {
        var w = Make();
        w.Stop();
        w.Start();
        w.Stop();
        w.Stop();
        w.Start(); // restart after Stop works (the old CTS was disposed, a new one is made)
        w.Stop();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NonPdfIsKeptAsideWithErrorAndNotRaised(int kind)
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        var bytes = kind == 0 ? Array.Empty<byte>() : Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 postscript");
        File.WriteAllBytes(src, bytes);
        await WaitFor(() => ErrorCount >= 1);
        await WaitFor(() => !File.Exists(src), 3000);
        Assert.Equal(0, Count);
        Assert.Contains("not a PDF", _errors[0].reason);
        Assert.Empty(Directory.GetFiles(w.JobsDir));
        // never deleted: whatever reached the port is kept for inspection
        var kept = Assert.Single(Directory.GetFiles(RejectedDir));
        Assert.Equal(bytes, File.ReadAllBytes(kept));
        Assert.Equal(kept, Assert.Single(_claimed));
    }

    [Fact]
    public async Task PermanentMoveFailureRaisesErrorOnceAndNoJob()
    {
        var logs = new List<string>();
        using var w = Make();
        w.Log = m => { lock (logs) logs.Add(m); };
        w.Start();
        Directory.Delete(w.JobsDir);
        File.WriteAllText(w.JobsDir, "a file where the jobs dir should be");
        File.WriteAllBytes(w.PrintPath, Pdf());
        await WaitFor(() => ErrorCount >= 1);
        await Task.Delay(1500); // the fast retries and several polls: still one Error (rate-limited per reason)
        Assert.Equal(1, ErrorCount);
        Assert.Equal(0, Count);
        Assert.True(File.Exists(w.PrintPath)); // the job is not lost
        Assert.Contains("move", _errors[0].reason, StringComparison.OrdinalIgnoreCase);

        // once the cause is fixed the job goes through
        File.Delete(w.JobsDir);
        Directory.CreateDirectory(w.JobsDir);
        await WaitFor(() => Count == 1);
        Assert.Equal(1, ErrorCount);
    }

    [Fact]
    public async Task AFailingMoveKeepsThePortFileLockedUntilTheMoveWorks()
    {
        // jobs\ unwritable during a burst: releasing print.pdf between retries would let the spooler's next job
        // overwrite it. The claim is kept and only the rename is retried, past the fast retries into the slow ones.
        using var w = Make();
        w.Start();
        Directory.Delete(w.JobsDir);
        File.WriteAllText(w.JobsDir, "a file where the jobs dir should be");
        var pdf = Pdf(30);
        File.WriteAllBytes(w.PrintPath, pdf);
        await WaitFor(() => ErrorCount >= 1);
        int opened = 0, tries = 0;
        var end = DateTime.UtcNow.AddMilliseconds(2000); // 250 ms x 5 fast retries, then the 400 ms poll
        while (DateTime.UtcNow < end)
        {
            tries++;
            try
            {
                using var _ = new FileStream(w.PrintPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                opened++;
            }
            catch (IOException) { }
            await Task.Delay(5);
        }
        Assert.True(tries > 20, $"only {tries} tries"); // Task.Delay(5) is ~15 ms on Windows, slower under a loaded suite
        Assert.Equal(0, opened);
        File.Delete(w.JobsDir);
        Directory.CreateDirectory(w.JobsDir);
        await WaitFor(() => Count == 1);
        Assert.Equal(pdf, File.ReadAllBytes(_events[0].path));
    }

    [Fact]
    public async Task AFileCreatedAsTheCheckEndsIsNotLeftForThePoll()
    {
        // The loop ends when print.pdf is gone; a Created event in the gap before it is marked idle used to be
        // dropped, leaving the next job to the (2 s) poll, far past the next job's overwrite.
        var w = new SpoolWatcher(_dir) { RetryEvery = TimeSpan.FromMilliseconds(10), PollEvery = TimeSpan.FromHours(1) };
        w.NewPrintJob += (id, p) => { lock (_events) _events.Add((id, p, DateTime.UtcNow)); };
        int once = 0;
        w.LoopEnded = () =>
        {
            if (Count != 1 || Interlocked.Exchange(ref once, 1) != 0) return;
            File.WriteAllBytes(w.PrintPath, Pdf(20, (byte)'2'));
            Thread.Sleep(300); // the watcher's events for it arrive while the loop still counts as running
        };
        using (w)
        {
            w.Start();
            await Task.Delay(200); // the start-up check ran and ended
            File.WriteAllBytes(w.PrintPath, Pdf(20, (byte)'1'));
            await WaitFor(() => Count == 2, 3000);
        }
    }

    [Fact]
    public async Task StopWhileAJobIsBeingWrittenLeavesItAlone()
    {
        var w = Make();
        w.Start();
        using (var fs = new FileStream(w.PrintPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            fs.Write(Pdf());
            await Task.Delay(100); // the watcher is retrying against the writer's handle
            w.Stop();
        }
        await Task.Delay(500);
        Assert.Equal(0, Count);
        Assert.Equal(0, ErrorCount);
        Assert.True(File.Exists(w.PrintPath));
        Assert.Empty(Directory.GetFiles(w.JobsDir));
    }

    // ---- concatenated or truncated print files ----

    [Fact]
    public async Task TwoDocumentsInOneFileAreRejectedNotSent()
    {
        using var w = Make();
        w.Start();
        byte[] two = [.. Pdf(30, (byte)'1'), .. Pdf(30, (byte)'2')];
        File.WriteAllBytes(w.PrintPath, two);
        await WaitFor(() => ErrorCount >= 1);
        await WaitFor(() => !File.Exists(w.PrintPath), 3000);
        Assert.Equal(0, Count);
        Assert.Empty(Directory.GetFiles(w.JobsDir));
        Assert.Equal("print.pdf contained 2 documents; the LAN Fax printer received two jobs at once. Print them one at a time.",
            _errors[0].reason);
        var kept = Assert.Single(Directory.GetFiles(RejectedDir));
        Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(kept), out _));
        Assert.Equal(".pdf", Path.GetExtension(kept));
        Assert.Equal(two, File.ReadAllBytes(kept));

        // the next single document goes through normally
        File.WriteAllBytes(w.PrintPath, Pdf(20));
        await WaitFor(() => Count == 1);
    }

    [Fact]
    public async Task FileWithoutEofIsRejectedWithoutReleasingIt()
    {
        // A successful exclusive open proves the spooler closed the file, so a PDF with no %%EOF is truncated: it is
        // kept aside at once, and the port stays locked until it is gone (no gap for the next job to overwrite it).
        using var w = Make();
        Exception? seen = null;
        w.BeforeMove = () =>
        {
            try { using var _ = new FileStream(w.PrintPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception e) { seen = e; }
        };
        w.Start();
        var truncated = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n");
        File.WriteAllBytes(w.PrintPath, truncated);
        await WaitFor(() => ErrorCount >= 1, 3000);
        Assert.False(File.Exists(w.PrintPath));
        Assert.Equal(SharingViolation, Assert.IsType<IOException>(seen).HResult);
        Assert.Equal(0, Count);
        Assert.Contains("not a PDF", _errors[0].reason);
        Assert.Contains("%%EOF", _errors[0].reason);
        Assert.Equal(truncated, File.ReadAllBytes(Assert.Single(Directory.GetFiles(RejectedDir))));
        Assert.Single(_claimed);
    }

    [Fact]
    public async Task APrintFileThatCannotBeOpenedIsReportedNotRetriedSilently()
    {
        // Access denied never clears on its own (a spool dir whose ACL the tray user lacks): after a while it is an
        // error, so the user hears about it instead of every later document overwriting this one unseen.
        using var w = Make();
        w.AccessDeniedReportAfter = TimeSpan.FromMilliseconds(300);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(w.PrintPath, Pdf(20));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var fi = new FileInfo(w.PrintPath);
        var acl = fi.GetAccessControl();
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ReadData,
            System.Security.AccessControl.AccessControlType.Deny);
        acl.AddAccessRule(deny);
        fi.SetAccessControl(acl);
        try
        {
            w.Start();
            await WaitFor(() => ErrorCount >= 1, 5000);
            Assert.Contains("access", _errors[0].reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, Count);
            Assert.True(File.Exists(w.PrintPath)); // never deleted or moved
        }
        finally
        {
            w.Stop();
            acl.RemoveAccessRule(deny);
            fi.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task WaitIdleReturnsOnceALeftoverIsClaimed()
    {
        // A print.pdf left from the last run is claimed before the app resumes the queue, not in a race with the
        // first held job.
        Directory.CreateDirectory(_dir);
        var pdf = Pdf(30);
        using var w = Make();
        File.WriteAllBytes(w.PrintPath, pdf);
        w.Start();
        Assert.True(w.WaitIdle(TimeSpan.FromSeconds(5)));
        Assert.False(File.Exists(w.PrintPath));
        Assert.Equal(1, Count);
        Assert.Equal(pdf, File.ReadAllBytes(_events[0].path));
        await Task.CompletedTask;
    }

    [Fact]
    public void WaitIdleGivesUpWhileTheFileIsStillBeingWritten()
    {
        using var w = Make();
        w.Start();
        using var writer = new FileStream(w.PrintPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(Pdf(10));
        Assert.False(w.WaitIdle(TimeSpan.FromMilliseconds(300)));
    }

    // ---- claim and hold: the file never leaves the watcher's exclusive handle until it is in jobs\ ----

    [Fact]
    public async Task ASecondWriterIsLockedOutWhileTheFileIsClaimed()
    {
        using var w = Make();
        Exception? seen = null;
        w.BeforeMove = () =>
        {
            // the spooler's next job opening the port path while this one is being checked
            try { using var _ = new FileStream(w.PrintPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception e) { seen = e; }
        };
        w.Start();
        var pdf = Pdf(40, (byte)'y');
        File.WriteAllBytes(w.PrintPath, pdf);
        await WaitFor(() => Count == 1);
        var io = Assert.IsType<IOException>(seen);
        Assert.Equal(SharingViolation, io.HResult);
        Assert.Equal(pdf, File.ReadAllBytes(_events[0].path));
    }

    [Fact]
    public async Task TheFileIsRenamedIntoJobsWhileStillHeld()
    {
        using var w = Make();
        bool? portGone = null;
        int? destLocked = null;
        w.AfterMove = dest =>
        {
            portGone = !File.Exists(w.PrintPath);
            try { using var _ = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); destLocked = 0; }
            catch (IOException e) { destLocked = e.HResult; }
        };
        w.Start();
        File.WriteAllBytes(w.PrintPath, Pdf(40));
        await WaitFor(() => Count == 1);
        Assert.True(portGone, "print.pdf still existed after the rename");
        Assert.Equal(SharingViolation, destLocked); // same handle, never released between the checks and the move
        Assert.Equal(_events[0].path, Assert.Single(_claimed));
    }

    [Fact]
    public async Task StopAndWaitReturnsOnlyOnceAClaimInProgressHasBeenReported()
    {
        // Exit stops the watcher, then settles the kept jobs: a claim loop already past its last cancellation check
        // must finish (and report its claim) first, or the kept job of a file already in jobs\ is printed again at
        // the next start.
        using var w = Make();
        using var inMove = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        w.AfterMove = _ => { inMove.Set(); go.Wait(TimeSpan.FromSeconds(10)); };
        w.Start();
        File.WriteAllBytes(w.PrintPath, Pdf(40));
        Assert.True(inMove.Wait(TimeSpan.FromSeconds(10)));
        var stop = Task.Run(() => w.StopAndWait(TimeSpan.FromSeconds(10)));
        await Task.Delay(200);
        Assert.False(stop.IsCompleted, "StopAndWait returned while the claim was still in progress");
        go.Set();
        Assert.True(await stop);
        lock (_claimed) Assert.Single(_claimed);
    }

    [Fact]
    public async Task ACrashWhileHoldingLeavesThePrintFileInPlace()
    {
        var w = Make();
        int hooked = 0;
        w.BeforeMove = () =>
        {
            if (Interlocked.Increment(ref hooked) != 1) return;
            w.Stop(); // nothing after this point runs for the claim: as if the process died holding the handle
            throw new InvalidOperationException("simulated crash");
        };
        w.Start();
        var pdf = Pdf(40);
        File.WriteAllBytes(w.PrintPath, pdf);
        await WaitFor(() => Volatile.Read(ref hooked) >= 1);
        await Task.Delay(300);
        Assert.Equal(0, Count);
        Assert.True(File.Exists(w.PrintPath), "the only copy was deleted");
        Assert.Equal(pdf, File.ReadAllBytes(w.PrintPath)); // and the handle is closed again
        Assert.Empty(Directory.GetFiles(w.JobsDir));
        w.Dispose();
    }

    [Fact]
    public void ClaimFailsWhileAnotherHandleIsOpenAndSucceedsAfter()
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, "print.pdf");
        File.WriteAllBytes(p, Pdf());
        using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Assert.Null(ClaimedFile.TryOpen(p));
        Assert.Null(ClaimedFile.TryOpen(Path.Combine(_dir, "missing.pdf")));
        using var c = ClaimedFile.TryOpen(p);
        Assert.NotNull(c);
        var dest = Path.Combine(_dir, "moved.pdf");
        c!.RenameTo(dest);
        Assert.False(File.Exists(p));
        Assert.True(File.Exists(dest));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("%PDF-1.7 %%EOF", 1)]
    [InlineData("%%EOF%%EOF", 2)]
    [InlineData("%%EO F %EOF %%eof", 0)]
    public void CountsEofMarkers(string content, int expected) =>
        Assert.Equal(expected, SpoolWatcher.CountEof(new MemoryStream(Encoding.ASCII.GetBytes(content))));

    [Fact]
    public void CountsEofMarkersAcrossBufferBoundaries()
    {
        foreach (var at in new[] { 65536 - 4, 65536 - 2, 65536, 65536 * 2 - 1 })
        {
            var b = Enumerable.Repeat((byte)'x', 65536 * 3).ToArray();
            Encoding.ASCII.GetBytes("%%EOF").CopyTo(b, at);
            Assert.Equal(1, SpoolWatcher.CountEof(new MemoryStream(b)));
        }
    }
}
