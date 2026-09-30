using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.Text;
using System.Text.RegularExpressions;
using LANFax.Core;

// Prints N jobs (1..N pages) back to back to the real LAN Fax printer while only the SpoolWatcher runs, then checks
// that N files were claimed with page counts exactly {1..N}. Also counts the spooler's finished jobs through
// SpoolerJobSource + PrintLossMonitor (the tray app's loss check) and, when the printer keeps printed jobs, deletes
// each matched one through KeptJobRecovery (the tray app's wiring). Probe files are moved out of spool\jobs (so the
// tray app never offers them) into --out, and the probe's kept jobs are deleted. The queue is always left resumed.
//
//   --count N (5)  --printer "LAN Fax"  --spool C:\ProgramData\LANFax\spool  --out <dir>
//   --paused-first   pause the queue, print, then resume: held jobs must drain without loss
//   --no-watch       no watcher: shows what the loss check reports when files are overwritten (expects N-1 lost)
//   --keep-check     clears the printer's "Keep printed documents" and sets it back (the health check's path)
//   --crash          the app dead while N jobs print (queue running, nobody claims), then the app's start-up:
//                    leftover claimed, kept jobs printed again; expects all N documents back (needs KEEPPRINTEDJOBS)

var opt = Args.Parse(args);
var t0 = Stopwatch.StartNew();
void Say(string m) => Console.WriteLine($"[{t0.Elapsed.TotalSeconds,7:0.000}] {m}");

if (Process.GetProcessesByName("LANFax").Length > 0) { Console.Error.WriteLine("The LANFax tray app is running; close it first (it would claim the probe's jobs)."); return 2; }
var printPath = Path.Combine(opt.Spool, "print.pdf");
if (File.Exists(printPath)) { Console.Error.WriteLine($"{printPath} exists (an unclaimed document?); move it away first."); return 2; }
Directory.CreateDirectory(opt.Out);

var queue = new SpoolerPrintQueue(opt.Printer);
if (opt.KeepCheck)
{
    // Clears "Keep printed documents" and sets it back through the app's own SetPrinter path; always left on.
    bool before = queue.KeepsPrintedJobs;
    try
    {
        queue.SetKeepPrintedJobs(false);
        bool cleared = queue.KeepsPrintedJobs;
        queue.KeepPrintedJobs();
        bool after = queue.KeepsPrintedJobs;
        Console.WriteLine($"keep printed documents: before {before}, after clearing {cleared}, after setting {after}");
        Console.WriteLine(!cleared && after ? "RESULT: PASS" : "RESULT: FAIL");
        return !cleared && after ? 0 : 1;
    }
    finally { if (!queue.KeepsPrintedJobs) queue.KeepPrintedJobs(); }
}
var store = new SpoolerKeptJobs(opt.Printer);
bool Probe(KeptJob k) => k.Document.StartsWith("LANFax burst probe", StringComparison.Ordinal);
var foreign = store.Printed().Where(k => !Probe(k)).ToList();
if (foreign.Count > 0) Say($"note: {foreign.Count} kept printed job(s) not from this probe are in the queue; left alone");
var recovery = new KeptJobRecovery(store, new KeptJobLedger(Path.Combine(opt.Out, "kept-jobs.json"))) { Log = m => Say("kept jobs: " + m) };
var claimed = new List<(string path, double at)>();
var finished = new List<(int id, double at)>();
var lost = new List<int>();
var statusLog = new List<string>();
int reprinted = 0;
bool monitoring = false; // like the app: claims before the loss check starts (the leftover) are not counted

using var jobs = new SpoolerJobSource(opt.Printer);
jobs.StatusSeen += (id, st) => { lock (statusLog) statusLog.Add($"{t0.Elapsed.TotalSeconds:0.000} job {id} status 0x{st:X}"); };
jobs.JobFinished += id => { lock (finished) finished.Add((id, t0.Elapsed.TotalSeconds)); Say($"spooler: job {id} printed"); };
jobs.Unreliable += why => Say("spooler: UNRELIABLE " + why);
using var monitor = new PrintLossMonitor(jobs, () => File.Exists(printPath)) { Log = m => Say("loss check: " + m), IgnoreJob = recovery.DeletedHere };
monitor.Lost += id => { lock (lost) lost.Add(id); };
monitor.Matched += id => { recovery.Claimed(id); _ = Task.Run(recovery.Drain); };
monitor.Doubtful += id => Say($"loss check: job {id} doubtful: {recovery.Reprint(id)}");
monitor.ClaimUnpaired += recovery.ClaimUnpaired;

