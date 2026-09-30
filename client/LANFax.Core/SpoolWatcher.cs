namespace LANFax.Core;

/// <summary>
/// Watches spoolDir\print.pdf (the file the printer port writes) and claims each finished job before the spooler can
/// overwrite it with the next one (measured 2026-09-30: back-to-back jobs replace print.pdf, no append, no error).
/// The claim is an exclusive open (<see cref="ClaimedFile"/>): it fails while the spooler still has the file open, and
/// once it succeeds the spooler's next job gets a sharing violation and retries on its own until the file is gone.
/// The file is checked and then renamed away through that same handle, so it is never unlocked at the port path.
/// Checks under the claim: a file that does not start with <c>%PDF</c> (including 0 bytes) goes to
/// spoolDir\rejected\&lt;guid&gt;.pdf and is reported through <see cref="Error"/>; so is a PDF with no <c>%%EOF</c>
/// (the exclusive open proves its writer closed it, so it is truncated) and one with two or more <c>%%EOF</c> (two
/// jobs in one file). None of these releases the claim before the file is off the port path, and a failing rename
/// (jobs\ unwritable) is retried under the same claim. Otherwise the file goes to spoolDir\jobs\&lt;guid&gt;.pdf and
/// <see cref="NewPrintJob"/> fires. Nothing is ever deleted. While print.pdf exists the claim is retried every
/// <see cref="RetryEvery"/> (10 ms) so the gap between the spooler closing a job and the claim stays small; a
/// FileSystemWatcher and a 2 s poll start that loop when it is idle.
/// </summary>
public sealed class SpoolWatcher(string spoolDir) : IDisposable
{
    /// <summary>The same <see cref="Error"/> reason is raised at most once per this window.</summary>
    public static readonly TimeSpan ErrorRepeatAfter = TimeSpan.FromSeconds(60);
    /// <summary>After this many consecutive move failures the rename is retried every <see cref="PollEvery"/>, still holding the file.</summary>
    public const int FastRetryLimit = 5;
    /// <summary>A print.pdf held open elsewhere this long is logged (another program holding it, or a very long job).</summary>
    public static readonly TimeSpan LockedLogAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MoveRetry = TimeSpan.FromMilliseconds(250);
    private static readonly byte[] EofMarker = "%%EOF"u8.ToArray();

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _reported = new();
    private FileSystemWatcher? _fsw;
    private Timer? _poll;
    private CancellationTokenSource? _cts;
    private int _running;

    public event Action<Guid, string>? NewPrintJob;

    /// <summary>
    /// A file left the port path (to jobs\ or rejected\): one per spooler job that reached the port. Raised before
    /// <see cref="NewPrintJob"/> or the rejection's <see cref="Error"/>; the loss check counts these.
    /// </summary>
    public event Action<string>? Claimed;

    /// <summary>
    /// A print job cannot be handled: (reason, cause). Rejected files are reported every time; other reasons
    /// (e.g. jobs\ not writable) at most once per <see cref="ErrorRepeatAfter"/>. Raised on a thread-pool thread.
    /// </summary>
    public event Action<string, Exception>? Error;

