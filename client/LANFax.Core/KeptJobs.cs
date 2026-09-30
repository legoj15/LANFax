using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LANFax.Core;

/// <summary>A job the LAN Fax queue kept after printing it (PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS).</summary>
/// <param name="Position">
/// Its place in the queue. NOT the print order: jobs spooled at the same time print in the order they finish
/// spooling (measured, probe -Crash 2026-09-30).
/// </param>
/// <param name="Submitted">UTC; with <paramref name="Id"/> it tells a reused job id apart.</param>
/// <param name="Pages">JOB_INFO_1 TotalPages; 0 when unknown.</param>
public sealed record KeptJob(int Id, int Position, string Document, string User, DateTime Submitted, int Pages = 0);

/// <summary>The queue's kept printed jobs. Delete and Restart throw <see cref="Win32Exception"/> (5: not this user's job).</summary>
public interface IKeptJobStore
{
    /// <summary>Kept jobs whose status says printed, in queue order.</summary>
    IReadOnlyList<KeptJob> Printed();
    void Delete(int id);
    /// <summary>Prints the job again (it writes print.pdf once more).</summary>
    void Restart(int id);
}

/// <summary>EnumJobs / SetJob on one printer. Needs PRINTER_ACCESS_USE; SetJob needs the job's owner (or an admin).</summary>
public sealed class SpoolerKeptJobs(string printerName) : IKeptJobStore
{
    private const uint JobControlRestart = 4, JobControlDelete = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobInfo1
    {
        public uint JobId;
        public IntPtr PrinterName, MachineName, UserName, Document, Datatype, StatusText;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public SystemTime Submitted;
    }

    public IReadOnlyList<KeptJob> Printed()
    {
        var h = Winspool.Open(printerName, Winspool.PrinterAccessUse);
        try
        {
            Winspool.EnumJobs(h, 0, uint.MaxValue, 1, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0) return [];
            var buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!Winspool.EnumJobs(h, 0, uint.MaxValue, 1, buf, needed, out _, out var returned))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var list = new List<KeptJob>();
                int size = Marshal.SizeOf<JobInfo1>();
                for (int i = 0; i < returned; i++)
                {
                    var j = Marshal.PtrToStructure<JobInfo1>(buf + i * size);
                    if ((j.Status & SpoolerJobSource.JobStatusPrinted) == 0 || (j.Status & SpoolerJobSource.JobStatusDeleted) != 0) continue;
                    var t = j.Submitted;
                    DateTime submitted;
                    try { submitted = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Milliseconds, DateTimeKind.Utc); }
                    catch (ArgumentOutOfRangeException) { submitted = default; }
                    list.Add(new KeptJob((int)j.JobId, (int)j.Position, Marshal.PtrToStringUni(j.Document) ?? "",
                        Marshal.PtrToStringUni(j.UserName) ?? "", submitted, (int)j.TotalPages));
                }
                return list.OrderBy(k => k.Position).ThenBy(k => k.Id).ToList();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Winspool.ClosePrinter(h); }
    }

    public void Delete(int id) => Control(id, JobControlDelete);
    public void Restart(int id) => Control(id, JobControlRestart);

    private void Control(int id, uint command)
    {
        var h = Winspool.Open(printerName, Winspool.PrinterAccessUse);
        try
        {
            if (!Winspool.SetJob(h, (uint)id, 0, IntPtr.Zero, command)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Winspool.ClosePrinter(h); }
    }
}

/// <summary>
/// Kept jobs the app is done with but could not delete (another user's), in spoolDir\kept-jobs.json, so the next
/// start does not print them again. Keyed on id and submit time (ids restart after a spooler reset). Written only
/// when it changes; an unreadable file counts as empty (logged) and is rewritten.
/// </summary>
public sealed class KeptJobLedger(string path)
{
    private sealed record Entry(int Id, DateTime Submitted);

    private HashSet<Entry>? _entries;

    public Action<string>? Log { get; set; }

    public bool Contains(KeptJob j) => Load().Contains(new Entry(j.Id, j.Submitted));

    public void Add(KeptJob j)
    {
        if (Load().Add(new Entry(j.Id, j.Submitted))) Save();
    }

    /// <summary>Drops entries for jobs no longer kept.</summary>
    public void Prune(IReadOnlyList<KeptJob> kept)
    {
        var live = kept.Select(k => new Entry(k.Id, k.Submitted)).ToHashSet();
        if (Load().RemoveWhere(e => !live.Contains(e)) > 0) Save();
    }

    private HashSet<Entry> Load()
    {
        if (_entries != null) return _entries;
        _entries = new HashSet<Entry>();
        try
        {
            if (File.Exists(path))
                foreach (var e in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? []) _entries.Add(e);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"kept-job ledger {path} unreadable ({e.Message}); starting empty");
        }
        return _entries;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries!.OrderBy(e => e.Id).ToList()));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log?.Invoke($"kept-job ledger {path} not written: {e.Message}");
        }
    }
}