// When the port (re)creates print.pdf: the race margin is from a job's "printed" to the next job's create.
using var portFsw = new FileSystemWatcher(opt.Spool, "print.pdf") { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
portFsw.Created += (_, _) => { lock (statusLog) statusLog.Add($"{t0.Elapsed.TotalSeconds:0.000} print.pdf created"); };

SpoolWatcher? watcher = null;
void StartWatcher()
{
    watcher = new SpoolWatcher(opt.Spool) { Log = m => Say("watcher: " + m) };
    watcher.Claimed += p => { if (Volatile.Read(ref monitoring)) monitor.FileClaimed(); else recovery.ClaimUnpaired(DateTime.UtcNow); lock (claimed) claimed.Add((p, t0.Elapsed.TotalSeconds)); Say("claimed " + Path.GetFileName(p)); };
    watcher.Error += (r, _) => Say("watcher ERROR: " + r);
    watcher.Start();
}
int Waiting() => queue.JobCount - store.Printed().Count; // jobs not printed yet (kept printed jobs stay in the queue)
int ProbeKept() => store.Printed().Count(Probe);

int exit = 1;
try
{
    if (opt.Crash)
    {
        // The app is dead: queue printing, nobody claims, only the loss check's job source is not even running.
        queue.Resume();
        Say("crash mode: no watcher, no loss check while the jobs print");
    }
    else
    {
        monitor.Start();
        monitoring = true;
        if (!opt.NoWatch) StartWatcher();
        if (opt.PausedFirst) { queue.Pause(); Say($"queue paused (IsPaused={queue.IsPaused})"); }
        else queue.Resume();
    }

    // All jobs spooled at once (Print() blocks for about a job's rendering, so one after the other never overlaps):
    // the spooler then runs them through the port back to back.
    await Task.WhenAll(Enumerable.Range(1, opt.Count).Select(k => Task.Run(() =>
    {
        Print(opt.Printer, k, opt.Count);
        Say($"spooled job {k} ({k} page(s))");
    })));

    if (opt.PausedFirst)
    {
        await Task.Delay(3000);
        Say($"held in queue: {queue.JobCount} job(s); resuming");
        queue.Resume();
    }

    if (opt.Crash)
    {
        var printedBy = DateTime.UtcNow.AddSeconds(opt.WaitSeconds);
        while (DateTime.UtcNow < printedBy && (Waiting() > 0 || ProbeKept() < opt.Count)) await Task.Delay(100);
        Say($"all printed while nobody watched: {ProbeKept()} kept printed job(s), print.pdf at the port: {File.Exists(printPath)}");
        // The app's start: hold, note the leftover, watcher, wait for the leftover's claim, loss check, recover, resume.
        queue.Pause();
        bool leftover = File.Exists(printPath);
        StartWatcher();
        Say($"leftover claimed: {watcher!.WaitIdle(TimeSpan.FromSeconds(5))}");
        monitor.Start();
        Volatile.Write(ref monitoring, true);
        string? first; lock (claimed) first = claimed.Count > 0 ? claimed[0].path : null;
        int? leftoverPages = leftover && first != null ? PageCount(File.ReadAllBytes(first)) : null;
        foreach (var k in store.Printed().Where(Probe)) Say($"kept: job {k.Id} position {k.Position} pages {k.Pages} ({k.Document})");
        Say($"leftover pages: {leftoverPages}");
        var r = recovery.RecoverAtStart(leftover, monitor.IsFollowed, leftoverPages);
        reprinted = r.Reprinted;
        Say($"recovery: {r.Reprinted} printed again, {r.Unrecovered.Count} unrecovered");
        queue.Resume();
    }

    var deadline = DateTime.UtcNow.AddSeconds(opt.WaitSeconds);
    int finishTarget = opt.Crash ? reprinted : opt.Count;
    while (DateTime.UtcNow < deadline)
    {
        int done; lock (finished) done = finished.Count;
        int got; lock (claimed) got = claimed.Count;
        if (done >= finishTarget && (opt.NoWatch || got >= opt.Count) && Waiting() == 0) break;
        await Task.Delay(100);
    }
    Say($"queue now holds {Waiting()} unprinted job(s)");

    if (opt.NoWatch && File.Exists(printPath))
    {
        // The watcher arriving late: the port holds only the last job's file.
        var late = Path.Combine(opt.Out, $"late-{Guid.NewGuid():N}.pdf");
        File.Move(printPath, late);
        monitor.FileClaimed();
        lock (claimed) claimed.Add((late, t0.Elapsed.TotalSeconds));
        Say("moved the port file away by hand (one claim)");
    }

    // Past the loss check's grace, then check.
    await Task.Delay(monitor.Grace + TimeSpan.FromSeconds(1));
    monitor.Check();
    recovery.Drain();

    watcher?.Stop();
    var pages = new List<int>();
    var report = new StringBuilder();
    foreach (var (path, at) in claimed.OrderBy(c => c.at))
    {
        var bytes = File.ReadAllBytes(path);
        int count = PageCount(bytes);
        int eofs = SpoolWatcher.CountEof(new MemoryStream(bytes));
        pages.Add(count);
        var dest = Path.Combine(opt.Out, Path.GetFileName(path));
        if (!path.StartsWith(opt.Out, StringComparison.OrdinalIgnoreCase)) File.Move(path, dest);
        report.AppendLine($"  {at,7:0.000}s  /Count {count}  %%EOF {eofs}  {bytes.Length} bytes  -> {dest}");
    }
    var expected = Enumerable.Range(1, opt.Count).ToList();
    var sorted = pages.OrderBy(p => p).ToList();
    int keptLeft = ProbeKept();
    bool ok = opt.NoWatch ? lost.Count == opt.Count - 1 : sorted.SequenceEqual(expected) && lost.Count == 0 && keptLeft == 0;

    Console.WriteLine();
    Console.WriteLine($"mode: {(opt.Crash ? "crash (no watcher while printing, then start-up recovery)" : opt.NoWatch ? "no watcher" : "watcher")}{(opt.PausedFirst ? ", queue paused while printing then resumed" : "")}");
    Console.WriteLine($"printed jobs: {opt.Count}   spooler finished: {finished.Count}   files claimed: {claimed.Count}   loss check lost: {lost.Count}{(opt.Crash ? $"   printed again: {reprinted}" : "")}   probe jobs still kept: {keptLeft}");
    Console.WriteLine($"page counts claimed (sorted): [{string.Join(",", sorted)}]   expected: [{string.Join(",", opt.NoWatch ? [opt.Count] : expected)}]");
    Console.Write(report);
    Console.WriteLine($"gaps between claims (s): [{string.Join(", ", claimed.OrderBy(c => c.at).Select(c => c.at).Zip(claimed.OrderBy(c => c.at).Skip(1).Select(c => c.at), (a, b) => (b - a).ToString("0.000")))}]");
    Console.WriteLine($"spooler job statuses seen: {statusLog.Count}");
    if (opt.Verbose) foreach (var l in statusLog) Console.WriteLine("  " + l);
    Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
    exit = ok ? 0 : 1;
}
finally
{
    watcher?.Dispose();
    try { queue.Resume(); Say($"queue resumed (IsPaused={queue.IsPaused})"); } catch (Exception e) { Say("RESUME FAILED: " + e.Message); exit = 3; }
    // The tray app would print leftover probe jobs again at its next start: delete every one this probe left kept.
    foreach (var k in store.Printed().Where(Probe))
        try { store.Delete(k.Id); Say($"deleted kept probe job {k.Id}"); } catch (Exception e) { Say($"KEPT JOB {k.Id} NOT DELETED: {e.Message}"); exit = exit == 0 ? 5 : exit; }
    if (File.Exists(printPath)) { Say($"WARNING: {printPath} left behind"); exit = exit == 0 ? 4 : exit; }
}
return exit;

static void Print(string printer, int job, int of)
{
    using var doc = new PrintDocument { PrintController = new StandardPrintController() };
    doc.PrinterSettings.PrinterName = printer;
    doc.DocumentName = $"LANFax burst probe {job}/{of}";
    int page = 0;
    using var font = new Font("Arial", 28);
    doc.PrintPage += (_, e) =>
    {
        page++;
        e.Graphics!.DrawString($"Burst probe job {job} of {of}\npage {page} of {job}", font, Brushes.Black, 72, 72);
        e.HasMorePages = page < job;
    };
    doc.Print();
}

static int PageCount(byte[] pdf)
{
    var text = Encoding.Latin1.GetString(pdf);
    int best = 0;
    foreach (Match m in Regex.Matches(text, @"/Type\s*/Pages\b[^>]*?/Count\s+(\d+)|/Count\s+(\d+)[^>]*?/Type\s*/Pages\b"))
        best = Math.Max(best, int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
    if (best == 0) best = Regex.Matches(text, @"/Type\s*/Page\b").Count;
    return best;
}

sealed record Args(int Count, string Printer, string Spool, string Out, bool PausedFirst, bool NoWatch, bool Crash, bool Verbose, int WaitSeconds, bool KeepCheck)
{
    public static Args Parse(string[] a)
    {
        string Get(string name, string def) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : def; }
        return new Args(
            int.Parse(Get("--count", "5")),
            Get("--printer", "LAN Fax"),
            Get("--spool", @"C:\ProgramData\LANFax\spool"),
            Get("--out", Path.Combine(Path.GetTempPath(), "lanfax-burst-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))),
            a.Contains("--paused-first"), a.Contains("--no-watch"), a.Contains("--crash"), a.Contains("--verbose"),
            int.Parse(Get("--wait", "120")), a.Contains("--keep-check"));
    }
}
