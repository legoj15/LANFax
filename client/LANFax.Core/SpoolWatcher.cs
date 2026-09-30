namespace LANFax.Core;

/// <summary>
/// Watches spoolDir\print.pdf (the file the printer port writes). A file is complete when it has existed
/// for at least 1 s, its size is unchanged across two checks 500 ms apart, and it opens with FileShare.None.
/// A complete file that does not start with <c>%PDF</c> (including 0 bytes) is deleted and reported through
/// <see cref="Error"/>. A PDF must also end its document: with no <c>%%EOF</c> it is waited on for
/// <see cref="EofWait"/> and then treated as not a PDF; with two or more it is two jobs written into one file (the
/// spooler's behaviour for back-to-back jobs is still unverified), so it is moved to spoolDir\rejected\&lt;guid&gt;.pdf
/// and reported, never sent. Otherwise, if its size and first bytes are unchanged since the check, it is moved to
/// spoolDir\jobs\&lt;guid&gt;.pdf (freeing the port path) and <see cref="NewPrintJob"/> fires. A FileSystemWatcher
/// and a 2 s poll both just start the check loop if it is idle.
/// </summary>
public sealed class SpoolWatcher(string spoolDir) : IDisposable
{
    /// <summary>The same <see cref="Error"/> reason is raised at most once per this window.</summary>
    public static readonly TimeSpan ErrorRepeatAfter = TimeSpan.FromSeconds(60);
    /// <summary>After this many consecutive move failures, retry only on the 2 s poll (not every 250 ms).</summary>
    public const int FastRetryLimit = 5;
    /// <summary>Bytes compared again before the move, to catch a file rewritten or truncated in place after the check.</summary>
    private const int HeaderBytes = 1024;
    private static readonly byte[] EofMarker = "%%EOF"u8.ToArray();

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _reported = new();
    private FileSystemWatcher? _fsw;
    private Timer? _poll;
    private CancellationTokenSource? _cts;
    private int _running;
    private int _moveFailures; // consecutive; only touched by the single running loop

    public event Action<Guid, string>? NewPrintJob;

    /// <summary>
    /// A print job cannot be handled: (reason, cause). "not a PDF" files are reported every time; other reasons
    /// (e.g. jobs\ not writable) at most once per <see cref="ErrorRepeatAfter"/>. Raised on a thread-pool thread.
    /// </summary>
    public event Action<string, Exception>? Error;

