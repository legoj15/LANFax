using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using LANFax.Core;

namespace LANFax.Tests;

/// <summary>
/// Against a real named pipe with a unique name per test. The client plays the spooler: it connects, writes one
/// job, and closes (end of stream is the end of the job).
/// </summary>
public class PrintPipeListenerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-pipe-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "LANFaxTest-" + Guid.NewGuid().ToString("N");
    private readonly List<(Guid id, string path)> _events = new();
    private readonly List<(string reason, Exception error)> _errors = new();
    private readonly List<PrintPipeListener> _made = new();

    public void Dispose()
    {
        foreach (var l in _made) l.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private PrintPipeListener Make(string? pipe = null)
    {
        var l = new PrintPipeListener(_dir, pipe ?? _pipe) { Log = m => Console.Error.WriteLine("LOG " + m) };
        l.NewPrintJob += (id, p) => { lock (_events) _events.Add((id, p)); };
        l.Error += (r, e) => { lock (_errors) _errors.Add((r, e)); };
        _made.Add(l);
        return l;
    }

    private int Count { get { lock (_events) return _events.Count; } }
    private int ErrorCount { get { lock (_errors) return _errors.Count; } }
    private string JobsDir => Path.Combine(_dir, "jobs");
    private string RejectedDir => Path.Combine(_dir, "rejected");

    /// <summary>A minimal complete PDF: the header, <paramref name="len"/> bytes of <paramref name="fill"/>, one <c>%%EOF</c>.</summary>
    private static byte[] Pdf(int len = 16, byte fill = (byte)'x') =>
        [.. "%PDF-1.7\n"u8.ToArray(), .. Enumerable.Repeat(fill, len), .. "\n%%EOF\n"u8.ToArray()];

    private static async Task WaitFor(Func<bool> cond, int ms = 15000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException("condition not met"); await Task.Delay(20); }
    }

    private NamedPipeClientStream Connect(string? pipe = null)
    {
        var c = new NamedPipeClientStream(".", pipe ?? _pipe, PipeDirection.Out, PipeOptions.Asynchronous);
        c.Connect(5000);
        return c;
    }

    /// <summary>One job as the spooler sends it: connect, write, close.</summary>
    private void Send(byte[] bytes, string? pipe = null)
    {
        using var c = Connect(pipe);
        c.Write(bytes);
        c.Flush();
    }

    private static string[] Files(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir) : [];

    [Fact]
    public async Task OneJobLandsInJobsDirAndIsRaised()
    {
        Assert.False(Directory.Exists(_dir));
        var l = Make();
        l.Start();
        Assert.True(l.IsListening);
        var pdf = Pdf(40_000);
        Send(pdf);
        await WaitFor(() => Count == 1);
        var (id, path) = _events[0];
        Assert.Equal(Path.Combine(JobsDir, id + ".pdf"), path);
        Assert.Equal(pdf, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(JobsDir, "*.part"));
        Assert.Equal(0, ErrorCount);
    }

    [Fact]
    public async Task ReceivingIsSignalledWhileAJobArrivesAndClearedOnceItIsSaved()
    {
        // The tray blocks Windows shutdown while a job is in flight: the signal must go up when a client is connected
        // and writing, stay up until the file is renamed and raised, and come down once idle.
        var l = Make();
        var changes = new List<bool>();
        var raisedWhileReceiving = new List<bool>();
        l.ReceivingChanged += r => { lock (changes) changes.Add(r); };
        l.NewPrintJob += (_, _) => { lock (raisedWhileReceiving) raisedWhileReceiving.Add(l.IsReceiving); };
        l.Start();
        Assert.False(l.IsReceiving);
        var pdf = Pdf(1000);
        using (var c = Connect())
        {
            c.Write(pdf, 0, 100);
            c.Flush();
            await WaitFor(() => l.IsReceiving);
            lock (changes) Assert.Equal([true], changes);
            c.Write(pdf, 100, pdf.Length - 100);
            c.Flush();
            Assert.True(l.IsReceiving); // the client has not closed: the job is still arriving
        }
        await WaitFor(() => Count == 1);
        await WaitFor(() => !l.IsReceiving);
        lock (changes) Assert.Equal([true, false], changes);
        Assert.Equal([true], raisedWhileReceiving); // still "in flight" while the job is renamed and raised
        Assert.Equal(pdf, File.ReadAllBytes(_events[0].path));
    }

    [Fact]
    public async Task ReceivingGoesUpAndDownOncePerJob()
    {
        var l = Make();
        var changes = new List<bool>();
        l.ReceivingChanged += r => { lock (changes) changes.Add(r); };
        l.Start();
        Send(Pdf(10));
        await WaitFor(() => Count == 1);
        await WaitFor(() => { lock (changes) return changes.Count == 2; });
        Send(Pdf(20));
        await WaitFor(() => Count == 2);
        await WaitFor(() => { lock (changes) return changes.Count == 4; });
        lock (changes) Assert.Equal([true, false, true, false], changes);
        Assert.False(l.IsReceiving);
    }

    [Fact]
    public async Task ThreeBackToBackJobsAreThreeFilesInPrintOrder()
    {
        var l = Make();
        l.Start();
        var jobs = new[] { Pdf(300, (byte)'a'), Pdf(20, (byte)'b'), Pdf(5000, (byte)'c') };
        foreach (var j in jobs) Send(j); // no pause between connections
        await WaitFor(() => Count == 3);
        for (int i = 0; i < 3; i++) Assert.Equal(jobs[i], File.ReadAllBytes(_events[i].path));
        Assert.Equal(3, _events.Select(e => e.id).Distinct().Count());
        Assert.Equal(0, ErrorCount);
    }

    [Fact]
    public async Task FiftyTinyJobsThatCloseBeforeTheListenerWaitsAllArrive()
    {
        // A small job fits the pipe buffer: the sender can connect to the spare instance, write everything and close
        // before the listener gets to that instance. None may be lost.
        var l = Make();
        l.Start();
        var jobs = Enumerable.Range(0, 50).Select(i => Pdf(i, (byte)('a' + i % 26))).ToList();
        foreach (var j in jobs) Send(j);
        await WaitFor(() => Count + ErrorCount >= 50);
        Assert.Equal(0, ErrorCount);
        for (int i = 0; i < 50; i++) Assert.Equal(jobs[i], File.ReadAllBytes(_events[i].path));
    }

    [Fact]
    public async Task NonPdfIsMovedToRejectedAndReported()
    {
        var l = Make();
        l.Start();
        var junk = Encoding.ASCII.GetBytes("hello, not a pdf %%EOF");
        Send(junk);
        await WaitFor(() => ErrorCount == 1);
        Assert.Equal(0, Count);
        var kept = Assert.Single(Files(RejectedDir));
        Assert.Equal(junk, File.ReadAllBytes(kept));
        Assert.Empty(Files(JobsDir));
        Assert.Contains("not a PDF", _errors[0].reason);
    }

    [Fact]
    public async Task EmptyConnectionIsOnlyLoggedAndTheListenerGoesOn()
    {
        // A connection that delivers no bytes has nothing to keep or send: it is logged, never raised as a job or an
        // Error (whether the listener was already waiting on that instance or the client came and went first), and
        // leaves no file anywhere.
        var l = Make();
        l.Start();
        for (int i = 0; i < 3; i++) using (Connect()) { }
        var pdf = Pdf();
        Send(pdf);
        await WaitFor(() => Count == 1);
        Assert.Equal(pdf, File.ReadAllBytes(_events[0].path));
        Assert.Single(Directory.GetFiles(JobsDir));
        Assert.Empty(Files(RejectedDir));
        Assert.Equal(0, ErrorCount);
    }

    [Fact]
    public async Task PdfThatGoesOnAfterAnEarlierEofIsRejectedAsIncomplete()
    {
        // A connection that ended after an earlier section is not a whole document: the last bytes (bar whitespace)
        // must be %%EOF, and it must be the only one (Print To PDF writes exactly one, measured).
        var l = Make();
        l.Start();
        byte[] truncated = [.. Pdf(100), .. "1 0 obj << /Type /Catalog >> endobj\n"u8.ToArray()];
        Send(truncated);
        await WaitFor(() => ErrorCount == 1);
        Assert.Equal(0, Count);
        Assert.Contains("not a complete PDF", _errors[0].reason);
        Assert.Equal(truncated, File.ReadAllBytes(Assert.Single(Files(RejectedDir))));
        byte[] twoSections = [.. Pdf(100), .. Pdf(50)[9..], .. "\r\n \0"u8.ToArray()]; // ends in %%EOF + whitespace
        Send(twoSections);
        await WaitFor(() => ErrorCount == 2);
        Assert.Equal(0, Count);
        Assert.Contains("2 %%EOF", _errors[1].reason);
    }

    [Theory]
    [MemberData(nameof(PdfAcceptanceVectors.Names), MemberType = typeof(PdfAcceptanceVectors))]
    public async Task TheListenerAppliesTheSharedAcceptanceRule(string name)
    {
        // Same vectors as the install script's Test-SinglePdf (InstallScriptTests): one rule for both.
        var (_, bytes, accepted) = PdfAcceptanceVectors.Get(name);
        var l = Make();
        l.MaxJobBytes = PdfAcceptanceVectors.MaxBytes;
        l.Start();
        Send(bytes);
        var sentinel = Pdf(7, (byte)'s'); // jobs are handled in order: once this is raised, the case is settled
        Send(sentinel);
        await WaitFor(() => { lock (_events) return _events.Count > 0 && File.ReadAllBytes(_events[^1].path).SequenceEqual(sentinel); });
        Assert.Equal(accepted ? 2 : 1, Count);
        if (accepted) Assert.Equal(bytes, File.ReadAllBytes(_events[0].path));
    }

    [Fact]
    public async Task ConnectionBrokenMidJobIsRejectedNeverRaised()
    {
        // The client goes away after half a PDF (no %%EOF): the part is kept in rejected\, the job is not raised.
        var l = Make();
        l.Start();
        var half = Pdf(10_000)[..5000];
        using (var c = Connect()) { c.Write(half); c.Flush(); }
        await WaitFor(() => ErrorCount == 1);
        Assert.Equal(0, Count);
        Assert.Empty(Files(JobsDir));
        Assert.Equal(half, File.ReadAllBytes(Assert.Single(Files(RejectedDir))));
        // The listener goes on: the spooler's retry of the whole job arrives normally.
        var whole = Pdf(10_000);
        Send(whole);
        await WaitFor(() => Count == 1);
        Assert.Equal(whole, File.ReadAllBytes(_events[0].path));
    }

    [Fact]
    public async Task AClientThatConnectsAndNeverWritesIsDroppedAfterTheIdleTimeout()
    {
        // Any signed-in user may open the pipe. One that holds a connection and sends nothing must not stall the
        // printer: after IdleTimeout without a byte it is dropped (reported, rate-limited) and the next job arrives.
        var l = Make();
        l.IdleTimeout = TimeSpan.FromMilliseconds(300);
        l.Start();
        using var idle = Connect();
        var pdf = Pdf(200_000); // larger than any pipe quota: the sender needs the listener to read
        var send = Task.Run(() => Send(pdf));
        await WaitFor(() => Count == 1, 5000);
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(pdf, File.ReadAllBytes(_events[0].path));
        var e = Assert.Single(_errors);
        Assert.Contains("sent nothing", e.reason);
        Assert.Empty(Files(RejectedDir));
        Assert.Single(Directory.GetFiles(JobsDir));
    }

    [Fact]
    public async Task AJobThatStallsMidWayIsRejectedAfterTheIdleTimeout()
    {
        var l = Make();
        l.IdleTimeout = TimeSpan.FromMilliseconds(300);
        l.Start();
        var half = Pdf(10_000)[..5000];
        using var c = Connect();
        c.Write(half);
        c.Flush();
        await WaitFor(() => ErrorCount == 1, 5000);
        Assert.Contains("no data arrived", _errors[0].reason);
        Assert.Equal(0, Count);
        Assert.Empty(Files(JobsDir));
        Assert.Equal(half, File.ReadAllBytes(Assert.Single(Files(RejectedDir))));
        Send(Pdf(100)); // the listener goes on
        await WaitFor(() => Count == 1, 5000);
    }

    [Fact]
    public async Task AJobRejectedWhileStoppingIsKeptButNotRaised()
    {
        // Once Stop is called nothing is raised (the tray is exiting): a job that ends incomplete during the grace
        // is still kept in rejected\ and logged.
        var l = Make();
        l.Start();
        var c = Connect();
        c.Write(Pdf(10_000)[..3000]);
        c.Flush();
        await WaitFor(() => Directory.Exists(JobsDir) && Directory.GetFiles(JobsDir, "*.part").Length == 1);
        var stop = Task.Run(l.Stop);
        await Task.Delay(200);
        c.Dispose(); // ends without %%EOF inside the grace
        await stop.WaitAsync(PrintPipeListener.StopGrace + TimeSpan.FromSeconds(2));
        Assert.Single(Files(RejectedDir));
        Assert.Equal(0, ErrorCount);
        Assert.Equal(0, Count);
    }

    [Fact]
    public async Task OversizeJobIsRejectedAndTheSenderIsNotFailed()
    {
        // Over the cap: kept (truncated at the cap) in rejected\, the rest read and dropped so the spooler's write
        // succeeds and it does not retry the job forever.
        var l = Make();
        l.MaxJobBytes = 4096;
        l.Start();
        var big = Pdf(100_000);
        Send(big); // must not throw
        await WaitFor(() => ErrorCount == 1);
        Assert.Equal(0, Count);
        Assert.Empty(Files(JobsDir));
        var kept = Assert.Single(Files(RejectedDir));
        Assert.True(new FileInfo(kept).Length <= 4096 + 65536, "kept far more than the cap");
        Assert.Contains("too large", _errors[0].reason);
        // Next job fine.
        Send(Pdf(100));
        await WaitFor(() => Count == 1);
    }

    [Fact]
    public async Task StopMidJobIsPromptAndLeavesNoPdf()
    {
        var l = Make();
        l.Start();
        var c = Connect();
        c.Write(Pdf(10_000)[..3000]);
        c.Flush();
        await WaitFor(() => Directory.Exists(JobsDir) && Directory.GetFiles(JobsDir, "*.part").Length == 1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        l.Stop(); // the sender is stalled: the grace runs out and the job is cut off
        Assert.True(sw.Elapsed < PrintPipeListener.StopGrace + TimeSpan.FromSeconds(2), $"Stop took {sw.Elapsed}");
        Assert.False(l.IsListening);
        Assert.Empty(Directory.GetFiles(JobsDir, "*.pdf"));
        Assert.Empty(Directory.GetFiles(JobsDir, "*.part")); // moved aside, never deleted
        Assert.Single(Files(RejectedDir));
        Assert.Equal(0, Count);
        // The sender sees the pipe gone. The spooler does NOT retry such a job by itself (measured): it waits in the
        // queue in "Error" until someone restarts it, which is why Stop gives a job in progress StopGrace first.
        Assert.ThrowsAny<IOException>(() => { for (int i = 0; i < 100; i++) { c.Write(new byte[65536]); c.Flush(); } });
        try { c.Dispose(); } catch (IOException) { }
    }

    [Fact]
    public async Task StopLetsAJobInProgressFinishWithinTheGraceAndKeepsItForTheNextStart()
    {
        // A job cut off mid-write waits in the queue in "Error" and the spooler does not retry it (measured), so Stop
        // gives a job in progress StopGrace to finish. It is kept in jobs\ (offered as unsent at the next start), not raised.
        var l = Make();
        l.Start();
        var pdf = Pdf(10_000);
        var c = Connect();
        c.Write(pdf[..3000]);
        c.Flush();
        await WaitFor(() => Directory.Exists(JobsDir) && Directory.GetFiles(JobsDir, "*.part").Length == 1);
        var stop = Task.Run(l.Stop);
        await Task.Delay(200);
        c.Write(pdf[3000..]);
        c.Flush();
        c.Dispose();
        await stop.WaitAsync(PrintPipeListener.StopGrace + TimeSpan.FromSeconds(2));
        var kept = Assert.Single(Directory.GetFiles(JobsDir, "*.pdf"));
        Assert.Equal(pdf, File.ReadAllBytes(kept));
        Assert.Empty(Directory.GetFiles(JobsDir, "*.part"));
        Assert.Equal(0, Count);
        Assert.Empty(Files(RejectedDir));
    }

    [Fact]
    public async Task StopReceivesTheNextJobThatAlreadyConnectedToTheSpare()
    {
        // While one job arrives, a spare instance waits, and the spooler's next job may already be on it when Stop
        // comes. That job is read too (within the grace) and kept for the next start, never dropped unread.
        var l = Make();
        l.Start();
        var first = Pdf(10_000, (byte)'a');
        var c1 = Connect();
        c1.Write(first[..3000]);
        c1.Flush();
        await WaitFor(() => Directory.Exists(JobsDir) && Directory.GetFiles(JobsDir, "*.part").Length == 1);
        var second = Pdf(200, (byte)'b');
        var c2 = Connect(); // the spare
        var write2 = Task.Run(() => { c2.Write(second); c2.Flush(); c2.Dispose(); });
        var stop = Task.Run(l.Stop);
        await Task.Delay(200);
        c1.Write(first[3000..]);
        c1.Flush();
        c1.Dispose();
        await stop.WaitAsync(PrintPipeListener.StopGrace + TimeSpan.FromSeconds(3));
        await write2.WaitAsync(TimeSpan.FromSeconds(3));
        var kept = Directory.GetFiles(JobsDir, "*.pdf").Select(File.ReadAllBytes).ToList();
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, k => k.SequenceEqual(first));
        Assert.Contains(kept, k => k.SequenceEqual(second));
        Assert.Empty(Files(RejectedDir));
        Assert.Equal(0, Count);
    }

    [Fact]
    public async Task StopWithNoConnectionIsPromptAndClosesThePipe()
    {
        var l = Make();
        l.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        l.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Stop took {sw.Elapsed}");
        using var c = new NamedPipeClientStream(".", _pipe, PipeDirection.Out);
        Assert.ThrowsAny<Exception>(() => c.Connect(300));
        // Start again on the same name works (the first-instance check passes once the old instances are gone).
        l.Start();
        Assert.True(l.IsListening);
        Send(Pdf());
        await WaitFor(() => Count == 1);
    }

    [Fact]
    public async Task SecondListenerOnTheSameNameReportsAnErrorAndTheFirstKeepsWorking()
    {
        var first = Make();
        first.Start();
        var second = Make();
        second.Start();
        Assert.False(second.IsListening);
        await WaitFor(() => ErrorCount == 1);
        Assert.Contains("already", _errors[0].reason);
        Send(Pdf());
        await WaitFor(() => Count == 1);
        Assert.True(first.IsListening);
    }

    [Fact]
    public void SquatterPipeIsDetected()
    {
        // Another program created the pipe first (any ACL): the listener must not become a second instance of it.
        using var squatter = new NamedPipeServerStream(_pipe, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var l = Make();
        l.Start();
        Assert.False(l.IsListening);
        Assert.Equal(1, ErrorCount);
    }

    [Fact]
    public async Task ATakenNameIsReportedAgainWhileItLastsAndTakenOverOnceItIsFree()
    {
        // A squatter (or a LANFax that is exiting) holds the name: the listener keeps trying, repeats the warning
        // while the name stays taken, and serves the pipe as soon as it is free.
        var squatter = new NamedPipeServerStream(_pipe, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var l = Make();
        l.NameRetry = TimeSpan.FromMilliseconds(50);
        l.NameTakenRepeat = TimeSpan.FromMilliseconds(300);
        l.Start();
        Assert.False(l.IsListening);
        Assert.Equal(1, ErrorCount);
        await WaitFor(() => ErrorCount >= 2, 5000);
        lock (_errors) Assert.All(_errors, e => Assert.Contains("already", e.reason));
        squatter.Dispose();
        await WaitFor(() => l.IsListening, 5000);
        Send(Pdf());
        await WaitFor(() => Count == 1);
        l.Stop();
        Assert.False(l.IsListening);
    }

    [Fact]
    public void StopEndsTheRetryWhileTheNameIsTaken()
    {
        var squatter = new NamedPipeServerStream(_pipe, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var l = Make();
        l.NameRetry = TimeSpan.FromMilliseconds(50);
        l.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        l.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Stop took {sw.Elapsed}");
        squatter.Dispose();
        Thread.Sleep(300);
        Assert.False(l.IsListening);
        using var c = new NamedPipeClientStream(".", _pipe, PipeDirection.Out);
        Assert.ThrowsAny<Exception>(() => c.Connect(200)); // nobody took the name after Stop
    }

    [Fact]
    public void PipeSecurityDeniesNetworkAndGivesEverySignedInUserReadWriteDeleteButNoNewInstance()
    {
        // The spooler opens the port as the job owner (measured 2026-09-30: a SYSTEM-only DACL refused the job), so
        // another signed-in user's job needs the access the spooler asks for (measured). That is no more than printing
        // to the printer already allows: changing the pipe or opening another server instance of it (which would take
        // jobs) stays with SYSTEM and the user running the listener.
        var rules = PrintPipeListener.CreateSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var me = WindowsIdentity.GetCurrent().User!;
        Assert.Contains(rules, r => r.IdentityReference.Equals(network) && r.AccessControlType == AccessControlType.Deny
            && (r.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite);
        Assert.Contains(rules, r => r.IdentityReference.Equals(system) && r.AccessControlType == AccessControlType.Allow);
        Assert.Contains(rules, r => r.IdentityReference.Equals(me) && r.AccessControlType == AccessControlType.Allow);
        var u = Assert.Single(rules, r => r.IdentityReference.Equals(users));
        Assert.Equal(AccessControlType.Allow, u.AccessControlType);
        Assert.Equal((PipeAccessRights)0x13019b, u.PipeAccessRights); // GENERIC_READ|GENERIC_WRITE|DELETE minus FILE_CREATE_PIPE_INSTANCE
        Assert.Equal((PipeAccessRights)0, u.PipeAccessRights & (PipeAccessRights.ChangePermissions
            | PipeAccessRights.TakeOwnership | PipeAccessRights.CreateNewInstance));
        Assert.All(rules.Where(r => r.AccessControlType == AccessControlType.Allow),
            r => Assert.True(r.IdentityReference.Equals(system) || r.IdentityReference.Equals(me) || r.IdentityReference.Equals(users),
                "unexpected allow for " + r.IdentityReference));
    }

    [Fact]
    public void TheLivePipeCarriesTheNetworkDeny()
    {
        var l = Make();
        l.Start();
        using var c = new NamedPipeClientStream(".", _pipe, PipeDirection.Out);
        c.Connect(5000);
        var sddl = c.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.Matches(@"\(D;[^)]*;;;NU\)", sddl);
        Assert.DoesNotContain(";;;WD)", sddl); // no Everyone
    }

    [Fact]
    public async Task StalePartFilesAreMovedToRejectedAtStart()
    {
        Directory.CreateDirectory(JobsDir);
        var stale = Path.Combine(JobsDir, Guid.NewGuid() + ".pdf.part");
        File.WriteAllBytes(stale, [1, 2, 3]);
        var keep = Path.Combine(JobsDir, Guid.NewGuid() + ".pdf"); // an unsent job is the tracker's, untouched
        File.WriteAllBytes(keep, Pdf());
        var l = Make();
        l.Start();
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(keep));
        var moved = Assert.Single(Files(RejectedDir));
        Assert.Equal(Path.GetFileName(stale), Path.GetFileName(moved));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(moved));
        await Task.Yield();
        Assert.Equal(0, Count);
    }

    [Fact]
    public async Task AJobWhoseRenameFailsIsCopiedIntoPlaceAndRaised()
    {
        // A scanner (or anything) holds the finished .part without FILE_SHARE_DELETE past the rename's retries: the
        // complete job is copied to <id>.pdf and raised, never left under .part (which the tracker does not see).
        var l = Make();
        l.Start();
        var pdf = Pdf(10_000);
        var c = Connect();
        c.Write(pdf[..3000]);
        c.Flush();
        await WaitFor(() => Directory.Exists(JobsDir) && Directory.GetFiles(JobsDir, "*.part").Length == 1);
        var part = Directory.GetFiles(JobsDir, "*.part")[0];
        using (var holder = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            c.Write(pdf[3000..]);
            c.Flush();
            c.Dispose();
            await WaitFor(() => Count == 1);
        }
        Assert.Equal(pdf, File.ReadAllBytes(_events[0].path));
        Assert.Equal(Path.Combine(JobsDir, _events[0].id + ".pdf"), _events[0].path);
        Assert.Equal(0, ErrorCount);
        // The .part could not be deleted while held: the next start sets it aside as a copy, not as a new job.
        l.Stop();
        var again = Make();
        again.Start();
        Assert.Single(Directory.GetFiles(JobsDir, "*.pdf"));
        Assert.Empty(Directory.GetFiles(JobsDir, "*.part"));
        Assert.Equal(0, ErrorCount);
    }

    [Fact]
    public void ACompletePartWithoutItsPdfIsSetAsideAndReportedNeverOfferedAgain()
    {
        // A whole job left as <id>.pdf.part with no <id>.pdf beside it: either its copy was delivered and the .part
        // could not be deleted (the user has since sent or cancelled it, so <id>.pdf is gone), or its rename and copy
        // both failed. The two cannot be told apart, and re-offering a faxed document would fax it twice: it goes to
        // rejected\ with one Error, never back into jobs\.
        Directory.CreateDirectory(JobsDir);
        var id = Guid.NewGuid();
        var pdf = Pdf(50);
        File.WriteAllBytes(Path.Combine(JobsDir, id + ".pdf.part"), pdf);
        var l = Make();
        l.Start();
        Assert.False(File.Exists(Path.Combine(JobsDir, id + ".pdf")));
        Assert.Empty(Directory.GetFiles(JobsDir));
        Assert.Equal(pdf, File.ReadAllBytes(Assert.Single(Files(RejectedDir))));
        lock (_errors) Assert.Contains("may already have been faxed", Assert.Single(_errors).reason);
        Assert.Equal(0, Count);
    }

    [Fact]
    public async Task AJobAlreadyDeliveredByCopyIsNotOfferedAgainOnceItsPdfIsGone()
    {
        // The rename fails, the copy is delivered and the held .part cannot be deleted; the user then sends the job
        // (the tracker moves <id>.pdf away). A restart must not turn the leftover .part back into <id>.pdf.
        var l = Make();
        l.Start();
        var pdf = Pdf(10_000);
        var c = Connect();
        c.Write(pdf[..3000]);
        c.Flush();
        await WaitFor(() => Directory.Exists(JobsDir) && Directory.GetFiles(JobsDir, "*.part").Length == 1);
        var part = Directory.GetFiles(JobsDir, "*.part")[0];
        using (var holder = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            c.Write(pdf[3000..]);
            c.Flush();
            c.Dispose();
            await WaitFor(() => Count == 1);
            l.Stop();
        }
        File.Delete(_events[0].path); // sent: the tracker moved it out of jobs\
        var again = Make();
        again.Start();
        Assert.Empty(Directory.GetFiles(JobsDir, "*.pdf"));
        Assert.Empty(Directory.GetFiles(JobsDir, "*.part"));
        Assert.Equal(pdf, File.ReadAllBytes(Assert.Single(Files(RejectedDir))));
        Assert.Equal(1, Count); // raised once, in the first session only
        again.Stop();
    }

    [Fact]
    public async Task AListenerThatStoppedByItselfIsNotRunningAndStartsAgain()
    {
        // Any unexpected fault ends the serve loop ("print pipe listener stopped"): IsRunning says so, so the tray can
        // restart it, and Start serves the pipe again.
        var l = Make();
        bool thrown = false;
        l.Log = m => { if (!thrown && m.Contains(" received,")) { thrown = true; throw new InvalidOperationException("boom"); } };
        l.Start();
        Assert.True(l.IsRunning);
        Send(Pdf());
        await WaitFor(() => !l.IsRunning);
        Assert.False(l.IsListening);
        Assert.Contains(_errors, e => e.reason.Contains("listener stopped"));
        l.Start();
        Assert.True(l.IsRunning && l.IsListening);
        Send(Pdf(5, (byte)'z'));
        await WaitFor(() => Count == 1);
    }

    [Fact]
    public async Task IsRunningStaysTrueWhileTheNameIsTakenAndGoesFalseOnStop()
    {
        using var squatter = new NamedPipeServerStream(_pipe, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var l = Make();
        l.NameRetry = TimeSpan.FromMilliseconds(50);
        l.Start();
        await Task.Delay(200);
        Assert.False(l.IsListening);
        Assert.True(l.IsRunning); // still trying: not a dead listener
        l.Stop();
        Assert.False(l.IsRunning);
    }

    [Fact]
    public async Task ASecondStopWaitsForTheJobTheFirstIsFinishing()
    {
        // The tray's Exit runs Stop off the UI thread; a WM_ENDSESSION meanwhile calls Stop again. That second call
        // must wait for the job in flight too, or Windows ends the process mid-job.
        var l = Make();
        l.Start();
        var pdf = Pdf(10_000);
        var c = Connect();
        c.Write(pdf[..3000]);
        c.Flush();
        await WaitFor(() => Directory.Exists(JobsDir) && Directory.GetFiles(JobsDir, "*.part").Length == 1);
        var first = Task.Run(l.Stop);
        await Task.Delay(200);
        var second = Task.Run(l.Stop);
        await Task.Delay(1000);
        Assert.False(second.IsCompleted, "the second Stop returned while the job was still arriving");
        c.Write(pdf[3000..]);
        c.Flush();
        c.Dispose();
        await Task.WhenAll(first, second).WaitAsync(PrintPipeListener.StopWait + TimeSpan.FromSeconds(2));
        Assert.False(l.IsReceiving);
        Assert.Equal(pdf, File.ReadAllBytes(Assert.Single(Directory.GetFiles(JobsDir, "*.pdf"))));
    }

    [Fact]
    public async Task StartAfterAStopThatGaveUpSaysThePreviousListenerIsStillFinishing()
    {
        // A Stop that gave up (the loop is still busy) leaves our own instances on the name: that is not a squatter.
        using var gate = new ManualResetEventSlim();
        var l = Make();
        l.NameRetry = TimeSpan.FromMilliseconds(50);
        l.Log = m => { if (m.Contains(" received,")) gate.Wait(TimeSpan.FromSeconds(60)); };
        l.Start();
        Send(Pdf());
        await WaitFor(() => Directory.Exists(JobsDir) && Directory.GetFiles(JobsDir, "*.pdf").Length == 1);
        l.Stop(); // gives up after StopWait: the loop is blocked in the log call
        l.Start();
        gate.Set();
        lock (_errors)
        {
            var e = Assert.Single(_errors);
            Assert.Contains("still finishing", e.reason);
            Assert.DoesNotContain("another program", e.reason);
        }
        await WaitFor(() => l.IsListening, 10000); // takes the name once the old loop is gone
    }

    [Theory]
    [MemberData(nameof(PdfAcceptanceVectors.Names), MemberType = typeof(PdfAcceptanceVectors))]
    public void IsOneCompletePdfAppliesTheSharedAcceptanceRule(string name)
    {
        var (_, bytes, accepted) = PdfAcceptanceVectors.Get(name);
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name + ".pdf");
        File.WriteAllBytes(path, bytes);
        Assert.Equal(accepted, PrintPipeListener.IsOneCompletePdf(path, PdfAcceptanceVectors.MaxBytes));
    }

    [Fact]
    public void PipePathUsesTheName()
    {
        Assert.Equal(@"\\.\pipe\LANFax", PrintPipeListener.PortName(PrintPipeListener.DefaultPipeName));
        Assert.Equal(@"\\.\pipe\" + _pipe, new PrintPipeListener(_dir, _pipe).PipePath);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("%PDF-1.7 %%EOF", 1)]
    [InlineData("%%EOF%%EOF", 2)]
    [InlineData("%%EO F %EOF %%eof", 0)]
    public void CountsEofMarkers(string content, int expected) =>
        Assert.Equal(expected, PrintPipeListener.CountEof(new MemoryStream(Encoding.ASCII.GetBytes(content))));

    [Theory]
    [InlineData("", false)]
    [InlineData("%PDF %%EOF", true)]
    [InlineData("%PDF %%EOF\r\n", true)]
    [InlineData("%PDF %%EOF \t\f\0\n", true)]
    [InlineData("%PDF %%EOF\nxref", false)]
    [InlineData("%PDF %%EOF\n%%EO", false)]
    [InlineData("%PDF %%EOF\n1 0 obj %%EOF\n", true)]
    [InlineData("%PDF no marker\n", false)]
    public void EndsWithEofAllowsOnlyWhitespaceAfterTheLastMarker(string content, bool expected)
    {
        for (int chunk = 1; chunk <= 4; chunk++)
        {
            var b = Encoding.ASCII.GetBytes(content);
            var counter = new EofCounter();
            for (int i = 0; i < b.Length; i += chunk) counter.Feed(b.AsSpan(i, Math.Min(chunk, b.Length - i)));
            Assert.Equal(expected, counter.EndsWithEof);
        }
    }

    [Fact]
    public void CountsEofMarkersAcrossBufferBoundaries()
    {
        foreach (var at in new[] { 65536 - 4, 65536 - 2, 65536, 65536 * 2 - 1 })
        {
            var b = Enumerable.Repeat((byte)'x', 65536 * 3).ToArray();
            Encoding.ASCII.GetBytes("%%EOF").CopyTo(b, at);
            Assert.Equal(1, PrintPipeListener.CountEof(new MemoryStream(b)));
        }
    }

    [Fact]
    public void EofCounterFedInTinyChunksFindsMarkersSplitAnywhere()
    {
        var b = Encoding.ASCII.GetBytes("ab%%EOFcd%%E%%EOF%%%EOF");
        for (int chunk = 1; chunk <= 7; chunk++)
        {
            var counter = new EofCounter();
            for (int i = 0; i < b.Length; i += chunk) counter.Feed(b.AsSpan(i, Math.Min(chunk, b.Length - i)));
            Assert.Equal(3, counter.Count);
        }
    }
}
