using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using LANFax.Core;
using Microsoft.Win32.SafeHandles;

// Live probe of a printer on a pipe port, with only the LANFax PrintPipeListener running (no tray app, no server).
// Refuses a printer whose port is not \\.\pipe\<pipe>, and a queue that is not empty (use --drain first).
//
//   --printer "LANFax Pipe Probe"  --pipe LANFaxProbe  --out <dir, used as the listener's spool dir>
//   --count N (5)       prints N jobs back to back with 1..N pages; expects N files with /Count exactly {1..N}
//   --no-listener S     prints first with no listener, waits S seconds (queue statuses shown), then listens
//   --drain             only listens until the queue is empty (leftovers of an aborted run), keeps what arrives
//   --stop-mid-job S    one large job; the listener stops mid-job for S seconds, then listens S seconds: does the
//                       spooler retry by itself? (if not, the job is restarted so the queue is left empty)
//   --same              prints N identical 1-page jobs (same name, same content) and compares their bytes: an
//                       intentional reprint must not be byte-identical, or duplicate suppression would swallow it
//   --whois             one 1-page job read by a raw pipe server that reports who the pipe client is
//   --other-user        with --whois: the pipe gets the listener's DACL WITHOUT the rule for the printing user, so
//                       this job faces the pipe as another signed-in user's job would (the spooler opens the port as
//                       the job owner). A refused job is then delivered to a normal listener
//
// Exit code 0 = PASS.

var opt = Args.Parse(args);
var t0 = Stopwatch.StartNew();
void Say(string m) => Console.WriteLine($"[{t0.Elapsed.TotalSeconds,7:0.000}] {m}");

var port = Queue.Port(opt.Printer);
var expectedPort = PrintPipeListener.PortName(opt.Pipe);
if (!string.Equals(port, expectedPort, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"printer \"{opt.Printer}\" prints to \"{port}\", not {expectedPort}; refusing.");
    return 2;
}
Directory.CreateDirectory(opt.Out);
Say($"printer \"{opt.Printer}\" on {port}; files in {opt.Out}");

var before = Queue.Jobs(opt.Printer);
if (before.Count > 0 && !opt.Drain)
{
    Console.Error.WriteLine($"the queue is not empty ({before.Count} job(s)); run with -Drain first:");
    foreach (var j in before) Console.Error.WriteLine("  " + j);
    return 2;
}

if (opt.WhoIs) return WhoIs.Run(opt, Say);

var received = new List<(Guid id, string path, double at)>();
var errors = new List<string>();
using var listener = new PrintPipeListener(opt.Out, opt.Pipe) { Log = m => Say("listener: " + m) };
listener.NewPrintJob += (id, p) => { lock (received) received.Add((id, p, t0.Elapsed.TotalSeconds)); Say($"received {Path.GetFileName(p)} ({new FileInfo(p).Length} bytes)"); };
listener.Error += (r, _) => { lock (errors) errors.Add(r); Say("listener ERROR: " + r); };

if (opt.Drain)
{
    listener.Start();
    if (!listener.IsListening) return 1;
    // A job whose connection broke mid-job waits in "Error" and is not retried by the spooler (measured): restart it.
    foreach (var id in Queue.ErrorJobIds(opt.Printer)) { Say($"restarting job {id} (in error)"); Queue.Restart(opt.Printer, id); }
    var end = DateTime.UtcNow.AddMinutes(10);
    while (Queue.Jobs(opt.Printer).Count > 0 && DateTime.UtcNow < end) Thread.Sleep(1000);
    Thread.Sleep(1000);
    listener.Stop();
    var left = Queue.Jobs(opt.Printer);
    Say($"drain: {received.Count} job(s) received, {errors.Count} error(s), {left.Count} left in the queue");
    return left.Count == 0 ? 0 : 1;
}