    /// <summary>Optional diagnostics sink.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// How long a stable, unlocked print file without <c>%%EOF</c> is given to finish before it is deleted as not a
    /// PDF (a truncated document must not be faxed).
    /// </summary>
    public TimeSpan EofWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long print.pdf must have existed before it is checked. Set before <see cref="Start"/> (tests shorten the timings).</summary>
    public TimeSpan MinAge { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>The gap between the two size checks that must agree.</summary>
    public TimeSpan SizeRecheck { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>The wait before checking again while the file is too young, locked, growing or failing to move.</summary>
    public TimeSpan RetryEvery { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>The polling fallback beside the FileSystemWatcher (and the only retry after <see cref="FastRetryLimit"/> move failures).</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Test hook: runs after the checks, right before the size and header are compared again and the file moves.</summary>
    internal Action? BeforeMove { get; set; }

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

    private static byte[] ReadHeader(FileStream fs)
    {
        var head = new byte[(int)Math.Min(HeaderBytes, fs.Length)];
        fs.Position = 0;
        return head[..fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
    }

    /// <summary>The file at the port path still has the size and first bytes that were checked.</summary>
    private bool SameAsChecked(byte[] head, long size)
    {
        using var fs = new FileStream(PrintPath, FileMode.Open, FileAccess.Read, FileShare.None);
        return fs.Length == size && ReadHeader(fs).AsSpan().SequenceEqual(head);
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
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        DateTime? firstSeen = null;
        DateTime created = default;
        DateTime? noEofSince = null;
        long noEofSize = -1;
        while (!ct.IsCancellationRequested && File.Exists(PrintPath))
        {
            try
            {
                var fi = new FileInfo(PrintPath);
                if (!fi.Exists) { firstSeen = null; continue; }
                var ct2 = fi.CreationTimeUtc;
                if (firstSeen == null || ct2 != created) { firstSeen = DateTime.UtcNow; created = ct2; }

                if (DateTime.UtcNow - firstSeen < MinAge) { await Task.Delay(RetryEvery, ct); continue; }

                long size1 = fi.Length;
                await Task.Delay(SizeRecheck, ct);
                fi.Refresh();
                if (!fi.Exists) { firstSeen = null; continue; }
                if (fi.Length != size1 || fi.CreationTimeUtc != created) { firstSeen = null; continue; }

                bool isPdf;
                byte[] head;
                long checkedSize;
                int eofs;
                using (var fs = new FileStream(PrintPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    checkedSize = fs.Length;
                    head = ReadHeader(fs);
                    isPdf = head is [(byte)'%', (byte)'P', (byte)'D', (byte)'F', ..];
                    fs.Position = 0;
                    eofs = isPdf ? CountEof(fs) : 0;
                }

                if (ct.IsCancellationRequested) return;
                if (isPdf && eofs == 0)
                {
                    // Not finished yet (or truncated): keep checking while it stays the same size, up to EofWait.
                    if (noEofSince == null || noEofSize != checkedSize) { noEofSince = DateTime.UtcNow; noEofSize = checkedSize; }
                    if (DateTime.UtcNow - noEofSince < EofWait) { await Task.Delay(RetryEvery, ct); continue; }
                }
                firstSeen = null;
                noEofSince = null;
                if (!isPdf || eofs == 0)
                {
                    File.Delete(PrintPath);
                    Report(isPdf
                            ? $"print.pdf was not a PDF ({size1} bytes, no %%EOF after {EofWait.TotalSeconds:0.#} s); deleted it"
                            : $"print.pdf was not a PDF ({size1} bytes); deleted it",
                        new InvalidDataException(isPdf ? "print.pdf has no %%EOF" : "print.pdf does not start with %PDF"),
                        rateLimited: false);
                    continue;
                }

                // Two jobs written into one file must never go out as one fax: keep it aside and tell the user.
                bool reject = eofs >= 2;
                var id = Guid.NewGuid();
                var destDir = reject ? RejectedDir : JobsDir;
                var dest = Path.Combine(destDir, id + ".pdf");
                try { BeforeMove?.Invoke(); } catch { /* test hook */ }
                if (!SameAsChecked(head, checkedSize)) continue; // rewritten or truncated in place: check it again
                try
                {
                    Directory.CreateDirectory(destDir);
                    File.Move(PrintPath, dest);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (!File.Exists(PrintPath)) continue; // it vanished: nothing to move
                    int n = ++_moveFailures;
                    Report($"cannot move print.pdf into {destDir}: {e.Message}", e, rateLimited: true);
                    if (n >= FastRetryLimit)
                    {
                        if (n == FastRetryLimit) Log?.Invoke($"spool: {n} move failures in a row; retrying on the {PollEvery.TotalSeconds:0.#} s poll only");
                        return;
                    }
                    await Task.Delay(RetryEvery, ct);
                    continue;
                }
                _moveFailures = 0;

                if (reject)
                {
                    Report($"print.pdf contained {eofs} documents; the LAN Fax printer received two jobs at once. Print them one at a time.",
                        new InvalidDataException($"print.pdf had {eofs} %%EOF markers; kept as {dest}"), rateLimited: false);
                    continue;
                }

                if (ct.IsCancellationRequested)
                {
                    // stopped between move and raise: put it back so the next Start picks it up (unless a new job took the path)
                    try { File.Move(dest, PrintPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    return;
                }
                try { NewPrintJob?.Invoke(id, dest); } catch { /* subscriber bug: file is safely in jobs\ */ }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // locked by the spooler, or deleted mid-check: wait and look again
                await Task.Delay(RetryEvery, ct);
            }
        }
    }

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
        Log?.Invoke("spool: " + reason);
        try { Error?.Invoke(reason, e); } catch { /* a faulty subscriber must not stop the watcher */ }
    }
}
