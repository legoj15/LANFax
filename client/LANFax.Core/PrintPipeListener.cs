using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LANFax.Core;

/// <summary>
/// Receives the "LAN Fax" printer's output on the local named pipe <c>\\.\pipe\&lt;name&gt;</c> (the printer's port).
/// The spooler opens one connection per print job, writes the PDF and closes it, so every job is its own
/// connection and nothing can overwrite anything (measured 2026-09-30, docs/client-spec.md "Spool path and the printer").
/// <para>
/// Each connection is streamed straight to <c>spool\jobs\&lt;guid&gt;.pdf.part</c> (never buffered in memory), flushed
/// to disk, then renamed to <c>&lt;guid&gt;.pdf</c> and raised through <see cref="NewPrintJob"/>. A job that is not
/// one whole PDF (no leading <c>%PDF</c>, not exactly one <c>%%EOF</c>, or anything but PDF whitespace after it, which
/// includes a connection that ended early) or is larger than <see cref="MaxJobBytes"/> is moved to <c>spool\rejected\</c> and reported through <see cref="Error"/>;
/// nothing is deleted except the empty file of a connection that delivered no bytes (only logged). Jobs are handled
/// one at a time in arrival order while a spare pipe instance waits, so the spooler's next connection never finds
/// the pipe busy. A connection silent for <see cref="IdleTimeout"/> is dropped (a job so far is rejected), so a
/// client that connects and sends nothing cannot hold the printer.
/// </para>
/// <para>
/// The pipe denies NT AUTHORITY\NETWORK (named pipes are reachable over SMB), gives SYSTEM and the user running the
/// listener full access and lets every signed-in user write a job (the spooler opens the port as the job's owner;
/// see <see cref="CreateSecurity"/>). It is created with FILE_FLAG_FIRST_PIPE_INSTANCE: if the name
/// already exists (a second LANFax, another signed-in user's LANFax, or a squatter) the listener reports it through
/// <see cref="Error"/>, keeps trying every <see cref="NameRetry"/>, reports it again every
/// <see cref="NameTakenRepeat"/> while it lasts, and serves the pipe once the name is free. A fixed name cannot be
/// reserved: any local program that creates it first receives the print jobs, so this detects squatting, it does not
/// prevent it.
/// </para>
/// </summary>
public sealed class PrintPipeListener(string spoolDir, string pipeName = PrintPipeListener.DefaultPipeName) : IPrintListener
{
    public const string DefaultPipeName = "LANFax";
    /// <summary>Jobs larger than this are rejected (a fax of hundreds of pages is still far below it).</summary>
    public const long DefaultMaxJobBytes = 200L * 1024 * 1024;
    /// <summary>The same infrastructure <see cref="Error"/> reason is raised at most once per this window.</summary>
    public static readonly TimeSpan ErrorRepeatAfter = TimeSpan.FromSeconds(60);
    /// <summary>Two instances: the one handling a job and a spare waiting for the next connection.</summary>
    public const int MaxInstances = 2;
    /// <summary>
    /// How long <see cref="Stop"/> lets a job in progress finish. A job cut off mid-write waits in the printer queue in
    /// "Error" and the spooler does not retry it by itself (measured 2026-09-30), so it is worth a short wait. The
    /// spooler writes while the driver renders: a 40-page job took several seconds and did not fit in 1.5 s.
    /// </summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);
    /// <summary>The longest <see cref="Stop"/> blocks: the grace plus time for the last rename.</summary>
    public static readonly TimeSpan StopWait = StopGrace + TimeSpan.FromSeconds(2);
    private const int BufferBytes = 64 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _reported = new();
    private CancellationTokenSource? _accept; // cancelled by Stop: no new connections, no more raising
    private CancellationTokenSource? _abort;  // cancelled StopGrace after Stop: a job still arriving is cut off
    private Task? _loop;
    private Task? _lingering; // a loop Stop gave up waiting for; the next Start waits for it before sweeping .part files
    private Task? _stopping;  // the loop the last Stop waits for, and until when: a concurrent Stop waits for it too
    private DateTime _stopDeadline;
    private volatile bool _serving;
    private volatile bool _receiving;

