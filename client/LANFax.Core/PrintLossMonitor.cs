namespace LANFax.Core;

/// <summary>
/// Matches the jobs the spooler finished on the LAN Fax queue (<see cref="IPrintJobSource"/>) against the files the
/// watcher claimed from the port (<see cref="FileClaimed"/>). Each finished job writes print.pdf once, so a finished job
/// left without a claimed file for <see cref="Grace"/> means its file was replaced before it was claimed: that is
/// raised through <see cref="Lost"/>, once per unmatched job, never silently dropped. Matching is by count, not by job:
/// the port file carries no job id. A claim may come slightly before its job is reported finished (the port closes the
/// file before the job's status changes), so an unmatched claim waits <see cref="Grace"/> for its job too. While a
/// file sits unclaimed at the port nothing is declared lost (the watcher is still retrying).
/// <para>
/// Because a claim may belong to a job not reported yet, a pairing is only trusted (<see cref="Matched"/>, which
/// deletes the kept job) once no job reported within <see cref="Grace"/> after it can be left without a file. When one
/// is (<see cref="Lost"/>), the jobs paired shortly before it are raised through <see cref="Doubtful"/>: the lost file
/// may be theirs, so they are printed again too (duplicates rather than a silent loss). After discarded notifications
/// (<see cref="IPrintJobSource.Unreliable"/>) nothing in flight can be trusted: pending and paired jobs stop being
/// followed, their claims are handed on (<see cref="ClaimUnpaired"/>) for the kept-job sweep to count, and nothing is
/// paired for the next <see cref="Grace"/>.
/// </para>
/// </summary>
public sealed class PrintLossMonitor : IDisposable
{
    /// <summary>How old a pairing must be for <see cref="SettleForExit"/> to trust it.</summary>
    public static readonly TimeSpan ExitSlack = TimeSpan.FromSeconds(2);

    private readonly record struct Entry(int Id, DateTime At, long Seq);

    private readonly IPrintJobSource _jobs;
    private readonly Func<bool> _fileAtPort;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly List<Entry> _unclaimedJobs = new(); // oldest first; At/Seq: when the job was reported
    private readonly Queue<DateTime> _unmatchedClaims = new();
    private readonly List<Entry> _paired = new(); // paired, not yet trusted; At/Seq: when the pair was made
    private long _seq;
    private DateTime? _quietUntil;
    private Timer? _timer;

    /// <param name="fileAtPort">True while print.pdf exists (a job the watcher has not claimed yet).</param>
    public PrintLossMonitor(IPrintJobSource jobs, Func<bool> fileAtPort, Func<DateTime>? clock = null)
    {
        _jobs = jobs;
        _fileAtPort = fileAtPort;
        _clock = clock ?? (() => DateTime.UtcNow);
        _jobs.JobFinished += OnJobFinished;
        _jobs.Unreliable += OnUnreliable;
    }

    /// <summary>How long a finished job may wait for its claimed file (and a claim for its job).</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The spooler job ids finished without a claimed file. Raised on the checking thread.</summary>
    public event Action<int>? Lost;

    /// <summary>
    /// A finished job's pairing with a claimed file is trusted: the newest unclaimed job took the claim (the port keeps
    /// only the last file written) and no job reported just after it was lost. Raised from <see cref="Check"/> or
    /// <see cref="SettleForExit"/>.
    /// </summary>
    public event Action<int>? Matched;

    /// <summary>
    /// A paired job whose claim may have been a lost job's file (paired shortly before that job was reported): print
    /// it again as well. Raised from <see cref="Check"/>, before the <see cref="Lost"/> it goes with.
    /// </summary>
    public event Action<int>? Doubtful;

    /// <summary>
    /// A claim no followed job accounts for (no job reported in time, or dropped by a reset), with its time. Raised on
    /// the thread that dropped it; the kept-job sweep counts these against the jobs nobody followed.
    /// </summary>
    public event Action<DateTime>? ClaimUnpaired;

    /// <summary>Whether this job finished and is still waiting for its claimed file (not matched, not yet lost).</summary>
    public bool IsPending(int id)
    {
        lock (_gate) return _unclaimedJobs.Exists(j => j.Id == id);
    }

    /// <summary>Whether this job is still the loss check's business: pending, or paired and not yet trusted.</summary>
    public bool IsFollowed(int id)
    {
        lock (_gate) return _unclaimedJobs.Exists(j => j.Id == id) || _paired.Exists(j => j.Id == id);
    }

    public Action<string>? Log { get; set; }

    /// <summary>
    /// Jobs not to count: the app deleted them from the queue itself (a kept job printed earlier, whose deletion the
    /// spooler reports as PRINTED|DELETING). Called on the job source's thread.
    /// </summary>
    public Func<int, bool>? IgnoreJob { get; set; }

    /// <summary>Whether the job source still reports finished jobs (false once its watch died).</summary>
    public bool IsRunning => _jobs.IsRunning;

    /// <summary>Starts the job source; with <paramref name="checkEvery"/>, also a timer that calls <see cref="Check"/>.</summary>
    public void Start(TimeSpan? checkEvery = null)
    {
        _jobs.Start();
        if (checkEvery is { } every) _timer = new Timer(_ => { try { Check(); } catch (Exception e) { Log?.Invoke("loss check failed: " + e.Message); } }, null, every, every);
    }

    private bool Quiet(DateTime now) => _quietUntil is { } until && now < until;