    /// <summary>Optional diagnostics sink.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// How long print.pdf may refuse the claim with access denied (briefly normal for a file pending deletion) before
    /// that is reported through <see cref="Error"/>: this user cannot take files off the port, so every later job
    /// would overwrite it unseen.
    /// </summary>
    public TimeSpan AccessDeniedReportAfter { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The wait before trying the claim again while print.pdf is open elsewhere (the spooler writing it).</summary>
    public TimeSpan RetryEvery { get; set; } = TimeSpan.FromMilliseconds(10);
    /// <summary>The polling fallback beside the FileSystemWatcher (and the only retry after <see cref="FastRetryLimit"/> move failures).</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Test hook: runs under the claim, after the checks, right before the rename.</summary>
    internal Action? BeforeMove { get; set; }
    /// <summary>Test hook: runs under the claim, right after the rename (the destination path).</summary>
    internal Action<string>? AfterMove { get; set; }
    /// <summary>Test hook: runs when a check loop has ended, before it is marked idle.</summary>
    internal Action? LoopEnded { get; set; }

    public string PrintPath => Path.Combine(spoolDir, "print.pdf");
    public string JobsDir => Path.Combine(spoolDir, "jobs");
    public string RejectedDir => Path.Combine(spoolDir, "rejected");

    /// <summary>Number of <c>%%EOF</c> markers from the stream's current position to its end.</summary>
    public static int CountEof(Stream s)
    {
        var buf = new byte[64 * 1024];
        int carry = 0, count = 0, n;
        while ((n = s.Read(buf, carry, buf.Length - carry)) > 0)
        {
            int len = carry + n;
            var span = buf.AsSpan(0, len);
            int i = 0, at;
            while ((at = span[i..].IndexOf(EofMarker)) >= 0) { count++; i += at + EofMarker.Length; }
            // Keep the tail that could begin a marker split across reads (never part of one already counted).
            carry = Math.Min(EofMarker.Length - 1, len - i);
            Buffer.BlockCopy(buf, len - carry, buf, 0, carry);
        }
        return count;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts != null) return;
            Directory.CreateDirectory(spoolDir);
            Directory.CreateDirectory(JobsDir);
            _cts = new CancellationTokenSource();
            try
            {
                _fsw = new FileSystemWatcher(spoolDir, "print.pdf")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                _fsw.Created += (_, _) => Kick();
                _fsw.Changed += (_, _) => Kick();
                _fsw.Renamed += (_, _) => Kick();
                _fsw.Error += (_, _) => Kick(); // buffer overflow etc.: the poll and a fresh check cover it
            }
            catch (Exception) { _fsw = null; } // the poll alone is enough
            _poll = new Timer(_ => Kick(), null, TimeSpan.Zero, PollEvery);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _poll?.Dispose(); _poll = null;
            _fsw?.Dispose(); _fsw = null;
        }
        if (cts == null) return;
        cts.Cancel(); // a running loop sees the (still readable) cancelled token and stops before moving or raising
        cts.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// <see cref="Stop"/>, then waits up to <paramref name="timeout"/> for a check already running to end, so a claim
    /// past its last cancellation check has been reported (<see cref="Claimed"/>) before the caller settles its count.
    /// False on timeout.
    /// </summary>
    public bool StopAndWait(TimeSpan timeout)
    {
        Stop();
        var end = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _running) != 0)
        {
            if (DateTime.UtcNow >= end) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    /// <summary>Nothing at the port and no check running.</summary>
    public bool IsIdle => Volatile.Read(ref _running) == 0 && !File.Exists(PrintPath);

    /// <summary>
    /// Starts a check now and waits up to <paramref name="timeout"/> until nothing is left at the port and no check
    /// runs: a print.pdf left from the last run is claimed before the caller lets the queue print again. False on
    /// timeout (a file still being written or refused); the running check carries on either way.
    /// </summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        Kick();
        while (true)
        {
            if (IsIdle) return true;
            if (DateTime.UtcNow >= end) return false;
            Thread.Sleep(10);
        }
    }

    private void Kick()
    {
        CancellationToken ct;
        lock (_gate) { if (_cts == null) return; ct = _cts.Token; }
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await LoopAsync(ct); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Report("spool check failed: " + e.Message, e, rateLimited: true); /* next poll retries */ }
            finally
            {
                try { LoopEnded?.Invoke(); } catch { /* test hook */ }
                Volatile.Write(ref _running, 0);
            }
            // A file created after the loop saw none but before it was marked idle had its events dropped: look again.
            if (!ct.IsCancellationRequested && File.Exists(PrintPath)) Kick();
        });
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        DateTime? lockedSince = null;
        DateTime? deniedSince = null;
        while (!ct.IsCancellationRequested && File.Exists(PrintPath))
        {
            try
            {
                using var claim = ClaimedFile.TryOpen(PrintPath, out bool denied);
                if (claim == null)
                {
                    // Still being written (or deleted meanwhile: the loop condition sees that).
                    if (!denied) deniedSince = null;
                    else if (DateTime.UtcNow - (deniedSince ??= DateTime.UtcNow) > AccessDeniedReportAfter)
                        Report("cannot take print.pdf off the printer port: access denied. Later documents will replace it; ask an administrator to repair LANFax.",
                            new UnauthorizedAccessException("access denied opening " + PrintPath), rateLimited: true);
                    lockedSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - lockedSince > LockedLogAfter)
                    {
                        Log?.Invoke($"print.pdf has been open elsewhere for {LockedLogAfter.TotalMinutes:0} min; still waiting");
                        lockedSince = DateTime.UtcNow;
                    }
                    await Task.Delay(RetryEvery, ct);
                    continue;
                }
                lockedSince = null;
                deniedSince = null;
                if (ct.IsCancellationRequested) return; // stopped: release it untouched

                var fs = claim.Stream;
                long size = fs.Length;
                var head = new byte[4];
                bool isPdf = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == 4 && head.AsSpan().SequenceEqual("%PDF"u8);
                fs.Position = 0;
                int eofs = isPdf ? CountEof(fs) : 0;

                // Not a PDF, truncated, or two jobs in one file: kept aside, never sent, never deleted.
                bool reject = !isPdf || eofs != 1;
                var id = Guid.NewGuid();
                var destDir = reject ? RejectedDir : JobsDir;
                var dest = Path.Combine(destDir, id + ".pdf");
                BeforeMove?.Invoke();
                if (ct.IsCancellationRequested) return;
                // Only the rename is retried, under the same claim: releasing print.pdf between tries would let the
                // spooler's next job replace it. A Stop cancels the wait and releases it untouched.
                for (int failures = 0; ; )
                {
                    try
                    {
                        Directory.CreateDirectory(destDir);
                        claim.RenameTo(dest);
                        break;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        failures++;
                        Report($"cannot move print.pdf into {destDir}: {e.Message}", e, rateLimited: true);
                        if (failures == FastRetryLimit)
                            Log?.Invoke($"{failures} move failures in a row; still holding print.pdf, retrying every {PollEvery.TotalSeconds:0.#} s");
                        await Task.Delay(failures < FastRetryLimit ? MoveRetry : PollEvery, ct);
                    }
                }
                try { AfterMove?.Invoke(dest); } catch { /* test hook */ }
                claim.Dispose();
                try { Claimed?.Invoke(dest); } catch { /* a faulty subscriber must not stop the watcher */ }

                if (!isPdf || eofs == 0)
                {
                    Report(isPdf
                            ? $"print.pdf was not a PDF ({size} bytes, no %%EOF: truncated); kept as {dest}"
                            : $"print.pdf was not a PDF ({size} bytes); kept as {dest}",
                        new InvalidDataException(isPdf ? "print.pdf has no %%EOF" : "print.pdf does not start with %PDF"),
                        rateLimited: false);
                    continue;
                }
                if (reject)
                {
                    Report($"print.pdf contained {eofs} documents; the LAN Fax printer received two jobs at once. Print them one at a time.",
                        new InvalidDataException($"print.pdf had {eofs} %%EOF markers; kept as {dest}"), rateLimited: false);
                    continue;
                }
                // Stopped after the move: the file is in jobs\ with no record, so the next start's scan offers it.
                if (ct.IsCancellationRequested) return;
                try { NewPrintJob?.Invoke(id, dest); } catch { /* subscriber bug: file is safely in jobs\ */ }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // deleted mid-check, or a read failed: look again
                await Task.Delay(Max(RetryEvery, TimeSpan.FromMilliseconds(50)), ct);
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private void Report(string reason, Exception e, bool rateLimited)
    {
        if (rateLimited)
        {
            var now = DateTime.UtcNow;
            lock (_reported)
            {
                if (_reported.TryGetValue(reason, out var last) && now - last < ErrorRepeatAfter) return;
                _reported[reason] = now;
            }
        }
        Log?.Invoke(reason);
        try { Error?.Invoke(reason, e); } catch { /* a faulty subscriber must not stop the watcher */ }
    }
}