if (opt.StopMidJobSeconds is int observe)
{
    // One large job; the listener stops once the job's .part exists (the spooler is mid-write), then stays down for
    // `observe` seconds, then listens again for `observe` seconds: does the spooler retry the job by itself?
    int bigPages = Math.Max(opt.Count, 40);
    listener.Start();
    var jobsDir = Path.Combine(opt.Out, "jobs");
    Printer.Print(opt.Printer, $"LANFax pipe probe stop-mid-job ({bigPages} pages)", bigPages);
    var partDeadline = DateTime.UtcNow.AddSeconds(60);
    while (!Directory.EnumerateFiles(jobsDir, "*.part").Any() && received.Count == 0 && DateTime.UtcNow < partDeadline) Thread.Sleep(5);
    if (received.Count > 0) { Say("FAIL: the job completed before the listener could be stopped; use a larger -Count"); return 1; }
    listener.Stop();
    var keptAtStop = Directory.EnumerateFiles(jobsDir, "*.pdf").ToList();
    if (keptAtStop.Count > 0)
    {
        // Finished within PrintPipeListener.StopGrace: kept for the next start, the spooler's write succeeded.
        foreach (var k in keptAtStop) Say($"finished within the stop grace ({PrintPipeListener.StopGrace.TotalSeconds:0.#} s) and kept: {Path.GetFileName(k)}, /Count {Pdf.PageCount(k)}, %%EOF x{Eofs(k)}");
        var leftGrace = Queue.Jobs(opt.Printer);
        Say($"left in queue: {leftGrace.Count}");
        bool graceOk = keptAtStop.Count == 1 && Pdf.PageCount(keptAtStop[0]) == bigPages && leftGrace.Count == 0;
        Console.WriteLine(graceOk ? "RESULT: PASS (stop grace)" : "RESULT: FAIL");
        return graceOk ? 0 : 1;
    }
    Say("listener stopped mid-job");
    var up = DateTime.UtcNow.AddSeconds(observe);
    while (DateTime.UtcNow < up) { Thread.Sleep(5000); foreach (var j in Queue.Jobs(opt.Printer)) Say("  queue: " + j); }
    listener.Start();
    Say("listener started again");
    var retryUntil = DateTime.UtcNow.AddSeconds(observe);
    while (received.Count == 0 && DateTime.UtcNow < retryUntil) { Thread.Sleep(5000); foreach (var j in Queue.Jobs(opt.Printer)) Say("  queue: " + j); }
    bool retried = received.Count > 0;
    Say(retried ? "the spooler retried the job by itself" : $"the spooler did NOT retry the job within {observe} s");
    if (!retried)
    {
        foreach (var id in Queue.ErrorJobIds(opt.Printer)) { Say($"Restart-PrintJob {id}"); Queue.Restart(opt.Printer, id); }
        var d = DateTime.UtcNow.AddSeconds(60);
        while (received.Count == 0 && DateTime.UtcNow < d) Thread.Sleep(500);
    }
    Thread.Sleep(1500);
    listener.Stop();
    var whole = received.Select(r => (r.path, pages: Pdf.PageCount(r.path), eofs: Eofs(r.path))).ToList();
    foreach (var w in whole) Say($"file {Path.GetFileName(w.path)}: /Count {w.pages}, %%EOF x{w.eofs}");
    var leftStop = Queue.Jobs(opt.Printer);
    Say($"retried by the spooler: {retried}; delivered whole: {whole.Count == 1 && whole[0].pages == bigPages && whole[0].eofs == 1}; left in queue: {leftStop.Count}");
    bool ok = whole.Count == 1 && whole[0].pages == bigPages && leftStop.Count == 0;
    Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
    return ok ? 0 : 1;
}

