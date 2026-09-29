namespace LANFax.Core;

/// <summary>
/// Watches spoolDir\print.pdf (the file the printer port writes). A file is complete when it has existed
/// for at least 1 s, its size is unchanged across two checks 500 ms apart, and it opens with FileShare.None.
/// A complete file that does not start with <c>%PDF</c> (including 0 bytes) is deleted and reported through
/// <see cref="Error"/>; a PDF is moved to spoolDir\jobs\&lt;guid&gt;.pdf (freeing the port path) and
/// <see cref="NewPrintJob"/> fires. A FileSystemWatcher and a 2 s poll both just start the check loop if it is idle.
/// </summary>
public sealed class SpoolWatcher(string spoolDir) : IDisposable
{
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SizeRecheck = TimeSpan.FromMilliseconds(500);
    /// <summary>The same <see cref="Error"/> reason is raised at most once per this window.</summary>
    public static readonly TimeSpan ErrorRepeatAfter = TimeSpan.FromSeconds(60);
    /// <summary>After this many consecutive move failures, retry only on the 2 s poll (not every 250 ms).</summary>
    public const int FastRetryLimit = 5;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

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

    public string PrintPath => Path.Combine(spoolDir, "print.pdf");
    public string JobsDir => Path.Combine(spoolDir, "jobs");

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
                using (var fs = new FileStream(PrintPath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var head = new byte[4];
                    isPdf = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length &&
                            head is [(byte)'%', (byte)'P', (byte)'D', (byte)'F'];
                }

                if (ct.IsCancellationRequested) return;
                firstSeen = null;
                if (!isPdf)
                {
                    File.Delete(PrintPath);
                    Report($"print.pdf was not a PDF ({size1} bytes); deleted it",
                        new InvalidDataException("print.pdf does not start with %PDF"), rateLimited: false);
                    continue;
                }

                var id = Guid.NewGuid();
                var dest = Path.Combine(JobsDir, id + ".pdf");
                try
                {
                    Directory.CreateDirectory(JobsDir);
                    File.Move(PrintPath, dest);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (!File.Exists(PrintPath)) continue; // it vanished: nothing to move
                    int n = ++_moveFailures;
                    Report($"cannot move print.pdf into {JobsDir}: {e.Message}", e, rateLimited: true);
                    if (n >= FastRetryLimit)
                    {
                        if (n == FastRetryLimit) Log?.Invoke($"spool: {n} move failures in a row; retrying on the 2 s poll only");
                        return;
                    }
                    await Task.Delay(RetryEvery, ct);
                    continue;
                }
                _moveFailures = 0;

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