    public event Action<Guid, string>? NewPrintJob;

    /// <summary>
    /// A print job cannot be handled, or the pipe cannot be served: (reason, cause). Rejected jobs are reported
    /// every time; pipe failures at most once per <see cref="ErrorRepeatAfter"/>. Raised on a thread-pool thread
    /// (or on the caller's thread from <see cref="Start"/>).
    /// </summary>
    public event Action<string, Exception>? Error;

    /// <summary>Optional diagnostics sink.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Set before <see cref="Start"/>.</summary>
    public long MaxJobBytes { get; set; } = DefaultMaxJobBytes;

    /// <summary>While another program holds the pipe name, how often the listener tries to take it. Set before <see cref="Start"/>.</summary>
    public TimeSpan NameRetry { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>While another program holds the pipe name, how often that is reported again. Set before <see cref="Start"/>.</summary>
    public TimeSpan NameTakenRepeat { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A connection that delivers no byte for this long is dropped. Any signed-in user may open the pipe, and jobs are
    /// read one at a time, so a client that connects and sends nothing would otherwise stall every later job. Generous,
    /// because the spooler writes while the driver renders. Set before <see cref="Start"/>.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public string PipeName => pipeName;
    public string PipePath => PortName(pipeName);
    public string JobsDir => Path.Combine(spoolDir, "jobs");
    public string RejectedDir => Path.Combine(spoolDir, "rejected");

    /// <summary>The printer port name for a pipe: <c>\\.\pipe\&lt;name&gt;</c>.</summary>
    public static string PortName(string pipeName) => @"\\.\pipe\" + pipeName;

    /// <summary>True while the pipe is served (false while another program holds the name).</summary>
    public bool IsListening { get { lock (_gate) return _serving && _loop is { IsCompleted: false }; } }

    /// <summary>
    /// True from <see cref="Start"/> until <see cref="Stop"/> or until the listener stopped by itself (an unexpected
    /// fault, reported through <see cref="Error"/>). Unlike <see cref="IsListening"/> it stays true while another
    /// program holds the name and the listener keeps retrying. False means nothing will serve the pipe until the next
    /// <see cref="Start"/>.
    /// </summary>
    public bool IsRunning { get { lock (_gate) return _loop is { IsCompleted: false }; } }

    /// <summary>
    /// True while a print job is in flight: from the moment a client is connected until its file is saved (renamed
    /// and raised, or set aside). A <see cref="Stop"/> now waits up to <see cref="StopGrace"/>; the tray blocks Windows
    /// shutdown meanwhile.
    /// </summary>
    public bool IsReceiving => _receiving;

    /// <summary>
    /// <see cref="IsReceiving"/> changed: true when a job starts arriving, false once it is saved. Once per job, in
    /// that order, raised on a thread-pool thread (also while stopping, so a shutdown block is always released).
    /// </summary>
    public event Action<bool>? ReceivingChanged;

    /// <summary>
    /// What any signed-in user gets: what the spooler asks for when it opens the port for that user's job, and no
    /// more (0x13019b; measured 2026-09-30 with Probe-PipePrinter.ps1 -WhoIs -OtherUser: GENERIC_READ|GENERIC_WRITE
    /// without DELETE was refused, this passed). Read on an inbound pipe gives a client nothing to read. Left out:
    /// FILE_CREATE_PIPE_INSTANCE (0x4, which would let any user open a second server instance of this pipe and take
    /// jobs), WRITE_DAC and WRITE_OWNER.
    /// </summary>
    public const PipeAccessRights PrintingUserRights =
        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize | PipeAccessRights.Delete;

    /// <summary>
    /// Deny NETWORK; allow SYSTEM and the current user; let every signed-in user (Authenticated Users) write a job.
    /// The spooler opens the port as the job's owner (measured 2026-09-30: with only SYSTEM allowed the job was
    /// refused and waited in "Error, Printing"), so without the last rule another user's print job would never
    /// arrive. Writing a job is what printing to the printer already lets every user do.
    /// </summary>
    public static PipeSecurity CreateSecurity()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var me = WindowsIdentity.GetCurrent();
        ps.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PrintingUserRights, AccessControlType.Allow));
        return ps;
    }

    private NamedPipeServerStream CreateInstance(bool first) =>
        NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, MaxInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, CreateSecurity());

    /// <summary>Number of <c>%%EOF</c> markers from the stream's current position to its end.</summary>
    public static int CountEof(Stream s)
    {
        var buf = new byte[BufferBytes];
        var counter = new EofCounter();
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) counter.Feed(buf.AsSpan(0, n));
        return counter.Count;
    }

    /// <summary>
    /// The acceptance rule on a file (docs/client-spec.md "Print job acceptance"): starts with <c>%PDF</c>, exactly one
    /// <c>%%EOF</c> with nothing but PDF whitespace after it, at most <paramref name="maxBytes"/>. Opened for reading
    /// with FileShare.Read, so a file someone is still writing throws <see cref="IOException"/>.
    /// </summary>
    public static bool IsOneCompletePdf(string path, long maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes);
        if (fs.Length < 4 || fs.Length > maxBytes) return false;
        var head = new byte[4];
        fs.ReadExactly(head);
        if (head is not [(byte)'%', (byte)'P', (byte)'D', (byte)'F']) return false;
        var counter = new EofCounter();
        counter.Feed(head);
        var buf = new byte[BufferBytes];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0) counter.Feed(buf.AsSpan(0, n));
        return counter.Count == 1 && counter.EndsWithEof;
    }

    /// <summary>
    /// Why the first instance could not be created: ERROR_ACCESS_DENIED from FILE_FLAG_FIRST_PIPE_INSTANCE means the
    /// name exists. While <paramref name="previous"/> (a loop the last Stop gave up on) still runs, the holder is
    /// most likely that loop, not another program (with both its instances open the create fails as "all pipe
    /// instances are busy" instead).
    /// </summary>
    private string CannotTake(Exception e, Task? previous) =>
        previous is { IsCompleted: false } ? StillFinishing
        : e is UnauthorizedAccessException ? NameTaken
        : $"cannot open {PipePath}: {e.Message}";

    private string StillFinishing => $"the previous listener on {PipePath} is still finishing a job; print jobs are taken again once it is done";

    private string NameTaken => $"another program is already listening on {PipePath} (a second LANFax, LANFax of another signed-in user, or a program posing as it); print jobs go there, not here";

    /// <summary>
    /// Creates the folders, moves stale <c>.part</c> files to rejected\ and starts serving the pipe. When the name is
    /// taken it raises <see cref="Error"/> (on the caller's thread), <see cref="IsListening"/> stays false and the
    /// listener keeps trying in the background until <see cref="Stop"/>.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;
            try { _lingering?.Wait(StopWait); } catch (AggregateException) { }
            var previous = _lingering is { IsCompleted: false } ? _lingering : null;
            bool stillWriting = previous != null;
            _lingering = null;
            Directory.CreateDirectory(JobsDir);
            // A loop the last Stop gave up on may still be writing its .part: that one is not stale. Swept next start.
            if (stillWriting) Log?.Invoke("pipe: the previous listener is still finishing a job; stale .part files are left for the next start");
            else MoveStaleParts();
            NamedPipeServerStream? first = null;
            try { first = CreateInstance(first: true); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { Report(CannotTake(e, previous), e, rateLimited: false); }
            _accept = new CancellationTokenSource();
            _abort = new CancellationTokenSource();
            var accept = _accept.Token;
            var abort = _abort.Token;
            _serving = first != null;
            _loop = Task.Run(() => RunAsync(first, accept, abort, previous));
        }
    }

    /// <summary>
    /// Stops serving: no new connection is taken and nothing is raised any more (a job rejected or failing during the
    /// grace is only logged). A job in progress gets
    /// <see cref="StopGrace"/> to finish, and so does a job the spooler already started on the spare instance; they
    /// are kept in jobs\, where the next start offers them as unsent. After the grace a job is cut off and its
    /// <c>.part</c> moved to rejected\ (the document waits in the printer queue in "Error" until someone restarts it
    /// there). A connection that arrives in the instant between the last check and closing the pipe is dropped the
    /// same way; only pausing the printer first would close that. Blocks at most <see cref="StopWait"/>; if the loop
    /// is still busy then (a slow disk), it is logged, finishes in the background, and the next <see cref="Start"/>
    /// waits for it. A Stop while another is in progress (the tray's Exit, then WM_ENDSESSION) waits for the same
    /// loop, until the first one's <see cref="StopWait"/> runs out.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? accept, abort;
        Task? loop, pending = null;
        TimeSpan left = TimeSpan.Zero;
        lock (_gate)
        {
            accept = _accept; abort = _abort; loop = _loop; _accept = null; _abort = null; _loop = null;
            if (accept == null) { pending = _stopping; left = _stopDeadline - DateTime.UtcNow; }
            else { _stopping = loop; _stopDeadline = DateTime.UtcNow + StopWait; }
        }
        if (accept == null || abort == null)
        {
            if (pending is { IsCompleted: false } && left > TimeSpan.Zero)
            {
                try { pending.Wait(left); } catch (AggregateException) { /* reported by the loop */ }
            }
            return;
        }
        accept.Cancel();
        abort.CancelAfter(StopGrace);
        bool done = true;
        try { done = loop == null || loop.Wait(StopWait); }
        catch (AggregateException) { /* faults were reported by the loop */ }
        abort.Cancel();
        if (!done)
        {
            // The loop may still read the tokens: leave the sources to the GC rather than dispose them under it.
            Log?.Invoke($"pipe: listener still stopping after {StopWait.TotalSeconds:0} s; it finishes in the background");
            lock (_gate) _lingering = loop;
            return;
        }
        accept.Dispose();
        abort.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// A <c>.part</c> left in jobs\ at start goes to rejected\, never back into jobs\. Beside its saved
    /// <c>&lt;guid&gt;.pdf</c> it is the copy a failed rename left behind (logged). One complete PDF without its
    /// <c>.pdf</c> is either such a copy whose job was since sent or cancelled, or a job whose rename and copy both
    /// failed; the two cannot be told apart and offering a faxed job again would fax it twice, so it is set aside and
    /// reported through <see cref="Error"/>. Anything else is an interrupted job (logged).
    /// </summary>
    private void MoveStaleParts()
    {
        foreach (var part in Directory.EnumerateFiles(JobsDir, "*.part").ToList())
        {
            try
            {
                var done = part[..^".part".Length];
                if (done.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(done))
                    {
                        Log?.Invoke($"pipe: {Path.GetFileName(part)} is a copy of the saved {Path.GetFileName(done)} (its rename had failed); moved to {MoveToRejected(part)}");
                        continue;
                    }
                    if (IsOneCompletePdf(part, MaxJobBytes))
                    {
                        // Its copy may have been delivered and sent (or cancelled) since: never offered again.
                        var aside = MoveToRejected(part, Path.GetFileName(done));
                        Report($"a complete print job from an earlier session was set aside as {aside}; it may already have been faxed (its rename had failed), so check before faxing it",
                            new IOException("complete .part without its .pdf: " + part), rateLimited: false);
                        continue;
                    }
                }
                var dest = MoveToRejected(part);
                Log?.Invoke($"pipe: stale {Path.GetFileName(part)} from an interrupted job moved to {dest}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Report($"cannot move the stale {Path.GetFileName(part)} into {RejectedDir}: {e.Message}", e, rateLimited: true);
            }
        }
    }

    /// <summary>
    /// Takes the pipe name with FILE_FLAG_FIRST_PIPE_INSTANCE (retrying while another program holds it), then serves it
    /// until stopped. When every instance is gone (a client came and went before a spare existed, or the spare could
    /// not be created) the name is free for a moment, so it is taken again the same way: a program that grabbed it in
    /// that moment is reported, never joined.
    /// </summary>
    private async Task RunAsync(NamedPipeServerStream? first, CancellationToken accept, CancellationToken abort, Task? previous)
    {
        // Start reported a taken name already; a name lost later is reported at once.
        var reported = first == null ? DateTime.UtcNow : DateTime.MinValue;
        try
        {
            while (true)
            {
                bool failed = false;
                while (first == null)
                {
                    accept.ThrowIfCancellationRequested();
                    try { first = CreateInstance(first: true); }
                    catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                    {
                        failed = true;
                        if (DateTime.UtcNow - reported >= NameTakenRepeat)
                        {
                            reported = DateTime.UtcNow;
                            Report(CannotTake(e, previous), e, rateLimited: false);
                        }
                        await Task.Delay(NameRetry, accept);
                    }
                }
                if (failed) Log?.Invoke($"pipe: {PipePath} is free again; listening");
                _serving = true;
                bool lost;
                try { lost = await ServeAsync(first, accept, abort); }
                finally { _serving = false; }
                first = null;
                if (!lost) return;
            }
        }
        catch (OperationCanceledException) when (accept.IsCancellationRequested) { }
    }

    /// <summary>
    /// Whether a client is on <paramref name="conn"/>. Waits for one until <paramref name="accept"/> is cancelled;
    /// once stopping it never waits, but still takes a client that is already there (an instance accepts clients
    /// from its creation until it is closed, so the spooler may have started its next job on the spare).
    /// </summary>
    private static async Task<bool> ConnectedAsync(NamedPipeServerStream conn, CancellationToken accept)
    {
        if (!accept.IsCancellationRequested)
        {
            try { await conn.WaitForConnectionAsync(accept); return true; }
            catch (OperationCanceledException) when (accept.IsCancellationRequested) { }
        }
        using var now = new CancellationTokenSource();
        var check = conn.WaitForConnectionAsync(now.Token); // completes at once when a client is already connected
        if (!check.IsCompleted) now.Cancel();
        try { await check; return true; }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; } // a client that came and went
    }

    /// <summary>Serves the pipe. True when every instance is gone and the name must be taken again; false when stopped.</summary>
    private async Task<bool> ServeAsync(NamedPipeServerStream waiting, CancellationToken accept, CancellationToken abort)
    {
        NamedPipeServerStream? spare = waiting;
        try
        {
            while (true)
            {
                if (spare == null) return !accept.IsCancellationRequested;
                var conn = spare;
                spare = null;
                try
                {
                    if (!await ConnectedAsync(conn, accept)) return false;
                    // A spare waits while this job is handled: the spooler's next connection never finds no instance.
                    // Not once stopping: no new instance is opened for connections that would only be dropped.
                    if (!accept.IsCancellationRequested)
                    {
                        try { spare = CreateInstance(first: false); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Invoke("pipe: no spare instance: " + e.Message); }
                    }
                    SetReceiving(true);
                    try { await ReceiveAsync(conn, accept, abort); }
                    finally { SetReceiving(false); }
                }
                catch (OperationCanceledException) when (accept.IsCancellationRequested) { return false; }
                catch (IOException e)
                {
                    // WaitForConnection failing (a client that came and went): serve the next one.
                    Log?.Invoke("pipe: connection failed: " + e.Message);
                }
                finally
                {
                    await conn.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException) when (accept.IsCancellationRequested) { return false; }
        catch (Exception e) { Report("print pipe listener stopped: " + e.Message, e, rateLimited: false); return false; }
        finally
        {
            if (spare != null) await spare.DisposeAsync();
        }
    }

    /// <summary>One job: stream to .part, then validate and rename (or reject). Returns when the connection is done.</summary>
    private async Task ReceiveAsync(NamedPipeServerStream conn, CancellationToken stopping, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var part = Path.Combine(JobsDir, id + ".pdf.part");
        var buf = new byte[BufferBytes];
        var eof = new EofCounter();
        var head = new byte[4];
        int headLen = 0;
        long total = 0, written = 0;
        bool oversize = false, idle = false;
        Exception? broken = null;
        FileStream? fs = null;
        try
        {
            Directory.CreateDirectory(JobsDir);
            fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.Read, BufferBytes, FileOptions.Asynchronous);
            while (true)
            {
                int n;
                using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    quiet.CancelAfter(IdleTimeout);
                    try { n = await conn.ReadAsync(buf, quiet.Token); }
                    catch (IOException e) { broken = e; break; } // not a clean end of stream
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { idle = true; break; }
                }
                if (n == 0) break; // the client closed: the job is complete (or it went away early: validation decides)
                total += n;
                if (headLen < head.Length)
                {
                    int take = Math.Min(head.Length - headLen, n);
                    Array.Copy(buf, 0, head, headLen, take);
                    headLen += take;
                }
                if (oversize) continue; // read and drop the rest, so the spooler's write succeeds and it does not retry forever
                int keep = (int)Math.Min(n, MaxJobBytes - written);
                if (keep > 0)
                {
                    eof.Feed(buf.AsSpan(0, keep));
                    await fs.WriteAsync(buf.AsMemory(0, keep), ct);
                    written += keep;
                }
                if (keep < n) oversize = true;
            }
            fs.Flush(flushToDisk: true);
            await fs.DisposeAsync();
            fs = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (fs != null) await fs.DisposeAsync();
            fs = null;
            SetAside(part, $"stopped while a job was arriving (not finished within {StopGrace.TotalSeconds:0.#} s)");
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Our side failed (disk full, jobs\ not writable). Dropping the connection makes the spooler's write fail:
            // the document waits in the printer queue in "Error" until it is restarted there (measured).
            if (fs != null) { try { await fs.DisposeAsync(); } catch (IOException) { } }
            fs = null;
            SetAside(part, "write failed");
            Report($"cannot save a print job in {JobsDir}: {e.Message}. The document waits in the printer queue with an error: restart it there once this is fixed",
                e, rateLimited: true, stopping);
            return;
        }

        if (total == 0 && idle)
        {
            // A client that connected and sent nothing (not the spooler, which writes at once): dropped so the printer
            // is not held. Rate-limited: a program doing it in a loop gets one warning a minute.
            try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Invoke($"pipe: cannot delete the empty {part}: {e.Message}"); }
            Report($"a program connected to {PipePath} and sent nothing for {IdleTimeout.TotalSeconds:0.#} s; disconnected it so print jobs are not held up",
                new TimeoutException(), rateLimited: true, stopping);
            return;
        }
        if (total == 0)
        {
            // Nothing arrived: nothing to keep or send, and no document to warn about. The empty file goes.
            Log?.Invoke($"pipe: a connection ended without data{(broken != null ? " (" + broken.Message + ")" : "")}; ignored");
            try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Invoke($"pipe: cannot delete the empty {part}: {e.Message}"); }
            return;
        }

        bool isPdf = headLen == 4 && head is [(byte)'%', (byte)'P', (byte)'D', (byte)'F'];
        string? reason =
            idle ? $"no data arrived for {IdleTimeout.TotalSeconds:0.#} s after {total} bytes; if the document shows an error in the printer queue, restart it there"
            : broken != null ? $"the connection broke after {total} bytes ({broken.Message}); if the document shows an error in the printer queue, restart it there"
            : oversize ? $"it is too large ({total / (1024 * 1024)} MB; the limit is {MaxJobBytes / (1024 * 1024)} MB)"
            : !isPdf ? $"it is not a PDF ({total} bytes)"
            : !eof.EndsWithEof ? $"it is not a complete PDF ({total} bytes, {(eof.Count == 0 ? "no %%EOF" : "data after the last %%EOF")}; the connection ended early)"
            : eof.Count != 1 ? $"it is not one complete PDF ({total} bytes, {eof.Count} %%EOF markers; Print To PDF writes one)"
            : null;
        if (reason != null)
        {
            string where;
            try { where = MoveToRejected(part, id + ".pdf"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { where = part + " (could not move it: " + e.Message + ")"; }
            Report($"a print job was rejected: {reason}. Kept as {where}", new InvalidDataException(reason), rateLimited: false, stopping);
            return;
        }

        var dest = Path.Combine(JobsDir, id + ".pdf");
        try { MoveFile(part, dest); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Something (a scanner) still holds the file without FILE_SHARE_DELETE: copy the whole job into place
            // instead. The tracker only sees <id>.pdf, so a job left as .part would never be offered in this session.
            try { File.Copy(part, dest); }
            catch (Exception c) when (c is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(dest); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { } // our own partial copy, if any
                Report($"a print job arrived but could not be saved as {dest} ({c.Message}); it is kept as {part}, moved to {RejectedDir} at the next start of LANFax: print it again", c, rateLimited: false, stopping);
                return;
            }
            Log?.Invoke($"pipe: renaming {Path.GetFileName(part)} failed ({e.Message}); copied to {dest}");
            try { File.Delete(part); }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException) { Log?.Invoke($"pipe: cannot delete {part} (a copy of {dest}); set aside at the next start: {d.Message}"); }
        }
        Log?.Invoke($"pipe: job {id} received, {total} bytes");
        // Complete and on disk. Stopped meanwhile: it stays in jobs\, where the next start offers it as unsent.
        if (stopping.IsCancellationRequested) { Log?.Invoke($"pipe: job {id} finished while stopping; kept for the next start"); return; }
        try { NewPrintJob?.Invoke(id, dest); } catch { /* subscriber bug: the file is safely in jobs\ */ }
    }

    private void SetReceiving(bool on)
    {
        _receiving = on;
        try { ReceivingChanged?.Invoke(on); } catch { /* a faulty subscriber must not stop the listener */ }
    }

    /// <summary>A .part that will not be completed goes to rejected\ (never deleted).</summary>
    private void SetAside(string part, string why)
    {
        if (!File.Exists(part)) return;
        try { Log?.Invoke($"pipe: {why}; {Path.GetFileName(part)} moved to {MoveToRejected(part)}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Invoke($"pipe: {why}; cannot move {part}: {e.Message}"); }
    }

    /// <summary>Moves a file into rejected\ under its own name (or <paramref name="name"/>), never overwriting.</summary>
    private string MoveToRejected(string path, string? name = null)
    {
        Directory.CreateDirectory(RejectedDir);
        name ??= Path.GetFileName(path);
        var dest = Path.Combine(RejectedDir, name);
        for (int i = 1; File.Exists(dest); i++) dest = Path.Combine(RejectedDir, $"{i}-{name}");
        MoveFile(path, dest);
        return dest;
    }

    /// <summary>
    /// File.Move, retried for about a second: a scanner (Defender) may hold a file that was just written for a moment.
    /// </summary>
    private static void MoveFile(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(from, to); return; }
            catch (IOException) when (attempt < 10 && File.Exists(from) && !File.Exists(to)) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) when (attempt < 10 && File.Exists(from)) { Thread.Sleep(100); }
        }
    }

    /// <summary>Logs and raises <see cref="Error"/>; once <paramref name="stopping"/> is cancelled it only logs.</summary>
    private void Report(string reason, Exception e, bool rateLimited, CancellationToken stopping = default)
    {
        if (stopping.IsCancellationRequested) { Log?.Invoke("pipe (stopping): " + reason); return; }
        if (rateLimited)
        {
            var now = DateTime.UtcNow;
            lock (_reported)
            {
                if (_reported.TryGetValue(reason, out var last) && now - last < ErrorRepeatAfter) return;
                _reported[reason] = now;
            }
        }
        Log?.Invoke("pipe: " + reason);
        try { Error?.Invoke(reason, e); } catch { /* a faulty subscriber must not stop the listener */ }
    }
}

/// <summary>
/// Counts <c>%%EOF</c> markers in data fed in chunks of any size, including markers split across chunks (a
/// streaming matcher: the state is how much of the marker the last bytes matched).
/// </summary>
public sealed class EofCounter
{
    private static readonly byte[] Marker = "%%EOF"u8.ToArray();
    private int _matched;

    private bool _clean; // nothing but PDF whitespace since the last marker

    public int Count { get; private set; }

    /// <summary>
    /// True when the data seen so far ends with <c>%%EOF</c>, optionally followed by PDF whitespace. A PDF that
    /// stopped after an earlier section (linearized, incremental update) holds a <c>%%EOF</c> but does not end with one.
    /// </summary>
    public bool EndsWithEof => Count > 0 && _clean;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        int m = _matched;
        foreach (var b in chunk)
        {
            if (b == Marker[m])
            {
                if (++m == Marker.Length) { Count++; m = 0; _clean = true; continue; }
            }
            else if (b == (byte)'%') m = m == 2 ? 2 : 1; // "%%%" still ends in "%%"; after "%%E"/"%%EO", '%' restarts at 1
            else m = 0;
            if (b is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0)) _clean = false;
        }
        _matched = m;
    }
}