if (opt.Same)
{
    int n = Math.Max(2, opt.Count);
    listener.Start();
    for (int k = 1; k <= n; k++) { Printer.Print(opt.Printer, "LANFax pipe probe same", pages: 1); } // back to back: the worst case for a timestamp
    var until = DateTime.UtcNow.AddSeconds(90 + 10 * n);
    while (DateTime.UtcNow < until) { lock (received) if (received.Count >= n && Queue.Jobs(opt.Printer).Count == 0) break; Thread.Sleep(250); }
    Thread.Sleep(1500);
    listener.Stop();
    var hashes = received.Select(r => (r.path, len: new FileInfo(r.path).Length, sha: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(r.path))))).ToList();
    foreach (var h in hashes) Say($"file {Path.GetFileName(h.path)}: {h.len} bytes, sha256 {h.sha}");
    int distinct = hashes.Select(h => h.sha).Distinct().Count();
    var leftSame = Queue.Jobs(opt.Printer);
    Say($"same content printed {n} times: {hashes.Count} files, {distinct} distinct; left in queue: {leftSame.Count}");
    bool differ = hashes.Count == n && distinct == n && leftSame.Count == 0;
    Console.WriteLine(differ ? "RESULT: PASS (every reprint differs byte-wise)" : "RESULT: FAIL (identical reprints, or missing files)");
    return differ ? 0 : 1;
}

if (opt.NoListenerSeconds == null) listener.Start();
for (int k = 1; k <= opt.Count; k++) Printer.Print(opt.Printer, $"LANFax pipe probe {k}/{opt.Count}", pages: k);
Say($"printed {opt.Count} job(s) with 1..{opt.Count} pages");

if (opt.NoListenerSeconds is int wait)
{
    Say($"no listener for {wait} s");
    var until = DateTime.UtcNow.AddSeconds(wait);
    while (DateTime.UtcNow < until) { Thread.Sleep(Math.Min(10_000, (int)Math.Max(1, (until - DateTime.UtcNow).TotalMilliseconds))); foreach (var j in Queue.Jobs(opt.Printer)) Say("  queue: " + j); }
    listener.Start();
    Say("listener started");
}
if (!listener.IsListening) { Say("FAIL: the listener did not start"); return 1; }

var deadline = DateTime.UtcNow.AddSeconds(90 + 10 * opt.Count + (opt.NoListenerSeconds is null ? 0 : 300));
while (DateTime.UtcNow < deadline)
{
    lock (received) if (received.Count >= opt.Count && Queue.Jobs(opt.Printer).Count == 0) break;
    Thread.Sleep(250);
}
Thread.Sleep(1500); // anything extra would arrive now
listener.Stop();

var got = received.Select(r => (r.path, pages: Pdf.PageCount(r.path), eofs: Eofs(r.path), r.at)).ToList();
foreach (var g in got) Say($"file {Path.GetFileName(g.path)}: /Count {g.pages}, %%EOF x{g.eofs}, at {g.at:0.000}");
var pages = got.Select(g => g.pages).ToList();
var left2 = Queue.Jobs(opt.Printer);
bool exact = pages.OrderBy(p => p).SequenceEqual(Enumerable.Range(1, opt.Count));
bool inOrder = pages.SequenceEqual(Enumerable.Range(1, opt.Count));
bool oneEof = got.All(g => g.eofs == 1);
Say($"files {got.Count} of {opt.Count}; page counts [{string.Join(",", pages)}]; exactly 1..{opt.Count}: {exact}; in print order: {inOrder}; one %%EOF each: {oneEof}; listener errors: {errors.Count}; left in queue: {left2.Count}");
foreach (var j in left2) Say("  left: " + j);
bool pass = got.Count == opt.Count && exact && oneEof && errors.Count == 0 && left2.Count == 0;
Console.WriteLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
return pass ? 0 : 1;

static int Eofs(string path) { using var f = File.OpenRead(path); return PrintPipeListener.CountEof(f); }

sealed record Options(string Printer, string Pipe, string Out, int Count, int? NoListenerSeconds, bool Drain, bool WhoIs, int? StopMidJobSeconds, bool OtherUser, bool Same);

