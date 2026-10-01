using System.Text.Json;
using LANFax.Core;
using Timer = System.Threading.Timer;

namespace LANFax.App.Logic;

/// <summary>What <see cref="ClientRuntime.Start"/> found: unsent documents to offer, and the printer as it is now.</summary>
public sealed record RuntimeStart(IReadOnlyList<SendRequest> Unsent, PrinterStatus Printer);

/// <summary>
/// Everything the client does that is not UI, owned once per process and shared by the tray and any window: the
/// API clients, the one event stream for the app's lifetime (and its restart on a settings save), the connection and
/// server-status snapshot (arm state included: the stream's and an arm call's answer, <see cref="NoteArm"/>), the
/// monitor's rows and their loads (<see cref="Monitor"/>, kept whether or not a window shows them), the job tracker and
/// its poll, the inbound catch-up, the print listener with its watch and stop, and the printer and stuck-print-job
/// checks.
/// <para>
/// Threading: one owner thread, named by the one <see cref="SynchronizationContext"/> the runtime is given. Its Post
/// must run each callback on that thread, in order (the WinForms UI thread's context does). Stream, listener and timer
/// callbacks arrive on pool threads and are posted through it, and await continuations come back through it because
/// <see cref="Start"/> refuses to run anywhere but under it. Every public member is called on the owner thread, and
/// every event is raised on it, so subscribers touch UI directly; a UI that needs to get back to the owner thread
/// posts through <see cref="Owner"/>. Nothing is raised once <see cref="StopAsync"/> or <see cref="Dispose"/> began.
/// </para>
/// </summary>
public sealed class ClientRuntime : IDisposable, IMonitorSource
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);
    /// <summary>How often the listener and the printer's port (a registry read) are re-checked.</summary>
    public static readonly TimeSpan DefaultPrinterCheckInterval = TimeSpan.FromSeconds(30);
    /// <summary>No stuck-job check before this: a job the spooler is still retrying shows "Error" until it arrives.</summary>
    public static readonly TimeSpan StuckCheckDelay = TimeSpan.FromSeconds(60);
    /// <summary>For this long after the listener starts, each printer check also looks for documents stuck in Error.</summary>
    public static readonly TimeSpan StuckCheckWindow = TimeSpan.FromMinutes(10);
    /// <summary>The longest <see cref="StopAsync"/> waits for the cancelled stream run to end.</summary>
    public static readonly TimeSpan StreamStopWait = TimeSpan.FromSeconds(5);

    private readonly SynchronizationContext _owner;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _sseHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly UnauthorizedNotice _unauthorized = new();
    private readonly ListenerWatch _listenerWatch = new();
    /// <summary>Test-page jobs (no local PDF, so not in the tracker) awaiting their terminal event.</summary>
    private readonly HashSet<string> _submitted = new();
    /// <summary>Stuck queue job ids already announced, so each is announced once.</summary>
    private readonly HashSet<int> _stuckSeen = new();
    /// <summary>Spool ids already offered (at start, as print jobs, or after an install re-scan).</summary>
    private readonly HashSet<Guid> _offeredSpool = new();
    /// <summary>One stream for the app's lifetime (it reads the config at each connect), so LastEventId survives a restart.</summary>
    private readonly EventStream _events;
    private readonly object _sseGate = new();
    private CancellationToken _sseLive; // the token of the run currently allowed to raise events (guarded by _sseGate)
    private CancellationTokenSource? _sseCts;
    /// <summary>
    /// Bumped by every <see cref="RestartStream"/>, run or not (owner thread): a poll answer counts as live only if no
    /// restart came between its question and its answer. A stream token would not do: a restart that starts no run
    /// leaves the cancelled one in place, and every later poll would look stale.
    /// </summary>
    private int _streamGeneration;
    private Task? _sseTask;
    private readonly MonitorLoader _loader;
    private InboundAnnouncer? _inbound;
    private InboundCatchUp? _catchUp;
    private IPrintListener? _listener;
    private PrinterWatch? _printerWatch;
    private Timer? _pollTimer;
    private Timer? _printerTimer;
    private DateTime _listenerStart;
    private bool _started;
    private bool _polling;
    private bool _stuckChecking;
    private volatile bool _stopped;
    private bool _disposed;

    /// <param name="cfg">Shared with the settings dialog, which edits it in place; read at each call and connect.</param>
    /// <param name="owner">
    /// The owner thread's context (the UI thread's WindowsFormsSynchronizationContext): every callback is posted
    /// through it, and <see cref="Start"/> must run under it so await continuations come back the same way.
    /// </param>
    public ClientRuntime(ClientConfig cfg, SynchronizationContext owner)
    {
        Config = cfg;
        _owner = owner;
        Api = new FaxApi(cfg, _http) { Log = m => Info("api: " + m) };
        Tracker = new JobTracker(cfg.SpoolDir, m => Info("tracker: " + m));
        Monitor = new MonitorModel(TimeZoneInfo.Local);
        _loader = new MonitorLoader(Monitor, () => Api, OnMonitorLoaded, OnMonitorLoadFailed);
        _events = CreateEventStream();
    }

    // ---- seams (set before Start) ----

    /// <summary>
    /// Default: a <see cref="PrintPipeListener"/> on the spool folder, created at <see cref="Start"/> and disposed by
    /// <see cref="Dispose"/>. One given here is started and stopped by the runtime but disposed by whoever made it.
    /// </summary>
    public IPrintListener? Listener { get; init; }
    public PrinterSetup Printer { get; init; } = new();
    public string? AnnouncedPath { get; init; } = InboundAnnouncer.DefaultPath;
    public TimeSpan PollInterval { get; init; } = DefaultPollInterval;
    public TimeSpan PrinterCheckInterval { get; init; } = DefaultPrinterCheckInterval;
    public Func<string, IReadOnlyList<QueuedJob>?> ListPrintQueue { get; init; } = PrintQueueJobs.List;
    /// <summary>Runs one stream run; tests wrap it to watch the runs.</summary>
    public Func<EventStream, CancellationToken, Task> RunStream { get; init; } = (es, ct) => es.StartAsync(ct);
    /// <summary>The clock for the stuck-job window; tests move it.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    public Action<string> Info { get; init; } = FileLog.Info;
    public Action<string, Exception?> Error { get; init; } = FileLog.Error;

    // ---- shared parts ----

    public ClientConfig Config { get; }
    /// <summary>The owner thread's context: post through it to get back to the thread every event is raised on.</summary>
    public SynchronizationContext Owner => _owner;
    public FaxApi Api { get; }
    /// <summary><see cref="Api"/> as the monitor window sees it.</summary>
    public IMonitorApi MonitorApi => Api;
    /// <summary>Owner thread only (the send dialog records its sends here).</summary>
    public JobTracker Tracker { get; }
    /// <summary>
    /// The monitor's rows and pill/countdown texts, owner thread only. Loaded in full at the first connect (and the
    /// first after a restart), from the last load at every reconnect, from the server's <c>since</c> at a resync; fed
    /// every live job and received update in between. <see cref="MonitorChanged"/> says when to redraw.
    /// </summary>
    public MonitorModel Monitor { get; }
    /// <summary>The last monitor load's failure, until a load succeeds or a restart (for a window opened after it).</summary>
    public Exception? MonitorLoadError { get; private set; }
    /// <summary>
    /// Bumped by every <see cref="RestartStream"/> (a settings save may name another server): an arm call asked before
    /// a bump answers for the old server, so its answer is not given to <see cref="NoteArm"/>.
    /// </summary>
    public int ServerGeneration => Volatile.Read(ref _streamGeneration);

    // ---- snapshot (for a window that opens later) ----

    /// <summary>The live stream run is connected.</summary>
    public bool Connected { get; private set; }
    /// <summary>Some run connected at least once since start.</summary>
    public bool EverConnected { get; private set; }
    /// <summary>
    /// The server status, merged from every status and arm event (partial ones included). While not
    /// <see cref="Connected"/> it is the last one known; <see cref="RestartStream"/> (a settings save, maybe another
    /// server) resets it to <see cref="ServerStatus.Empty"/>, and faxd's stream opens with a full status and arm event.
    /// </summary>
    public ServerStatus Status { get; private set; } = ServerStatus.Empty;
    public ArmState Arm => Status.Arm;
    /// <summary>A print job is arriving through the pipe (the tray blocks sign-out meanwhile). Any thread.</summary>
    public bool IsReceiving => _listener?.IsReceiving ?? false;
    /// <summary>A printer install or repair is running.</summary>
    public bool Installing { get; private set; }

    // ---- events (owner thread) ----

    /// <summary>Raised whenever the state is set, also to the same value (a restart reports "not connected" again).</summary>
    public event Action<bool>? ConnectionChanged;
    /// <summary>The merged <see cref="Status"/> after a status event, an arm event that changed it, or a restart's reset.</summary>
    public event Action<ServerStatus>? StatusChanged;
    /// <summary>The arm state changed (an arm or status event that carried a different one, or a restart's reset).</summary>
    public event Action<ArmState>? ArmChanged;
    /// <summary>The stream was refused with 401; raised once until <see cref="SettingsSaved"/>.</summary>
    public event Action? Unauthorized;
    /// <summary>
    /// A job reached a terminal state: a tracked one the first time (with what happened to its local copy), or a test
    /// page sent through <see cref="TrackSubmitted"/> (null result).
    /// </summary>
    public event Action<Job, TerminalResult?>? JobFinished;
    /// <summary>A received fax not announced before.</summary>
    public event Action<Job>? FaxReceived;
    /// <summary>Received faxes the catch-up found that were never announced.</summary>
    public event Action<IReadOnlyList<Job>>? FaxesCaughtUp;
    /// <summary>A document to open in a send dialog: a new print job, one found in spool\jobs, or one the server lost.</summary>
    public event Action<SendRequest>? SendOffered;
    /// <summary>A print job started (true) or finished (false) arriving; a start already over when handled is dropped.</summary>
    public event Action<bool>? ReceivingChanged;
    /// <summary>The printer went missing or moved off the pipe since the last check.</summary>
    public event Action<PrinterStatus>? PrinterProblem;
    /// <summary>Documents printed before the listener started are stuck in the printer queue (count).</summary>
    public event Action<int>? StuckPrintJobs;
    /// <summary>Something the user should be told that needs no action from the runtime (text for a balloon).</summary>
    public event Action<string>? Notice;
    /// <summary>
    /// Every outbound job update, terminal or not: stream events, poll results and a send just accepted. Raised before
    /// the tracker sees it (so before <see cref="JobFinished"/>), after <see cref="Monitor"/> took it.
    /// </summary>
    public event Action<Job>? JobChanged;
    /// <summary>
    /// Every received-fax event from the stream, announced before or not; raised before <see cref="FaxReceived"/>,
    /// after <see cref="Monitor"/> took it (the catch-up's list is raised only through <see cref="FaxesCaughtUp"/>).
    /// </summary>
    public event Action<Job>? InboxChanged;
    /// <summary>
    /// A <c>resync</c> event (the server's replay gap was too large), with its <c>since</c>; raised after the poll, the
    /// inbound catch-up and the monitor's reload started.
    /// </summary>
    public event Action<DateTimeOffset?>? Resynced;
    /// <summary>
    /// <see cref="Monitor"/>'s rows may have changed: a live update moved one, a load landed, or a restart cleared them.
    /// </summary>
    public event Action? MonitorChanged;
    /// <summary>
    /// A monitor load failed for the current server (at most once per load request, however many of its passes fail);
    /// also kept in <see cref="MonitorLoadError"/>.
    /// </summary>
    public event Action<Exception>? MonitorLoadFailed;

    // ---- lifecycle ----

    /// <summary>
    /// Rescues the old file port's leftover, scans spool\jobs (before the listener starts, so a fresh print job is
    /// never mistaken for an unsent leftover), starts the listener, the stream, the poll and the printer check.
    /// Subscribe first: problems found here are raised from inside.
    /// </summary>
    public RuntimeStart Start()
    {
        if (_started) throw new InvalidOperationException("ClientRuntime.Start runs once");
        if (SynchronizationContext.Current != _owner)
            throw new InvalidOperationException("ClientRuntime.Start must run on its owner thread, under the SynchronizationContext it was given");
        _started = true;
        _inbound = new InboundAnnouncer(AnnouncedPath, m => Info("inbound: " + m));
        _catchUp = new InboundCatchUp(_inbound,
            (since, limit) => Api.GetJobsAsync(direction: "in", states: ["received"], limit: limit, updatedSince: since),
            news =>
            {
                Info($"inbound catch-up: {news.Count} fax(es) not announced yet");
                Raise(FaxesCaughtUp, news);
            },
            e => Info(e switch
            {
                FaxApiConfigException => $"inbound catch-up: not sent, settings unusable ({e.Message})",
                FaxApiException f => $"inbound catch-up: {f.StatusCode} {f.Message}",
                JsonException => $"inbound catch-up: unreadable answer ({e.Message})",
                _ => $"inbound catch-up failed: {e.GetType().Name}: {e.Message}",
            }),
            m => Info(m));

        // A document left in the old file port by an install before 2026-09-30 goes to jobs\ (offered as unsent) or
        // rejected\. Retried at every start: the install script only warns when it cannot move it.
        RescueOldPortLeftover();

        IReadOnlyList<SendRequest> unsent = [];
        try { unsent = Tracker.Scan(); }
        catch (Exception e) { Error(@"scanning spool\jobs failed", e); }
        foreach (var u in unsent) { _offeredSpool.Add(u.SpoolId); Info($"spool {u.SpoolId}: unsent document ({u.Kind})"); }

        _listener = Listener ?? new PrintPipeListener(Config.SpoolDir) { Log = m => Info(m) };
        // The listener raises true/false once per job, in order; the post keeps that order.
        _listener.ReceivingChanged += on => Post(() =>
        {
            if (on && !_listener.IsReceiving) return;
            Raise(ReceivingChanged, on);
        });
        _listener.NewPrintJob += (id, path) => Post(() =>
        {
            Info($"new print job {id}");
            _offeredSpool.Add(id);
            Raise(SendOffered, new SendRequest(id, path));
        });
        _listener.Error += (reason, ex) =>
        {
            Error("pipe: " + reason, ex); // Core already rate-limits the repeating ones
            Post(() => Raise(Notice, NotificationText.PrintPipeProblem(Config.PrinterName, reason)));
        };
        // A pipe that cannot be created (another LANFax has it) is reported through Error, not thrown.
        _listenerStart = UtcNow();
        try { _listener.Start(); }
        catch (Exception e)
        {
            Error("print pipe failed to start", e);
            Raise(Notice, "Cannot receive print jobs in " + Config.SpoolDir + ": " + e.Message);
        }

        RestartStream();
        _pollTimer = new Timer(_ => Post(() => _ = PollTrackedAsync()), null, PollInterval, PollInterval);

        var printer = PrinterStatusNow();
        _printerWatch = new PrinterWatch(printer);
        _printerTimer = new Timer(_ => Post(CheckNow), null, PrinterCheckInterval, PrinterCheckInterval);
        return new RuntimeStart(unsent, printer);
    }

    /// <summary>
    /// Exit: nothing is raised from here on; the timers die, the stream run is cancelled, the listener is stopped on
    /// purpose (a job still arriving gets <see cref="PrintPipeListener.StopGrace"/>, off the owner thread), and the
    /// cancelled run is awaited (at most <see cref="StreamStopWait"/>).
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        StopTimers();
        _loader.Stop();
        _sseCts?.Cancel();
        _listenerWatch.StoppedOnPurpose();
        if (_listener is { } l) await Task.Run(l.Stop);
        if (_sseTask is { } run)
        {
            await Task.WhenAny(run, Task.Delay(StreamStopWait));
            if (!run.IsCompleted) Info("event stream: still ending after the stop; left behind");
        }
    }

    /// <summary>
    /// Without a <see cref="StopAsync"/> first (the message loop ended another way: session end, a fault path), stops
    /// the same way synchronously, so the stream run ends before its HttpClient goes: timers, run cancelled, listener
    /// stopped on purpose, run waited for (at most <see cref="StreamStopWait"/>; its callbacks do not need this
    /// thread). Then disposes the listener it created (not an injected one) and the HttpClients.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_stopped)
        {
            _stopped = true;
            StopTimers();
            _loader.Stop();
            _sseCts?.Cancel();
            _listenerWatch.StoppedOnPurpose();
            try { _listener?.Stop(); }
            catch (Exception e) { Error("stopping the print pipe failed", e); }
            if (_sseTask is { } run && !run.Wait(StreamStopWait)) Info("event stream: still ending at dispose; left behind");
        }
        if (Listener == null) _listener?.Dispose();
        _http.Dispose();
        _sseHttp.Dispose();
    }

    private void StopTimers()
    {
        _pollTimer?.Dispose();
        _printerTimer?.Dispose();
    }

    // ---- event stream ----

    private CancellationToken LiveStreamToken { get { lock (_sseGate) return _sseLive; } }

    /// <summary>
    /// Handlers capture the live run's token when the event is raised (on the stream's thread), so what a cancelled
    /// run raises is dropped even if it reaches the owner thread after the next run started. Job and received events
    /// still reach the tracker and the announcer: the stream already counted them in LastEventId, so they would not be
    /// replayed. Only their way into <see cref="Monitor"/> (and <see cref="JobChanged"/>, <see cref="InboxChanged"/>) is
    /// dropped: the save may have switched servers, and the restart has just cleared the monitor for the new one.
    /// </summary>
    private EventStream CreateEventStream()
    {
        var es = new EventStream(Config, _sseHttp) { Log = m => Info(m) };
        es.Connected += () => PostLive(() =>
        {
            EverConnected = true;
            SetConnected(true);
            _ = PollTrackedAsync(); // catch up on anything that finished while disconnected
            _ = CatchUpInboundAsync(null); // and on faxes received meanwhile (or while the app was not running)
            _ = ReloadMonitorAsync(null); // full the first time (nothing loaded yet), else since the last load
        });
        es.Disconnected += e => PostLive(() =>
        {
            SetConnected(false);
            if (_unauthorized.ShouldShow(e))
            {
                Error("event stream: unauthorized", null);
                Raise(Unauthorized);
            }
        });
        es.Resync += since => PostLive(() =>
        {
            Info($"event stream: resync requested (since {since?.ToString("o") ?? "unknown"}); reloading jobs and inbox");
            _ = PollTrackedAsync();
            _ = CatchUpInboundAsync(since);
            _ = ReloadMonitorAsync(since);
            Raise(Resynced, since);
        });
        es.StatusChanged += el => PostLive(() =>
        {
            var arm = Status.Arm;
            Status = Status.Merge(el);
            Monitor.NoteStatus(Status, DateTimeOffset.UtcNow); // the receiving flag's TTL clock
            Raise(StatusChanged, Status);
            if (Status.Arm != arm) Raise(ArmChanged, Status.Arm);
        });
        es.ArmChanged += a => PostLive(() => NoteArm(a));
        es.JobChanged += j =>
        {
            var ct = LiveStreamToken; // read on the stream's thread, at raise time
            Post(() => ApplyJobUpdate(j, live: !ct.IsCancellationRequested));
        };
        es.Received += j =>
        {
            var ct = LiveStreamToken;
            Post(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    ApplyToMonitor(j);
                    Raise(InboxChanged, j);
                }
                if (_inbound?.TryAnnounce(j) == true) Raise(FaxReceived, j);
            });
        };
        return es;
    }

    /// <summary>
    /// (Re)starts the one <see cref="EventStream"/>: cancels the current run, waits for it to end (one run at a time,
    /// so LastEventId has a single writer), then runs again with a new token if a token is configured. The snapshot and
    /// <see cref="Monitor"/> are reset (a load in flight lands nowhere): the settings may name another server, and the
    /// new run's connect brings the full status and a full load again. With no run (unusable settings) one load is asked
    /// for here, so <see cref="MonitorLoadFailed"/> says why.
    /// </summary>
    public void RestartStream()
    {
        if (_stopped) return;
        var oldCts = _sseCts;
        var oldTask = _sseTask;
        oldCts?.Cancel();
        Interlocked.Increment(ref _streamGeneration);
        SetConnected(false);
        ResetStatus();
        _loader.ServerChanged();
        MonitorLoadError = null;
        Raise(MonitorChanged);
        // Nothing to authenticate with, a token that cannot be sent, or nowhere to connect: do not run.
        bool run = !string.IsNullOrEmpty(Config.Token) && ClientConfig.TokenError(Config.Token) == null && Config.EndpointError() == null;
        var cts = run ? new CancellationTokenSource() : null;
        _sseCts = cts;
        // No run means no connect, so no load: ask once anyway, so the window shows why (an unusable address or token
        // throws before anything is sent; an empty token is refused by the server).
        if (!run) _ = ReloadMonitorAsync(null);
        _sseTask = Task.Run(async () =>
        {
            if (oldTask != null) { try { await oldTask; } catch { /* logged by that run */ } }
            oldCts?.Dispose(); // only once its run has observed the cancellation
            if (cts == null || cts.IsCancellationRequested) return;
            lock (_sseGate) _sseLive = cts.Token;
            try { await RunStream(_events, cts.Token); }
            catch (Exception e) { Error("event stream ended", e); }
        });
    }

    /// <summary>The settings were saved: the unauthorized notice may show again, and the stream reconnects with them.</summary>
    public void SettingsSaved()
    {
        _unauthorized.Reset();
        RestartStream();
    }

    private void ResetStatus()
    {
        if (ReferenceEquals(Status, ServerStatus.Empty)) return;
        var arm = Status.Arm;
        Status = ServerStatus.Empty;
        Raise(StatusChanged, Status);
        if (Status.Arm != arm) Raise(ArmChanged, Status.Arm);
    }

    /// <summary>
    /// An arm state the server just gave: an arm, test-window or disarm call's answer (shown at once, before the
    /// stream's arm event confirms it), or that arm event itself. Raises <see cref="StatusChanged"/> and
    /// <see cref="ArmChanged"/> when it differs from <see cref="Arm"/>; nothing when equal or stopped. The caller drops
    /// an answer asked before a restart (<see cref="ServerGeneration"/>): it is the old server's.
    /// </summary>
    public void NoteArm(ArmState answer)
    {
        if (_stopped || answer == Status.Arm) return;
        Status = Status with { Arm = answer };
        Raise(StatusChanged, Status);
        Raise(ArmChanged, answer);
    }

    private void SetConnected(bool connected)
    {
        Connected = connected;
        Raise(ConnectionChanged, connected);
    }

    // ---- jobs ----

    /// <summary>A test page the server accepted: its terminal state is raised through <see cref="JobFinished"/>.</summary>
    public void TrackSubmitted(string serverId) => _submitted.Add(serverId);

    /// <summary>
    /// A job update from the stream, the poll or a send just accepted: raised through <see cref="JobChanged"/>, then
    /// a terminal one goes to the tracker. A direct call after <see cref="StopAsync"/> still updates the tracker (a
    /// send that finishes while exiting must be recorded); only the events are not raised. Stream events never get
    /// here after the stop: Post drops them, so a send that finishes while exiting is recorded only through a direct
    /// call or a poll answer already in flight.
    /// </summary>
    public void ApplyJobUpdate(Job j) => ApplyJobUpdate(j, live: true);

    /// <summary>
    /// As <see cref="ApplyJobUpdate(Job)"/>; an update that is not <paramref name="live"/> (from a stream run that a
    /// restart cancelled since, or a poll answer to a question asked before a restart) skips <see cref="Monitor"/> and
    /// <see cref="JobChanged"/> but still reaches the tracker.
    /// </summary>
    private void ApplyJobUpdate(Job j, bool live)
    {
        if (live)
        {
            ApplyToMonitor(j);
            Raise(JobChanged, j);
        }
        if (!j.IsTerminal) return;
        if (Tracker.OnJobUpdate(j) is { } r)
        {
            Info($"job {j.Id} {j.State}: local copy {r.Outcome}");
            Raise(JobFinished, j, r);
            return;
        }
        if (_submitted.Remove(j.Id)) Raise(JobFinished, j, null);
    }

    /// <summary>Fallback for missed events: asks the server about every job still waiting for a terminal state.</summary>
    public async Task PollTrackedAsync()
    {
        if (_polling || _stopped || string.IsNullOrEmpty(Config.Token)) return;
        var ids = Tracker.Pending.Select(t => t.ServerId!).Concat(_submitted).Distinct().ToList();
        if (ids.Count == 0) return;
        _polling = true;
        try
        {
            foreach (var id in ids)
            {
                // A restart (a settings save, maybe to another server) between question and answer makes it an old
                // server's row, kept from the grids. A restart before the question does not, run or no run.
                int gen = Volatile.Read(ref _streamGeneration);
                try { var j = await Api.GetJobAsync(id); ApplyJobUpdate(j, live: gen == Volatile.Read(ref _streamGeneration)); }
                catch (FaxApiConfigException e)
                {
                    Info($"poll: settings unusable ({e.Message}); next tick");
                    break; // nothing was sent; the same settings would fail for every job
                }
                catch (FaxApiException e) when (e.StatusCode == 404)
                {
                    OnJobUnknown(id);
                }
                catch (FaxApiException e)
                {
                    Info($"poll job {id}: {e.StatusCode} {e.Message}");
                    if (e.StatusCode is 0 or 401) break; // server unreachable or token bad: try next tick
                }
                catch (JsonException e) { Info($"poll job {id}: unreadable answer ({e.Message})"); }
            }
        }
        finally { _polling = false; }
    }

    /// <summary>
    /// Asks the server for inbound faxes updated since <paramref name="since"/> (the resync's) or since the newest one
    /// already seen, and announces those not announced yet. Covers faxes the event stream never delivered: a resync
    /// gap, a disconnection, or time the app was not running.
    /// </summary>
    public Task CatchUpInboundAsync(DateTimeOffset? since) =>
        _stopped || _catchUp == null || string.IsNullOrEmpty(Config.Token) ? Task.CompletedTask : _catchUp.RunAsync(since);

    // ---- monitor ----

    /// <summary>A live update into <see cref="Monitor"/>; <see cref="MonitorChanged"/> when a row moved. Not after the stop.</summary>
    private void ApplyToMonitor(Job j)
    {
        if (!_stopped && Monitor.Apply(j)) Raise(MonitorChanged);
    }

    /// <summary>
    /// One load request (<see cref="MonitorLoader"/> coalesces those that arrive while one runs). The loader handles
    /// the API's own failures; anything else is logged here rather than left on a task nobody awaits.
    /// </summary>
    private async Task ReloadMonitorAsync(DateTimeOffset? since)
    {
        try { await _loader.ReloadAsync(since); }
        catch (Exception e) { if (!_stopped) Error("monitor: load failed unexpectedly", e); }
    }

    private void OnMonitorLoaded()
    {
        MonitorLoadError = null;
        Raise(MonitorChanged);
    }

    private void OnMonitorLoadFailed(Exception e)
    {
        MonitorLoadError = e;
        // A refused token is the stream's Unauthorized notice too (logged there); the window shows its token line.
        if (e is FaxApiUnauthorizedException) Info("monitor: loading the job lists refused (401)");
        else Error("monitor: loading the job lists failed", e);
        Raise(MonitorLoadFailed, e);
    }

    /// <summary>The server has no such job (its database was reset): stop polling and give the document back.</summary>
    private void OnJobUnknown(string serverId)
    {
        _submitted.Remove(serverId);
        if (Tracker.MarkUnknown(serverId) is not { } t) return;
        Error($"job {serverId} (spool {t.SpoolId}) is unknown to the server; offering the document again", null);
        Raise(SendOffered, Tracker.RequestFor(t));
    }

    // ---- listener and printer ----

    /// <summary>
    /// The periodic check (every <see cref="PrinterCheckInterval"/>): a listener that stopped by itself is started
    /// again, a printer removed or moved off the pipe is reported once per change, and early on stuck print jobs are
    /// looked for.
    /// </summary>
    public void CheckNow()
    {
        if (_stopped || _listener == null || _printerWatch == null) return;
        CheckListener();
        if (Installing) return; // the install reports its own result
        if (_printerWatch.Observe(PrinterStatusNow()) is { } changed) Raise(PrinterProblem, changed);
        var sinceStart = UtcNow() - _listenerStart;
        if (sinceStart >= StuckCheckDelay && sinceStart < StuckCheckWindow && !_stuckChecking) _ = CheckStuckPrintJobsAsync(_listenerStart);
    }

    /// <summary>
    /// A listener that ended on an unexpected fault (it reported that once) is started again, and the stuck-job check
    /// window restarts with it: jobs printed meanwhile wait in the queue and may need a restart there.
    /// </summary>
    private void CheckListener()
    {
        var action = _listenerWatch.Observe(_listener!.IsRunning);
        if (action == ListenerAction.None) return;
        Error("print pipe listener was not running; starting it again", null);
        _listenerStart = UtcNow();
        try { _listener.Start(); }
        catch (Exception e) { Error("restarting the print pipe failed", e); }
        OfferRescuedDocuments(); // a job the stopped loop saved in jobs\ but never raised
        if (action == ListenerAction.RestartAndAnnounce) Raise(Notice, NotificationText.ListenerRestarted(Config.PrinterName));
    }

    /// <summary>
    /// The session-end stop (WM_ENDSESSION, on the owner thread). The app keeps running until Windows ends it, so the
    /// periodic check is told the stop was deliberate and does not start the listener again.
    /// </summary>
    public void StopListener()
    {
        _listenerWatch.StoppedOnPurpose();
        _listener?.Stop();
    }

    public PrinterStatus PrinterStatusNow() => Printer.Status(Config.PrinterName);

    /// <summary>
    /// Documents printed while no LANFax listened wait in the queue; the spooler retries them and they arrive on their
    /// own once the listener runs (seen within 30 s), but a job it gave up on (cut off mid-write, or waiting longer
    /// than its retry lasts, which is unmeasured beyond 4 minutes) stays in "Error". Jobs submitted before the start
    /// and in Error at any check from <see cref="StuckCheckDelay"/> to <see cref="StuckCheckWindow"/> after the start
    /// are raised once each. No automatic restart: a restarted job was once delivered twice (measured), so the user
    /// decides.
    /// </summary>
    private async Task CheckStuckPrintJobsAsync(DateTime listenerStartUtc)
    {
        _stuckChecking = true;
        try
        {
            if (_stopped) return;
            var jobs = await Task.Run(() => ListPrintQueue(Config.PrinterName));
            if (jobs == null) return; // no such printer: the printer checks cover it
            var stuck = PrintQueueJobs.NewStuckSince(jobs, listenerStartUtc, _stuckSeen);
            if (stuck.Count == 0) return;
            foreach (var j in stuck) Info($"print queue: job {j.Id} '{j.Document}' of {j.User}, submitted {j.SubmittedUtc:o}, is in error (status 0x{j.Status:x})");
            Raise(StuckPrintJobs, stuck.Count);
        }
        catch (Exception e) { Error("checking the print queue for stuck documents failed", e); }
        finally { _stuckChecking = false; }
    }

    /// <summary>
    /// Installs or repairs the printer (elevated script). <paramref name="report"/> gets the result first; then the old
    /// port's leftover is rescued again (the script only warns when it cannot move it) and whatever reached jobs\ is
    /// offered. The periodic check does not report the printer meanwhile.
    /// </summary>
    public async Task<InstallResult?> InstallPrinterAsync(Action<InstallResult> report)
    {
        if (Installing) return null;
        Installing = true;
        try
        {
            Info("printer install requested");
            var r = await Printer.InstallAsync(Config.SpoolDir, Config.PrinterName);
            foreach (var line in r.Output)
            {
                if (line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    Error("printer install: " + line, null);
                else Info("printer install: " + line);
            }
            if (r.Error != null) Error("printer install: " + r.Error, null);
            try { report(r); }
            catch (Exception e) { Error("printer install: reporting the result failed", e); } // the rescue still runs
            RescueOldPortLeftover();
            OfferRescuedDocuments();
            return r;
        }
        finally { Installing = false; }
    }

    /// <summary>
    /// A document left in the old file port <c>spool\print.pdf</c> (installs before 2026-09-30) is sorted into jobs\
    /// or rejected\, as the install script does, once the printer no longer prints there. A file that cannot be moved
    /// is announced and retried at the next start (and after the next install).
    /// </summary>
    private void RescueOldPortLeftover()
    {
        var path = OldPortLeftover.PathIn(Config.SpoolDir);
        try
        {
            if (!File.Exists(path)) return;
            if (SamePath(PrinterStatusNow().ActualPort, path))
            {
                Info($"{path}: the printer still prints there; repairing the printer moves it");
                return;
            }
            var r = OldPortLeftover.Rescue(Config.SpoolDir);
            if (r.Outcome == LeftoverOutcome.Offered) Info($"{path}: one complete PDF, moved to {r.MovedTo} (offered as unsent)");
            else if (r.Outcome == LeftoverOutcome.Rejected)
            {
                Error($"{path}: not one complete PDF, moved to {r.MovedTo}", null);
                Raise(Notice, NotificationText.LeftoverRejected(r.MovedTo!));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error($"{path}: moving the document left there failed", e);
            Raise(Notice, NotificationText.LeftoverStuck(path, e.Message));
        }
    }

    /// <summary>Whether a printer port names this file (a port that is not a path, such as a pipe or PORTPROMPT:, does not).</summary>
    private static bool SamePath(string? port, string path)
    {
        if (string.IsNullOrEmpty(port)) return false;
        try { return string.Equals(Path.GetFullPath(port), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// Offers every document in spool\jobs not offered yet: a leftover print.pdf an install moved there, or a job a
    /// listener that stopped by itself saved without raising.
    /// </summary>
    private void OfferRescuedDocuments()
    {
        try
        {
            foreach (var o in Tracker.NewDocuments(_offeredSpool))
            {
                Info($"spool {o.SpoolId}: document found in jobs ({o.Kind})");
                Raise(SendOffered, o);
            }
        }
        catch (Exception e) { Error("looking for rescued documents failed", e); }
    }

    // ---- threading ----

    /// <summary>Runs <paramref name="a"/> on the owner thread unless stopped by then; its failure is logged.</summary>
    private void Post(Action a)
    {
        if (_stopped) return;
        try
        {
            _owner.Post(_ =>
            {
                if (_stopped) return;
                try { a(); }
                catch (Exception e) { Error("runtime action failed", e); }
            }, null);
        }
        catch (Exception) { /* the owner thread is gone: shutting down */ }
    }

    /// <summary>A stream event: dropped if the run that raised it was cancelled by the time it runs.</summary>
    private void PostLive(Action a)
    {
        var ct = LiveStreamToken; // read on the stream's thread, at raise time
        Post(() => { if (!ct.IsCancellationRequested) a(); });
    }

    private void Raise(Action? handler)
    {
        if (_stopped || handler == null) return;
        try { handler(); } catch (Exception e) { Error("runtime subscriber failed", e); }
    }

    private void Raise<T>(Action<T>? handler, T value)
    {
        if (_stopped || handler == null) return;
        try { handler(value); } catch (Exception e) { Error("runtime subscriber failed", e); }
    }

    private void Raise<T1, T2>(Action<T1, T2>? handler, T1 a, T2 b)
    {
        if (_stopped || handler == null) return;
        try { handler(a, b); } catch (Exception e) { Error("runtime subscriber failed", e); }
    }
}
