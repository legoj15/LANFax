using System.Diagnostics;
using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);
    /// <summary>No stuck-job check before this: a job the spooler is still retrying shows "Error" until it arrives.</summary>
    private static readonly TimeSpan StuckCheckDelay = TimeSpan.FromSeconds(60);
    /// <summary>For this long after the listener starts, each printer check also looks for documents stuck in Error.</summary>
    private static readonly TimeSpan StuckCheckWindow = TimeSpan.FromMinutes(10);
    /// <summary>How often the printer's port is re-checked (a registry read), so a printer removed or moved is reported.</summary>
    private static readonly TimeSpan PrinterCheckInterval = TimeSpan.FromSeconds(30);
    private const string ReceivingReason = "Receiving a document from the LAN Fax printer";

    private readonly ConfigLocation _loc;
    private readonly ClientConfig _cfg;
    private readonly PrinterSetup _printer = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _sseHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly FaxApi _api;
    private readonly JobTracker _tracker;
    private readonly DialogQueue<SendRequest> _queue = new();
    private readonly OfferBook _offers = new();
    private readonly NotificationQueue _notices;
    private readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = (int)NotificationQueue.Duration.TotalMilliseconds };
    private readonly UnauthorizedNotice _unauthorized = new();
    private readonly NotifyIcon _tray;
    private readonly Control _ui = new();
    private readonly Icon _iconOn = IconFactory.Create(true);
    private readonly Icon _iconOff = IconFactory.Create(false);
    private readonly PrintPipeListener _spool;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = (int)PollInterval.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer _printerCheck = new() { Interval = (int)PrinterCheckInterval.TotalMilliseconds };
    private readonly PrinterWatch _printerWatch;
    private readonly ListenerWatch _listenerWatch = new();
    /// <summary>Asks Windows to wait for sign-out/shutdown while a print job is arriving through the pipe.</summary>
    private readonly ShutdownBlocker _shutdown;
    /// <summary>Test-page jobs (no local PDF, so not in the tracker) awaiting their terminal balloon.</summary>
    private readonly HashSet<string> _submitted = new();
    /// <summary>Which received faxes were already announced (live or by a catch-up); persisted per user.</summary>
    private readonly InboundAnnouncer _inbound;
    /// <summary>Runs the inbound catch-up; a request while one runs is queued, not dropped.</summary>
    private readonly InboundCatchUp _catchUp;
    /// <summary>Stuck queue job ids already announced, so each gets one balloon.</summary>
    private readonly HashSet<int> _stuckSeen = new();
    /// <summary>Spool ids already offered to the user (at start, as print jobs, or after an install re-scan).</summary>
    private readonly HashSet<Guid> _offeredSpool = new();
    private DateTime _listenerStart;
    private bool _stuckChecking;
    private readonly ToolStripMenuItem _installItem;
    private readonly ToolStripMenuItem _unsentItem;
    private readonly ToolStripMenuItem _retryItem;
    private readonly RegisteredWaitHandle? _showWait;
    private readonly EventWaitHandle _showEvent;
    /// <summary>One stream for the app's lifetime (it reads the config at each connect), so LastEventId survives a restart.</summary>
    private readonly EventStream _events;
    private readonly object _sseGate = new();
    private CancellationToken _sseLive; // the token of the run currently allowed to raise events (guarded by _sseGate)
    private CancellationTokenSource? _sseCts;
    private Task? _sseTask;
    private SendDialog? _openDialog;
    private long _noticeId;
    private bool _everConnected;
    private bool _polling;
    private bool _installing;
    private bool _settingsOpen;
    private bool _exiting;
    private bool _exitPending;

    /// <param name="showSignal">The per-session "show yourself" event, created with the instance mutex (owned by Program).</param>
    public TrayContext(EventWaitHandle showSignal)
    {
        _ = _ui.Handle; // force handle creation on the UI thread so BeginInvoke works from workers
        _loc = ConfigLocation.Default();
        _cfg = ClientConfig.Load(_loc.ReadPath, ClientConfig.DefaultTokenPath); // never throws; problems land in LoadError
        _api = new FaxApi(_cfg, _http) { Log = m => FileLog.Info("api: " + m) };
        _tracker = new JobTracker(_cfg.SpoolDir, m => FileLog.Info("tracker: " + m));
        _inbound = new InboundAnnouncer(InboundAnnouncer.DefaultPath, m => FileLog.Info("inbound: " + m));
        _catchUp = new InboundCatchUp(_inbound,
            (since, limit) => _api.GetJobsAsync(direction: "in", states: ["received"], limit: limit, updatedSince: since),
            news =>
            {
                FileLog.Info($"inbound catch-up: {news.Count} fax(es) not announced yet");
                foreach (var text in NotificationText.ForCaughtUp(news)) Balloon(text);
            },
            e => FileLog.Info(e switch
            {
                FaxApiConfigException => $"inbound catch-up: not sent, settings unusable ({e.Message})",
                FaxApiException f => $"inbound catch-up: {f.StatusCode} {f.Message}",
                JsonException => $"inbound catch-up: unreadable answer ({e.Message})",
                _ => $"inbound catch-up failed: {e.GetType().Name}: {e.Message}",
            }),
            m => FileLog.Info(m));
        FileLog.Info($"config: {_loc.ReadPath} (save to {_loc.Path}, fallback={_loc.IsFallback}), server {_cfg.Server}:{_cfg.Port}, token set={!string.IsNullOrEmpty(_cfg.Token)}");
        if (_cfg.LoadError != null) FileLog.Error("config: " + _cfg.LoadError);

        _notices = new NotificationQueue(ShowBalloon, stopTimer: () => _noticeTimer.Stop());
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notices.OnTimeout(_noticeId); };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open LANFax", null, (_, _) => OpenWeb());
        _unsentItem = new ToolStripMenuItem("", null, (_, _) => OpenOffers(OfferCategory.Unsent)) { Visible = false };
        _retryItem = new ToolStripMenuItem("", null, (_, _) => OpenOffers(OfferCategory.Retry)) { Visible = false };
        menu.Items.Add(_unsentItem);
        menu.Items.Add(_retryItem);
        menu.Items.Add("Send a test page...", null, (_, _) => EnqueueSend(new SendRequest(Guid.NewGuid(), null)));
        menu.Items.Add("Receive a test (arm 20 min)", null, async (_, _) => await ReceiveTestAsync());
        menu.Items.Add(new ToolStripSeparator());
        _installItem = new ToolStripMenuItem($"Install \"{_cfg.PrinterName}\" printer...", null, async (_, _) => await InstallPrinterAsync());
        menu.Items.Add(_installItem);
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAppAsync());
        menu.Opening += (_, _) =>
        {
            var printer = PrinterStatusNow();
            _installItem.Visible = printer.State != PrinterState.Installed;
            _installItem.Text = printer.State == PrinterState.Misconfigured
                ? $"Fix \"{_cfg.PrinterName}\" printer (prints to {printer.ActualPort})..."
                : $"Install \"{_cfg.PrinterName}\" printer...";
            UpdateOfferItem(_unsentItem, OfferCategory.Unsent);
            UpdateOfferItem(_retryItem, OfferCategory.Retry);
        };

        _tray = new NotifyIcon { Icon = _iconOff, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenWeb();
        _tray.BalloonTipClicked += (_, _) => _notices.OnClicked();
        _tray.BalloonTipClosed += (_, _) => _notices.OnClosed();
        UpdateConnectionState(false);

        _showEvent = showSignal;
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => OnUi(() => Balloon("LANFax is already running")), null, Timeout.Infinite, false);

        // A document left in the old file port by an install before 2026-09-30 goes to jobs\ (offered below) or
        // rejected\. Retried at every start: the install script only warns when it cannot move it.
        RescueOldPortLeftover();

        // Before the watcher starts, so a fresh print job is never mistaken for an unsent leftover.
        IReadOnlyList<SendRequest> unsent = [];
        try { unsent = _tracker.Scan(); }
        catch (Exception e) { FileLog.Error(@"scanning spool\jobs failed", e); }
        foreach (var u in unsent) { _offeredSpool.Add(u.SpoolId); FileLog.Info($"spool {u.SpoolId}: unsent document ({u.Kind})"); }

        _spool = new PrintPipeListener(_cfg.SpoolDir) { Log = m => FileLog.Info(m) };
        // Stop runs on the UI thread inside WM_ENDSESSION: nothing it raises while stopping needs the UI thread.
        _shutdown = new ShutdownBlocker(() => _spool.IsReceiving, StopListener, m => FileLog.Info("shutdown: " + m));
        // The listener raises true/false once per job, in order; BeginInvoke keeps that order on the UI thread.
        _spool.ReceivingChanged += on => OnUi(() => { if (on && _spool.IsReceiving) _shutdown.Block(ReceivingReason); else if (!on) _shutdown.Release(); });
        _spool.NewPrintJob += (id, path) => OnUi(() =>
        {
            FileLog.Info($"new print job {id}");
            _offeredSpool.Add(id);
            EnqueueSend(new SendRequest(id, path));
        });
        _spool.Error += (reason, ex) =>
        {
            FileLog.Error("pipe: " + reason, ex); // Core already rate-limits the repeating ones
            OnUi(() => Balloon(NotificationText.PrintPipeProblem(_cfg.PrinterName, reason)));
        };
        // A pipe that cannot be created (another LANFax has it) is reported through Error, not thrown.
        _listenerStart = DateTime.UtcNow;
        try { _spool.Start(); }
        catch (Exception e) { FileLog.Error("print pipe failed to start", e); Balloon("Cannot receive print jobs in " + _cfg.SpoolDir + ": " + e.Message); }

        _events = CreateEventStream();
        StartEventStream();
        _poll.Tick += async (_, _) => await PollTrackedAsync();
        _poll.Start();

        // Unsent documents open straight away, one dialog at a time, not behind a balloon that can be missed.
        if (unsent.Count > 0) OnUi(() => { foreach (var u in unsent) EnqueueSend(u); });
        if (_cfg.LoadError != null)
            Balloon(NotificationText.ConfigProblem(_cfg.LoadError), OpenSettings);
        var printerNow = PrinterStatusNow();
        _printerWatch = new PrinterWatch(printerNow);
        if (printerNow.State != PrinterState.Installed)
            AnnouncePrinter(printerNow);
        else if (string.IsNullOrEmpty(_cfg.Token) && _cfg.LoadError == null)
            Balloon("Set the server token in LANFax settings. Click here.", OpenSettings);
        _printerCheck.Tick += (_, _) => CheckPrinter();
        _printerCheck.Start();
    }

    /// <summary>Balloon for a printer that is missing or prints somewhere else; clicking it installs or repairs.</summary>
    private void AnnouncePrinter(PrinterStatus s)
    {
        if (s.State == PrinterState.Missing)
        {
            FileLog.Error($"printer \"{_cfg.PrinterName}\" is not installed");
            Balloon(NotificationText.PrinterMissing(_cfg.PrinterName), () => _ = InstallPrinterAsync());
            return;
        }
        FileLog.Error($"printer \"{_cfg.PrinterName}\" prints to {s.ActualPort}, expected {PrinterSetup.ExpectedPort()}");
        Balloon(NotificationText.PrinterMisconfigured(_cfg.PrinterName, s.ActualPort ?? ""), () => _ = InstallPrinterAsync());
    }

    /// <summary>
    /// Periodic re-check: a print pipe listener that stopped by itself is started again, and a printer removed or
    /// moved off the pipe after start is reported once per change.
    /// </summary>
    private void CheckPrinter()
    {
        if (_exiting) return;
        CheckListener();
        if (_installing) return; // the install reports its own result
        if (_printerWatch.Observe(PrinterStatusNow()) is { } changed) AnnouncePrinter(changed);
        var sinceStart = DateTime.UtcNow - _listenerStart;
        if (sinceStart >= StuckCheckDelay && sinceStart < StuckCheckWindow && !_stuckChecking) _ = AnnounceStuckPrintJobsAsync(_listenerStart);
    }

    /// <summary>
    /// A listener that ended on an unexpected fault (it reported that once) is started again, and the stuck-job check
    /// window restarts with it: jobs printed meanwhile wait in the queue and may need a restart there.
    /// </summary>
    private void CheckListener()
    {
        var action = _listenerWatch.Observe(_spool.IsRunning);
        if (action == ListenerAction.None) return;
        FileLog.Error("print pipe listener was not running; starting it again");
        _listenerStart = DateTime.UtcNow;
        try { _spool.Start(); }
        catch (Exception e) { FileLog.Error("restarting the print pipe failed", e); }
        OfferRescuedDocuments(); // a job the stopped loop saved in jobs\ but never raised
        if (action == ListenerAction.RestartAndAnnounce) Balloon(NotificationText.ListenerRestarted(_cfg.PrinterName));
    }

    /// <summary>
    /// The session-end stop (WM_ENDSESSION). It does not set <see cref="_exiting"/>, and the tray keeps running until
    /// Windows ends it, so the periodic check is told the stop was deliberate and does not start the listener again.
    /// Runs on the UI thread, like the check.
    /// </summary>
    private void StopListener()
    {
        _listenerWatch.StoppedOnPurpose();
        _spool.Stop();
    }

    // ---- threading and notifications ----

    private void OnUi(Action a)
    {
        if (_exiting || _ui.IsDisposed) return;
        try { _ui.BeginInvoke(() => { try { a(); } catch (Exception e) { FileLog.Error("ui action failed", e); } }); }
        catch (Exception) { /* shutting down */ }
    }

    /// <summary>Queues a balloon: it shows once the ones before it were clicked, closed or timed out.</summary>
    private void Balloon(string text, Action? onClick = null)
    {
        if (_exiting) return;
        _notices.Enqueue(text, onClick);
    }

    private void ShowBalloon(NotificationQueue.Notice n)
    {
        _noticeTimer.Stop();
        _noticeId = n.Id;
        _tray.BalloonTipTitle = "LANFax";
        _tray.BalloonTipText = n.Text;
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip((int)NotificationQueue.Duration.TotalMilliseconds);
        _noticeTimer.Start();
    }

    private void UpdateOfferItem(ToolStripMenuItem item, OfferCategory category)
    {
        var n = _offers.Count(category);
        item.Visible = n > 0;
        item.Text = OfferBook.MenuText(category, n);
    }

    /// <summary>Opens every waiting offer of a menu category, each rebuilt from its record as it stands now.</summary>
    private void OpenOffers(OfferCategory category)
    {
        foreach (var r in _offers.TakeAll(category))
            if (r.PdfPath == null || File.Exists(r.PdfPath)) EnqueueSend(_tracker.Reoffer(r));
    }

    private void UpdateConnectionState(bool connected)
    {
        _tray.Icon = connected ? _iconOn : _iconOff;
        _tray.Text = connected
            ? $"LANFax: connected to {_cfg.Server}"
            : (_everConnected ? "LANFax: disconnected, retrying" : $"LANFax: connecting to {_cfg.Server}...");
    }

    // ---- event stream and job tracking ----

    private CancellationToken LiveStreamToken { get { lock (_sseGate) return _sseLive; } }

    /// <summary>
    /// Handlers capture the live run's token when the event is raised (on the stream's thread), so anything a cancelled
    /// run raises is dropped even if it reaches the UI thread after the next run started.
    /// </summary>
    private EventStream CreateEventStream()
    {
        var es = new EventStream(_cfg, _sseHttp) { Log = m => FileLog.Info(m) };
        es.Connected += () =>
        {
            var ct = LiveStreamToken;
            OnUi(() =>
            {
                if (ct.IsCancellationRequested) return;
                _everConnected = true;
                UpdateConnectionState(true);
                _ = PollTrackedAsync(); // catch up on anything that finished while disconnected
                _ = CatchUpInboundAsync(null); // and on faxes received meanwhile (or while the app was not running)
            });
        };
        es.Disconnected += e =>
        {
            var ct = LiveStreamToken;
            OnUi(() =>
            {
                if (ct.IsCancellationRequested) return;
                UpdateConnectionState(false);
                if (_unauthorized.ShouldShow(e))
                {
                    FileLog.Error("event stream: unauthorized");
                    Balloon(NotificationText.Unauthorized, OpenSettings);
                }
            });
        };
        es.Resync += since =>
        {
            var ct = LiveStreamToken;
            OnUi(() =>
            {
                if (ct.IsCancellationRequested) return;
                FileLog.Info($"event stream: resync requested (since {since?.ToString("o") ?? "unknown"}); reloading jobs and inbox");
                _ = PollTrackedAsync();
                _ = CatchUpInboundAsync(since);
            });
        };
        es.JobChanged += j => OnUi(() => OnJobChanged(j));
        es.Received += j => OnUi(() => { if (_inbound.TryAnnounce(j)) Balloon(NotificationText.ForReceived(j)); });
        return es;
    }

    /// <summary>
    /// (Re)starts the one <see cref="EventStream"/>: cancels the current run, waits for it to end (one run at a time,
    /// so LastEventId has a single writer), then runs again with a new token if a token is configured.
    /// </summary>
    private void StartEventStream()
    {
        var oldCts = _sseCts;
        var oldTask = _sseTask;
        oldCts?.Cancel();
        UpdateConnectionState(false);
        // Nothing to authenticate with, a token that cannot be sent, or nowhere to connect: do not run.
        bool run = !string.IsNullOrEmpty(_cfg.Token) && ClientConfig.TokenError(_cfg.Token) == null && _cfg.EndpointError() == null;
        var cts = run ? new CancellationTokenSource() : null;
        _sseCts = cts;
        _sseTask = Task.Run(async () =>
        {
            if (oldTask != null) { try { await oldTask; } catch { /* logged by that run */ } }
            oldCts?.Dispose(); // only once its run has observed the cancellation
            if (cts == null || cts.IsCancellationRequested) return;
            lock (_sseGate) _sseLive = cts.Token;
            try { await _events.StartAsync(cts.Token); }
            catch (Exception e) { FileLog.Error("event stream ended", e); }
        });
    }

    private void OnJobChanged(Job j)
    {
        if (!j.IsTerminal) return;
        if (_tracker.OnJobUpdate(j) is { } r)
        {
            FileLog.Info($"job {j.Id} {j.State}: local copy {r.Outcome}");
            if (r.Outcome == TerminalOutcome.KeptForRetry)
            {
                var req = _tracker.RequestFor(r.Tracked);
                _offers.Add(OfferCategory.Retry, req); // stays in the tray menu if the balloon is missed
                Balloon(NotificationText.ForFailedWithRetry(j), () => OpenOffer(req));
            }
            else if (NotificationText.ForTerminalJob(j) is { } text) Balloon(text);
            return;
        }
        if (_submitted.Remove(j.Id) && NotificationText.ForTerminalJob(j) is { } msg) Balloon(msg);
    }

    /// <summary>A balloon's click: opens the offer unless it was already opened (e.g. from the tray menu).</summary>
    private void OpenOffer(SendRequest req)
    {
        if (_offers.Take(req.SpoolId) && req.PdfPath != null && File.Exists(req.PdfPath)) EnqueueSend(_tracker.Reoffer(req));
    }

    /// <summary>Fallback for missed events: asks the server about every job still waiting for a terminal state.</summary>
    private async Task PollTrackedAsync()
    {
        if (_polling || _exiting || string.IsNullOrEmpty(_cfg.Token)) return;
        var ids = _tracker.Pending.Select(t => t.ServerId!).Concat(_submitted).Distinct().ToList();
        if (ids.Count == 0) return;
        _polling = true;
        try
        {
            foreach (var id in ids)
            {
                try { OnJobChanged(await _api.GetJobAsync(id)); }
                catch (FaxApiConfigException e)
                {
                    FileLog.Info($"poll: settings unusable ({e.Message}); next tick");
                    break; // nothing was sent; the same settings would fail for every job
                }
                catch (FaxApiException e) when (e.StatusCode == 404)
                {
                    OnJobUnknown(id);
                }
                catch (FaxApiException e)
                {
                    FileLog.Info($"poll job {id}: {e.StatusCode} {e.Message}");
                    if (e.StatusCode is 0 or 401) break; // server unreachable or token bad: try next tick
                }
                catch (JsonException e) { FileLog.Info($"poll job {id}: unreadable answer ({e.Message})"); }
            }
        }
        finally { _polling = false; }
    }

    /// <summary>
    /// Asks the server for inbound faxes updated since <paramref name="since"/> (the resync's) or since the newest one
    /// already seen, and announces those not announced yet. Covers faxes the event stream never delivered: a resync
    /// gap, a disconnection, or time the app was not running.
    /// </summary>
    private Task CatchUpInboundAsync(DateTimeOffset? since) =>
        _exiting || string.IsNullOrEmpty(_cfg.Token) ? Task.CompletedTask : _catchUp.RunAsync(since);

    /// <summary>The server has no such job (its database was reset): stop polling and give the document back to the user.</summary>
    private void OnJobUnknown(string serverId)
    {
        _submitted.Remove(serverId);
        if (_tracker.MarkUnknown(serverId) is not { } t) return;
        FileLog.Error($"job {serverId} (spool {t.SpoolId}) is unknown to the server; offering the document again");
        EnqueueSend(_tracker.RequestFor(t));
    }

    // ---- send dialogs (one at a time) ----

    private void EnqueueSend(SendRequest req)
    {
        if (_exiting || _exitPending) return;
        _offers.Remove(req.SpoolId);
        if (req.PdfPath != null && _queue.Contains(q => q.SpoolId == req.SpoolId)) return; // already open or waiting
        var now = _queue.Enqueue(req);
        if (now != null) ShowSendDialog(now);
        else if (_openDialog != null) _openDialog.Text = _queue.Title(_openDialog.BaseTitle);
    }

    private void ShowSendDialog(SendRequest req) => _queue.Show(req, OpenSendDialog, (r, e) =>
    {
        FileLog.Error($"opening the send dialog for spool {r.SpoolId} failed", e);
        if (r.PdfPath == null) return;
        _offers.Add(OfferCategory.Unsent, r); // still reachable from the tray menu
        Balloon("A document could not be opened for sending. Use \"Unsent documents\" in the LANFax menu to try again.");
    });

    private void OpenSendDialog(SendRequest req)
    {
        var dlg = new SendDialog(_cfg, _api, req, _tracker, OpenSettings);
        try
        {
            dlg.Text = _queue.Title(dlg.BaseTitle);
            dlg.FormClosed += (_, _) => OnSendDialogClosed(dlg);
            _openDialog = dlg;
            dlg.Show();
        }
        catch
        {
            _openDialog = null;
            dlg.Dispose();
            throw;
        }
    }

    private void OnSendDialogClosed(SendDialog dlg)
    {
        try
        {
            var req = dlg.Request;
            var s = dlg.Session;
            switch (dlg.Outcome)
            {
                case DialogOutcome.Accepted when s.Accepted is { } job && s.AcceptedPlan is { } plan:
                    // Tracked even while exiting: the record must say "accepted" before the app goes.
                    if (plan.Route == SendRoute.Document)
                    {
                        _tracker.Track(plan, job);
                        if (job.IsTerminal) OnJobChanged(job);
                    }
                    else
                    {
                        if (req.PdfPath != null) _tracker.Discard(req.SpoolId); // test page instead: document not needed
                        _submitted.Add(job.Id);
                    }
                    FileLog.Info($"job {job.Id} queued (spool {req.SpoolId}, attempts {s.Attempts})");
                    Balloon(NotificationText.Queued(job, plan.Number));
                    break;
                case DialogOutcome.Cancelled when !_exiting:
                    if (req.PdfPath != null) _tracker.Discard(req.SpoolId);
                    FileLog.Info($"send cancelled (spool {req.SpoolId}, attempts {s.Attempts})");
                    break;
                case DialogOutcome.Kept when !_exiting:
                    // What the dialog learnt (lock, key, server pin) is in the record, not in the request it opened with.
                    var again = _tracker.Reoffer(req, s);
                    _offers.Add(OfferCategory.Unsent, again);
                    FileLog.Info($"send postponed (spool {req.SpoolId}, offered again as {again.Kind}); kept for the next start");
                    break;
                default:
                    FileLog.Info($"send dialog closed ({dlg.Outcome}, spool {req.SpoolId}); the document stays for the next start");
                    break;
            }
        }
        catch (Exception e) { FileLog.Error("handling the closed send dialog failed", e); }
        finally
        {
            if (_openDialog == dlg) _openDialog = null;
            dlg.Dispose();
            var next = _queue.Complete();
            if (next != null && !_exiting && !_exitPending) OnUi(() => ShowSendDialog(next));
        }
    }

    // ---- actions ----

    private PrinterStatus PrinterStatusNow() => _printer.Status(_cfg.PrinterName);

    /// <summary>
    /// Documents printed while no LANFax listened wait in the queue; the spooler retries them and they arrive on their
    /// own once the listener runs (seen within 30 s), but a job it gave up on (cut off mid-write, or waiting longer
    /// than its retry lasts, which is unmeasured beyond 4 minutes) stays in "Error". Jobs submitted before the start
    /// and in Error at any printer check (every 30 s) from <see cref="StuckCheckDelay"/> to <see cref="StuckCheckWindow"/> after the start get one
    /// balloon each that opens the queue. No automatic
    /// restart: a restarted job was once delivered twice (measured), so the user decides.
    /// </summary>
    private async Task AnnounceStuckPrintJobsAsync(DateTime listenerStartUtc)
    {
        _stuckChecking = true;
        try
        {
            if (_exiting) return;
            var jobs = await Task.Run(() => PrintQueueJobs.List(_cfg.PrinterName));
            if (jobs == null) return; // no such printer: the printer balloons cover it
            var stuck = PrintQueueJobs.NewStuckSince(jobs, listenerStartUtc, _stuckSeen);
            if (stuck.Count == 0) return;
            foreach (var j in stuck) FileLog.Info($"print queue: job {j.Id} '{j.Document}' of {j.User}, submitted {j.SubmittedUtc:o}, is in error (status 0x{j.Status:x})");
            OnUi(() => Balloon(NotificationText.StuckPrintJobs(_cfg.PrinterName, stuck.Count), OpenPrinterQueue));
        }
        catch (Exception e) { FileLog.Error("checking the print queue for stuck documents failed", e); }
        finally { _stuckChecking = false; }
    }

    private void OpenPrinterQueue()
    {
        try { Process.Start(new ProcessStartInfo("rundll32.exe") { ArgumentList = { "printui.dll,PrintUIEntry", "/o", "/n", _cfg.PrinterName } }); }
        catch (Exception e) { FileLog.Error("opening the print queue failed", e); }
    }

    private void OpenWeb()
    {
        try { Process.Start(new ProcessStartInfo(_cfg.BaseUri.ToString()) { UseShellExecute = true }); }
        catch (Exception e) { FileLog.Error("open browser failed", e); }
    }

    private async Task ReceiveTestAsync()
    {
        var r = MessageBox.Show(
            "The fax line will answer incoming calls for the next 20 minutes so you can test receiving a fax. Continue?",
            "Receive a test", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (r != DialogResult.OK) return;
        try
        {
            var arm = await _api.ReceiveTestAsync(20);
            var until = arm.Until ?? DateTimeOffset.Now.AddMinutes(20);
            Balloon(NotificationText.ReceiveArmed(until));
        }
        catch (FaxApiUnauthorizedException) { OpenSettings(); }
        catch (Exception e) when (e is FaxApiException or JsonException)
        {
            FileLog.Error("receive test failed", e);
            MessageBox.Show(e.Message, "Receive a test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task InstallPrinterAsync()
    {
        if (_installing) return;
        _installing = true;
        _installItem.Enabled = false;
        try
        {
            FileLog.Info("printer install requested");
            var r = await _printer.InstallAsync(_cfg.SpoolDir, _cfg.PrinterName);
            foreach (var line in r.Output)
            {
                if (line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    FileLog.Error("printer install: " + line);
                else FileLog.Info("printer install: " + line);
            }
            if (r.Error == null) Balloon(NotificationText.PrinterInstalled(_cfg.PrinterName, r.Warnings));
            else { FileLog.Error("printer install: " + r.Error); Balloon(r.Error); }
            // The script may have moved the printer off the old file port and still failed to move its leftover
            // (only a warning there): try again here, then offer whatever reached jobs\.
            RescueOldPortLeftover();
            OfferRescuedDocuments();
        }
        finally
        {
            _installing = false;
            _installItem.Enabled = true;
        }
    }

    /// <summary>
    /// A document left in the old file port <c>spool\print.pdf</c> (installs before 2026-09-30) is sorted into jobs\
    /// or rejected\, as the install script does, once the printer no longer prints there. A file that cannot be moved
    /// is announced and retried at the next start (and after the next install).
    /// </summary>
    private void RescueOldPortLeftover()
    {
        var path = OldPortLeftover.PathIn(_cfg.SpoolDir);
        try
        {
            if (!File.Exists(path)) return;
            if (SamePath(PrinterStatusNow().ActualPort, path))
            {
                FileLog.Info($"{path}: the printer still prints there; repairing the printer moves it");
                return;
            }
            var r = OldPortLeftover.Rescue(_cfg.SpoolDir);
            if (r.Outcome == LeftoverOutcome.Offered) FileLog.Info($"{path}: one complete PDF, moved to {r.MovedTo} (offered as unsent)");
            else if (r.Outcome == LeftoverOutcome.Rejected)
            {
                FileLog.Error($"{path}: not one complete PDF, moved to {r.MovedTo}");
                Balloon(NotificationText.LeftoverRejected(r.MovedTo!));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FileLog.Error($"{path}: moving the document left there failed", e);
            Balloon(NotificationText.LeftoverStuck(path, e.Message));
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
            foreach (var o in _tracker.NewDocuments(_offeredSpool))
            {
                FileLog.Info($"spool {o.SpoolId}: document found in jobs ({o.Kind})");
                EnqueueSend(o);
            }
        }
        catch (Exception e) { FileLog.Error("looking for rescued documents failed", e); }
    }

    private void OpenSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            using var dlg = new SettingsDialog(_cfg, _loc, () => _openDialog is { Sending: true });
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                FileLog.Info($"settings saved: {_cfg.Server}:{_cfg.Port}, token set={!string.IsNullOrEmpty(_cfg.Token)}");
                _unauthorized.Reset();
                StartEventStream();
            }
        }
        finally { _settingsOpen = false; }
    }

    /// <summary>
    /// Tray Exit. A send on the wire is offered a wait (up to <see cref="ExitWait"/>) so its result is recorded;
    /// exiting anyway is safe because the record already says "sending" with its key.
    /// </summary>
    private async Task ExitAppAsync()
    {
        if (_exiting || _exitPending) return;
        if (_openDialog is { Sending: true } dlg)
        {
            var r = MessageBox.Show("A fax is being sent. Wait for it?", "LANFax",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (r == DialogResult.Yes)
            {
                _exitPending = true; // no new dialogs while waiting
                dlg.Enabled = false; // and no second attempt from this one: Dispose must not cut it off
                FileLog.Info("exit: waiting for the send in flight");
                var deadline = DateTime.UtcNow + ExitWait;
                while (dlg.Sending && DateTime.UtcNow < deadline)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left > TimeSpan.Zero) await Task.WhenAny(dlg.Idle, Task.Delay(left));
                }
                FileLog.Info(dlg.Sending ? "exit: gave up waiting after 30 s" : "exit: send finished");
            }
        }
        _exiting = true;
        _poll.Stop();
        _printerCheck.Stop();
        _noticeTimer.Stop();
        _sseCts?.Cancel();
        // A document still arriving gets PrintPipeListener.StopGrace: say so, and keep the UI thread free meanwhile.
        if (_spool.IsReceiving) _tray.Text = "LANFax: finishing a document that is still printing...";
        _listenerWatch.StoppedOnPurpose();
        await Task.Run(_spool.Stop);
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _poll.Dispose();
            _printerCheck.Dispose();
            _noticeTimer.Dispose();
            _showWait?.Unregister(null);
            _sseCts?.Cancel();
            _spool.Dispose();
            _shutdown.Dispose();
            var open = _openDialog; _openDialog = null;
            open?.Dispose();
            _tray.Dispose();
            _iconOn.Dispose(); _iconOff.Dispose();
            _ui.Dispose();
            _http.Dispose(); _sseHttp.Dispose();
        }
        base.Dispose(disposing);
    }
}