static class Args
{
    public static Options Parse(string[] a)
    {
        string printer = "LANFax Pipe Probe", pipe = "LANFaxProbe";
        string outDir = Path.Combine(Path.GetTempPath(), "lanfax-pipe-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        int count = 5;
        int? noListener = null, stopMid = null;
        bool drain = false, whois = false, otherUser = false, same = false;
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--printer": printer = a[++i]; break;
                case "--pipe": pipe = a[++i]; break;
                case "--out": outDir = a[++i]; break;
                case "--count": count = int.Parse(a[++i]); break;
                case "--no-listener": noListener = int.Parse(a[++i]); break;
                case "--drain": drain = true; break;
                case "--stop-mid-job": stopMid = int.Parse(a[++i]); break;
                case "--whois": whois = true; break;
                case "--other-user": otherUser = true; break;
                case "--same": same = true; break;
                default: throw new ArgumentException("unknown option " + a[i]);
            }
        }
        if (count < 1) throw new ArgumentException("--count must be at least 1");
        if (otherUser && !whois) throw new ArgumentException("--other-user goes with --whois");
        return new Options(printer, pipe, outDir, count, noListener, drain, whois, stopMid, otherUser, same);
    }
}

static class Printer
{
    /// <summary>One job with exactly <paramref name="pages"/> pages; returns once it is spooled.</summary>
    public static void Print(string printer, string name, int pages)
    {
        using var pd = new PrintDocument();
        pd.PrinterSettings.PrinterName = printer;
        pd.DocumentName = name;
        pd.PrintController = new StandardPrintController(); // no progress dialog
        int page = 0;
        using var font = new Font("Arial", 24);
        pd.PrintPage += (_, e) =>
        {
            page++;
            e.Graphics!.DrawString($"{name}\npage {page} of {pages}", font, Brushes.Black, 72, 72);
            e.HasMorePages = page < pages;
        };
        pd.Print();
    }
}

static class Pdf
{
    /// <summary>The page tree's /Count (the largest one: the root counts every page).</summary>
    public static int PageCount(string path)
    {
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var counts = Regex.Matches(text, @"/Count\s+(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        return counts.Count == 0 ? -1 : counts.Max();
    }
}

static class Queue
{
    public static string? Port(string printer) => Ps($"(Get-Printer -Name '{printer.Replace("'", "''")}' -ErrorAction SilentlyContinue).PortName").Trim() is { Length: > 0 } s ? s : null;