    /// <summary>The watcher took a file off the port (to jobs\ or rejected\).</summary>
    public void FileClaimed()
    {
        DateTime? unpaired = null;
        lock (_gate)
        {
            var now = _clock();
            long seq = ++_seq;
            if (Quiet(now)) unpaired = now; // may be the file of a job the discarded notifications reported
            // The port keeps only the last job written, so a claim belongs to the newest unclaimed job.
            else if (_unclaimedJobs.Count > 0)
            {
                _paired.Add(_unclaimedJobs[^1] with { At = now, Seq = seq });
                _unclaimedJobs.RemoveAt(_unclaimedJobs.Count - 1);
            }
            else _unmatchedClaims.Enqueue(now);
        }
        if (unpaired is { } at) Raise(ClaimUnpaired, at);
    }

    private void OnJobFinished(int id)
    {
        if (IgnoreJob?.Invoke(id) == true) { Log?.Invoke($"spooler job {id} deleted by LANFax itself: not counted"); return; }
        lock (_gate)
        {
            var now = _clock();
            long seq = ++_seq;
            if (Quiet(now)) { Log?.Invoke($"spooler job {id} finished just after a reset: left to the kept-job sweep"); return; }
            if (_unmatchedClaims.Count > 0) { _unmatchedClaims.Dequeue(); _paired.Add(new Entry(id, now, seq)); }
            else _unclaimedJobs.Add(new Entry(id, now, seq));
        }
    }

    private void OnUnreliable(string why)
    {
        var handedOn = new List<DateTime>();
        lock (_gate)
        {
            Log?.Invoke($"loss check reset ({why}); {_unclaimedJobs.Count} unmatched job(s), {_paired.Count} unconfirmed pair(s) and {_unmatchedClaims.Count} unmatched claim(s) left to the kept-job sweep");
            handedOn.AddRange(_unmatchedClaims);
            handedOn.AddRange(_paired.Select(p => p.At));
            _unclaimedJobs.Clear();
            _unmatchedClaims.Clear();
            _paired.Clear();
            _quietUntil = _clock() + Grace;
        }
        foreach (var at in handedOn) Raise(ClaimUnpaired, at);
    }

    /// <summary>
    /// Declares every finished job still without a file after <see cref="Grace"/> lost (with the pairs it casts doubt
    /// on), hands on claims no job took, and trusts pairs nothing can undo any more; returns how many were lost.
    /// </summary>
    public int Check()
    {
        var lost = new List<int>();
        var doubtful = new List<int>();
        var expired = new List<DateTime>();
        List<int> settled;
        bool atPort = _fileAtPort();
        lock (_gate)
        {
            var now = _clock();
            while (_unmatchedClaims.Count > 0 && now - _unmatchedClaims.Peek() > Grace) expired.Add(_unmatchedClaims.Dequeue());
            if (!atPort)
                while (_unclaimedJobs.Count > 0 && now - _unclaimedJobs[0].At > Grace)
                {
                    var k = _unclaimedJobs[0];
                    _unclaimedJobs.RemoveAt(0);
                    lost.Add(k.Id);
                    foreach (var p in _paired.Where(p => CouldBeFileOf(p, k)).ToList())
                    {
                        doubtful.Add(p.Id);
                        _paired.Remove(p);
                        Log?.Invoke($"spooler job {p.Id} was paired with a claim that may have been job {k.Id}'s file: printed again too");
                    }
                }
            settled = Settle(now, Grace, strictlyOlder: true);
        }
        foreach (var at in expired) Raise(ClaimUnpaired, at);
        foreach (var id in doubtful) Raise(Doubtful, id);
        foreach (var id in lost)
        {
            Log?.Invoke($"spooler job {id} finished but no print file was claimed for it: replaced at the port");
            Raise(Lost, id);
        }
        foreach (var id in settled) Raise(Matched, id);
        return lost.Count;
    }

    /// <summary>
    /// The app is closing: trusts the pairs at least <see cref="ExitSlack"/> old that no pending job casts doubt on.
    /// The others stay kept in the queue, and the next start prints them again (a duplicate, never a loss).
    /// </summary>
    public void SettleForExit()
    {
        List<int> settled;
        lock (_gate) settled = Settle(_clock(), ExitSlack, strictlyOlder: false);
        foreach (var id in settled) Raise(Matched, id);
    }

    /// <summary>A claim paired before job <paramref name="k"/> was reported, and close enough, may be k's file.</summary>
    private bool CouldBeFileOf(Entry pair, Entry k) => pair.Seq < k.Seq && k.At - pair.At <= Grace;

    private List<int> Settle(DateTime now, TimeSpan age, bool strictlyOlder)
    {
        var settled = _paired
            .Where(p => (strictlyOlder ? now - p.At > age : now - p.At >= age) && !_unclaimedJobs.Exists(k => CouldBeFileOf(p, k)))
            .ToList();
        foreach (var p in settled) _paired.Remove(p);
        return settled.Select(p => p.Id).ToList();
    }

    private static void Raise<T>(Action<T>? handlers, T arg)
    {
        foreach (var h in handlers?.GetInvocationList() ?? [])
            try { ((Action<T>)h)(arg); } catch { /* a faulty subscriber must not stop the others */ }
    }

    /// <summary>
    /// Stops the checks. Claims still in flight (unmatched, or paired and not yet trusted) are handed on through
    /// <see cref="ClaimUnpaired"/>: their jobs are no longer followed, and the kept-job sweep accounts for them.
    /// </summary>
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _jobs.JobFinished -= OnJobFinished;
        _jobs.Unreliable -= OnUnreliable;
        _jobs.Stop();
        var handedOn = new List<DateTime>();
        lock (_gate)
        {
            handedOn.AddRange(_unmatchedClaims);
            handedOn.AddRange(_paired.Select(p => p.At));
            _unclaimedJobs.Clear();
            _unmatchedClaims.Clear();
            _paired.Clear();
        }
        foreach (var at in handedOn) Raise(ClaimUnpaired, at);
    }
}
