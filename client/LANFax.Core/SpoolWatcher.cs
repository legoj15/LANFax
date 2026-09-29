namespace LANFax.Core;

/// <summary>
/// Watches spoolDir\print.pdf (the file the printer port writes). A file is complete when it has existed
/// for at least 1 s, its size is unchanged across two checks 500 ms apart, and it opens with FileShare.None.
/// It is then moved to spoolDir\jobs\&lt;guid&gt;.pdf (freeing the port path) and <see cref="NewPrintJob"/> fires.
/// A FileSystemWatcher and a 2 s poll both just start the check loop if it is idle.
/// </summary>
public sealed class SpoolWatcher(string spoolDir) : IDisposable
{
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SizeRecheck = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private FileSystemWatcher? _fsw;
    private Timer? _poll;
    private CancellationTokenSource? _cts;
    private int _running;

    public event Action<Guid, string>? NewPrintJob;

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
        cts?.Cancel();
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
            catch (Exception) { /* next poll retries */ }
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

                using (new FileStream(PrintPath, FileMode.Open, FileAccess.Read, FileShare.None)) { }

                var id = Guid.NewGuid();
                var dest = Path.Combine(JobsDir, id + ".pdf");
                Directory.CreateDirectory(JobsDir);
                File.Move(PrintPath, dest);
                firstSeen = null;
                try { NewPrintJob?.Invoke(id, dest); } catch { /* subscriber bug: file is safely in jobs\ */ }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // locked by the spooler, or deleted mid-check: wait and look again
                await Task.Delay(RetryEvery, ct);
            }
        }
    }
}