/// <summary>What <see cref="KeptJobRecovery.Reprint"/> did with a job whose file may have been replaced.</summary>
public enum ReprintOutcome
{
    /// <summary>Printing again: the watcher claims it like any job.</summary>
    PrintingAgain,
    /// <summary>The queue does not keep it (KEEPPRINTEDJOBS off, or deleted meanwhile): nothing to print again.</summary>
    NotKept,
    /// <summary>Kept, but it cannot be printed again (another user's job, or reprinted too often): report it.</summary>
    Refused,
}

/// <summary>
/// Uses the jobs the LAN Fax queue keeps after printing to get replaced documents back. At start
/// (<see cref="RecoverAtStart"/>) every kept printed job the app has not dealt with was printed while no watcher ran
/// (the app killed or crashed with the queue printing): the port holds only the last one's file, so the others are
/// printed again, one by one, for the watcher to claim. While running, a job the loss check trusted as matched to a
/// claimed file is deleted (<see cref="Claimed"/>, then <see cref="Drain"/> off the watcher's thread), a job it found
/// replaced (or doubtful) is printed again (<see cref="Reprint"/>, at most <see cref="MaxReprints"/> times), and
/// <see cref="Sweep"/> accounts for printed jobs the loss check did not follow by counting the claims it did not pair
/// (<see cref="ClaimUnpaired"/>): as many claims as jobs, they are deleted; fewer, they are all printed again, because
/// which one was replaced cannot be told. Nothing is deleted that no claim accounts for. A job that cannot be deleted
/// goes to the <see cref="KeptJobLedger"/>. Spooler failures are logged, never thrown. Thread-safe.
/// </summary>
public sealed class KeptJobRecovery
{
    public const int MaxReprints = 2;
    private const int ErrorInvalidParameter = 87; // SetJob on a job id that no longer exists

    private readonly IKeptJobStore _store;
    private readonly KeptJobLedger _ledger;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly Queue<int> _claimed = new();
    private readonly Dictionary<int, int> _reprints = new();
    private readonly Dictionary<int, DateTime> _firstSeen = new();
    private readonly HashSet<int> _deletedHere = new(); // guarded by itself: read from the job source's thread
    private readonly Queue<int> _deletedOrder = new();
    private readonly List<DateTime> _unpaired = new(); // guarded by itself: added from the watcher and job threads
    private string? _lastError;

    public KeptJobRecovery(IKeptJobStore store, KeptJobLedger ledger, Func<DateTime>? clock = null)
    {
        _store = store;
        _ledger = ledger;
        _clock = clock ?? (() => DateTime.UtcNow);
        _ledger.Log ??= m => Log?.Invoke(m);
    }

    public Action<string>? Log { get; set; }