    public static List<string> Jobs(string printer) =>
        Ps($"Get-PrintJob -PrinterName '{printer.Replace("'", "''")}' -ErrorAction SilentlyContinue | ForEach-Object {{ \"$($_.Id) '$($_.DocumentName)' $($_.JobStatus) $($_.TotalPages)p\" }}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Jobs whose status includes Error (a connection that broke mid-job leaves them there).</summary>
    public static List<int> ErrorJobIds(string printer) =>
        Ps($"Get-PrintJob -PrinterName '{printer.Replace("'", "''")}' -ErrorAction SilentlyContinue | Where-Object {{ \"$($_.JobStatus)\" -match 'Error' }} | ForEach-Object {{ $_.Id }}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();

    /// <summary>Restart-PrintJob: the job's owner may do this without elevation.</summary>
    public static void Restart(string printer, int id) => Ps($"Restart-PrintJob -PrinterName '{printer.Replace("'", "''")}' -ID {id}");

    private static string Ps(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-NoProfile", "-Command", command }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var s = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return s;
    }
}

/// <summary>
/// Who connects to the pipe: a raw server (same ACL as the listener) takes one job, reads the first bytes (Windows
/// allows impersonation only after a read), then reports the client process, GetImpersonationUserName and the
/// impersonation token's user, level and groups.
/// </summary>
static class WhoIs
{
    public static int Run(Options opt, Action<string> say)
    {
        var security = PrintPipeListener.CreateSecurity();
        if (opt.OtherUser)
        {
            security.PurgeAccessRules(WindowsIdentity.GetCurrent().User!);
            say($"pipe DACL: the listener's without the printing user {WindowsIdentity.GetCurrent().Name}: {security.GetSecurityDescriptorSddlForm(AccessControlSections.Access)}");
        }
        var srv = NamedPipeServerStreamAcl.Create(opt.Pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
        using var cts = new CancellationTokenSource();
        var connected = srv.WaitForConnectionAsync(cts.Token);
        Printer.Print(opt.Printer, "LANFax pipe probe whois" + (opt.OtherUser ? " other-user" : ""), 1);
        var wait = TimeSpan.FromSeconds(opt.OtherUser ? 45 : 120);
        bool ok;
        try { ok = connected.Wait(wait); } catch (AggregateException) { ok = false; }
        if (!ok)
        {
            cts.Cancel();
            srv.Dispose();
            say($"no connection in {wait.TotalSeconds:0} s");
            foreach (var j in Queue.Jobs(opt.Printer)) say("  queue: " + j);
            if (!opt.OtherUser) { say("FAIL"); return 1; }
            say("=> refused: another user's job would NOT reach the listener");
            Deliver(opt, say);
            Console.WriteLine("RESULT: FAIL (another user's job is refused)");
            return 3;
        }
        using var _ = srv;
        if (opt.OtherUser) say("=> connected without a rule for the printing user: another user's job reaches the listener");
        var first = new byte[4096];
        int n = srv.Read(first, 0, first.Length);
        say($"connected; first read {n} bytes");
        if (GetNamedPipeClientProcessId(srv.SafePipeHandle, out uint pid))
        {
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch (Exception e) { name = "? " + e.Message; }
            say($"client process: {pid} {name}");
        }
        try { say("GetImpersonationUserName: " + srv.GetImpersonationUserName()); }
        catch (Exception e) { say("GetImpersonationUserName failed: " + e.Message); }
        if (ImpersonateNamedPipeClient(srv.SafePipeHandle))
        {
            try
            {
                if (OpenThreadToken(GetCurrentThread(), 0x0008 /* TOKEN_QUERY */, true, out var tok))
                {
                    using var h = new SafeAccessTokenHandle(tok);
                    using var wi = new WindowsIdentity(h.DangerousGetHandle());
                    say($"client token: {wi.Name} ({wi.User}), impersonation level {wi.ImpersonationLevel}");
                    foreach (var g in wi.Groups!) say($"  group {g} {Name(g)}");
                }
                else say("OpenThreadToken failed: " + Marshal.GetLastWin32Error());
            }
            finally { RevertToSelf(); }
        }
        else say("ImpersonateNamedPipeClient failed: " + Marshal.GetLastWin32Error());
        long total = n;
        var buf = new byte[65536];
        while ((n = srv.Read(buf, 0, buf.Length)) > 0) total += n;
        say($"job read to the end: {total} bytes");
        say("printing user (this process): " + WindowsIdentity.GetCurrent().Name);
        Console.WriteLine(opt.OtherUser ? "RESULT: PASS (another user's job connects)" : "RESULT: PASS");
        return 0;
    }

    /// <summary>A job the refusing pipe left in the queue: a normal listener takes it (restarted when in Error).</summary>
    private static void Deliver(Options opt, Action<string> say)
    {
        using var l = new PrintPipeListener(opt.Out, opt.Pipe) { Log = m => say("listener: " + m) };
        int got = 0;
        l.NewPrintJob += (_, p) => { Interlocked.Increment(ref got); say("delivered to a normal listener: " + Path.GetFileName(p)); };
        l.Start();
        foreach (var id in Queue.ErrorJobIds(opt.Printer)) { say($"restarting job {id} (in error)"); Queue.Restart(opt.Printer, id); }
        var end = DateTime.UtcNow.AddSeconds(120);
        while ((Volatile.Read(ref got) == 0 || Queue.Jobs(opt.Printer).Count > 0) && DateTime.UtcNow < end) Thread.Sleep(500);
        l.Stop();
        say($"left in queue: {Queue.Jobs(opt.Printer).Count}");
    }

    private static string Name(IdentityReference r) { try { return r.Translate(typeof(NTAccount)).Value; } catch { return "?"; } }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle h, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ImpersonateNamedPipeClient(SafePipeHandle h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool RevertToSelf();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
}