    /// <summary>
    /// How long a printed job nobody follows stays before <see cref="Sweep"/> accounts for it (a claim for it may still
    /// be handed on by the loss check), and how long an unneeded claim is kept for a job still to appear.
    /// </summary>
    public TimeSpan SettleAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A file was claimed that no job the loss check followed accounts for (the check not running, a reset, no job
    /// reported for it in time). Only recorded; <see cref="Sweep"/>, <see cref="SettleAtExit"/> and
    /// <see cref="RecoverAtStart"/> count these against the kept jobs nobody followed.
    /// </summary>
    public void ClaimUnpaired(DateTime at)
    {
        lock (_unpaired) _unpaired.Add(at);
    }

    private int UnpairedCount { get { lock (_unpaired) return _unpaired.Count; } }

    /// <summary>Spends <paramref name="n"/> claims (the oldest), or all of them with null.</summary>
    private void SpendClaims(int? n)
    {
        lock (_unpaired)
        {
            if (n is { } k) _unpaired.RemoveRange(0, Math.Min(k, _unpaired.Count));
            else _unpaired.Clear();
        }
    }

    public sealed record StartResult(int Reprinted, IReadOnlyList<KeptJob> Unrecovered);

    /// <summary>
    /// Call with the queue held, after a print.pdf left from the last run was claimed. <paramref name="leftoverAtPort"/>:
    /// print.pdf existed before the watcher started (it belongs to the last job printed); <paramref name="leftoverPages"/>
    /// is its page count, which names that job when exactly one kept job has it (queue position does not: jobs spooled
    /// together print out of queue order). With no such job, every job is printed again (one duplicate of the leftover
    /// rather than deleting the wrong one). Jobs the running loss check already follows (<paramref name="followed"/>)
    /// are left to it. The claims handed on so far (before the loss check ran: the leftover, and any job a refused
    /// pause let print meanwhile) are spent here: as many as the jobs, and every job's file was claimed.
    /// </summary>
    public StartResult RecoverAtStart(bool leftoverAtPort, Func<int, bool> followed, int? leftoverPages = null)
    {
        lock (_gate)
        {
            int claims = UnpairedCount;
            SpendClaims(null);
            if (!TryList(out var kept)) return new StartResult(0, []);
            _ledger.Prune(kept);
            var todo = kept.Where(k => !_ledger.Contains(k) && !followed(k.Id)).OrderBy(k => k.Position).ThenBy(k => k.Id).ToList();
            if (todo.Count == 0) return new StartResult(0, []);
            if (claims >= todo.Count)
            {
                Log?.Invoke($"{todo.Count} kept job(s) printed before the loss check started, {claims} file(s) claimed since start: none replaced");
                foreach (var k in todo) Account(k);
                return new StartResult(0, []);
            }
            if (leftoverAtPort)
            {
                var owners = leftoverPages is int p and > 0 ? todo.Where(k => k.Pages == p).ToList() : [];
                if (owners.Count == 1)
                {
                    var owner = owners[0];
                    Log?.Invoke($"kept job {owner.Id} ({owner.Document}, {owner.Pages} page(s)) printed last before start: its file was the one left at the port");
                    Account(owner);
                    todo.Remove(owner);
                }
                else
                    Log?.Invoke($"the leftover's job cannot be told ({(leftoverPages is int n ? $"{n} page(s), {owners.Count} kept job(s) with that count" : "page count unknown")}): every kept job is printed again, one is a duplicate of it");
            }
            int reprinted = 0;
            var unrecovered = new List<KeptJob>();
            foreach (var k in todo)
            {
                if (TryRestart(k)) { reprinted++; continue; }
                unrecovered.Add(k);
                Account(k);
            }
            if (reprinted > 0) Log?.Invoke($"{reprinted} document(s) printed while no LANFax watched the port are printed again");
            return new StartResult(reprinted, unrecovered);
        }
    }

    /// <summary>
    /// True for a job this recovery deleted (recorded before the delete call). Deleting a kept job makes the spooler
    /// report PRINTED|DELETING for it, which a job source that never saw it print counts as a finish; the loss check
    /// skips these (<see cref="PrintLossMonitor.IgnoreJob"/>).
    /// </summary>
    public bool DeletedHere(int id)
    {
        lock (_deletedHere) return _deletedHere.Contains(id);
    }

    /// <summary>The loss check matched this job to a claimed file. Only queues it (runs inside the watcher's claim loop).</summary>
    public void Claimed(int id)
    {
        lock (_claimed) _claimed.Enqueue(id);
    }

    /// <summary>Deletes the jobs queued by <see cref="Claimed"/>.</summary>
    public void Drain()
    {
        lock (_gate)
        {
            while (true)
            {
                int id;
                lock (_claimed) { if (!_claimed.TryDequeue(out id)) return; }
                _reprints.Remove(id);
                _firstSeen.Remove(id);
                try { Delete(id); }
                catch (Win32Exception e) when (e.NativeErrorCode == ErrorInvalidParameter) { }
                catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
                {
                    // Not ours to delete: remember it so no start prints it again.
                    if (TryList(out var kept) && kept.FirstOrDefault(k => k.Id == id) is { } job) Remember(job, e);
                }
            }
        }
    }

    /// <summary>The loss check found this job's file replaced. True when it is printing again; false: report it.</summary>
    public bool Lost(int id) => Reprint(id) == ReprintOutcome.PrintingAgain;

    /// <summary>
    /// This job's file was (or may have been) replaced: prints it again, at most <see cref="MaxReprints"/> times. A
    /// job that cannot be printed again is done with (deleted, or remembered when that is refused).
    /// </summary>
    public ReprintOutcome Reprint(int id)
    {
        lock (_gate)
        {
            if (!TryList(out var kept) || kept.FirstOrDefault(k => k.Id == id) is not { } job) return ReprintOutcome.NotKept;
            return ReprintKept(job) ? ReprintOutcome.PrintingAgain : ReprintOutcome.Refused;
        }
    }

    private bool ReprintKept(KeptJob job)
    {
        _reprints.TryGetValue(job.Id, out var n);
        _firstSeen.Remove(job.Id);
        if (n < MaxReprints && TryRestart(job))
        {
            _reprints[job.Id] = n + 1;
            return true;
        }
        if (n >= MaxReprints) Log?.Invoke($"kept job {job.Id} ({job.Document}) replaced again after {n} reprints; giving up");
        Account(job);
        return false;
    }

    /// <summary>
    /// Deletes claimed jobs, then accounts for the kept printed jobs nobody follows (not <paramref name="followed"/> by
    /// the loss check) once every one of them was seen for <see cref="SettleAfter"/>: with as many unpaired claims as
    /// jobs they are deleted, otherwise all of them are printed again. Returns the jobs that could not be printed
    /// again (to report; each is reported once).
    /// </summary>
    public IReadOnlyList<KeptJob> Sweep(Func<int, bool> followed)
    {
        Drain();
        lock (_gate)
        {
            if (!TryList(out var kept)) return [];
            _ledger.Prune(kept);
            var now = _clock();
            var live = kept.Select(k => k.Id).ToHashSet();
            foreach (var id in _firstSeen.Keys.Where(id => !live.Contains(id)).ToList()) _firstSeen.Remove(id);
            var unfollowed = new List<KeptJob>();
            foreach (var k in kept)
            {
                if (_ledger.Contains(k) || followed(k.Id)) { _firstSeen.Remove(k.Id); continue; }
                _firstSeen.TryAdd(k.Id, now);
                unfollowed.Add(k);
            }
            if (unfollowed.Count == 0)
            {
                lock (_unpaired) _unpaired.RemoveAll(at => now - at > SettleAfter);
                return [];
            }
            if (unfollowed.Exists(k => now - _firstSeen[k.Id] <= SettleAfter)) return [];
            return Account(unfollowed, UnpairedCount, "printed while the loss check did not follow them");
        }
    }

    /// <summary>
    /// The app is closing: deletes the claimed jobs, and the kept printed jobs nobody follows when there are as many
    /// unpaired claims as jobs. Otherwise they stay kept, and the next start prints them again.
    /// </summary>
    public void SettleAtExit(Func<int, bool> followed)
    {
        Drain();
        lock (_gate)
        {
            if (!TryList(out var kept)) return;
            var unfollowed = kept.Where(k => !_ledger.Contains(k) && !followed(k.Id)).ToList();
            if (unfollowed.Count == 0) return;
            int claims = UnpairedCount;
            if (claims < unfollowed.Count)
            {
                Log?.Invoke($"{unfollowed.Count} kept job(s) nobody followed but only {claims} claimed file(s) at exit: left for the next start to print again");
                return;
            }
            SpendClaims(unfollowed.Count);
            Log?.Invoke($"{unfollowed.Count} kept job(s) nobody followed, as many files claimed: deleted at exit");
            foreach (var k in unfollowed) Account(k);
        }
    }

    private List<KeptJob> Account(List<KeptJob> jobs, int claims, string why)
    {
        var unrecovered = new List<KeptJob>();
        if (claims >= jobs.Count)
        {
            SpendClaims(jobs.Count);
            Log?.Invoke($"{jobs.Count} kept job(s) {why}, {claims} claimed file(s) account for them: deleted");
            foreach (var k in jobs) { _firstSeen.Remove(k.Id); Account(k); }
            return unrecovered;
        }
        SpendClaims(null);
        Log?.Invoke($"{jobs.Count} kept job(s) {why} but only {claims} claimed file(s): which were replaced cannot be told, all printed again");
        foreach (var k in jobs)
            if (!ReprintKept(k)) unrecovered.Add(k);
        return unrecovered;
    }

    private bool TryList(out IReadOnlyList<KeptJob> kept)
    {
        try
        {
            kept = _store.Printed();
            _lastError = null;
            return true;
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            if (e.Message != _lastError) Log?.Invoke($"kept jobs unavailable: {e.Message} ({(e as Win32Exception)?.NativeErrorCode})");
            _lastError = e.Message;
            kept = [];
            return false;
        }
    }

    private bool TryRestart(KeptJob k)
    {
        try
        {
            _store.Restart(k.Id);
            Log?.Invoke($"kept job {k.Id} ({k.Document}, {k.User}) printed again");
            return true;
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            Log?.Invoke($"kept job {k.Id} ({k.Document}, {k.User}) cannot be printed again: {e.Message}");
            return false;
        }
    }

    /// <summary>Done with this job: delete it, or remember it when that is refused.</summary>
    private void Account(KeptJob k)
    {
        _reprints.Remove(k.Id);
        try { Delete(k.Id); }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorInvalidParameter) { }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException) { Remember(k, e); }
    }

    private void Delete(int id)
    {
        lock (_deletedHere)
        {
            if (_deletedHere.Add(id)) _deletedOrder.Enqueue(id);
            while (_deletedOrder.Count > 256) _deletedHere.Remove(_deletedOrder.Dequeue());
        }
        _store.Delete(id);
    }

    private void Remember(KeptJob k, Exception why)
    {
        Log?.Invoke($"kept job {k.Id} ({k.Document}, {k.User}) not deleted ({why.Message}); remembered as done");
        _ledger.Add(k);
    }
}
