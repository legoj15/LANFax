using System.Diagnostics;
using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(10);
    /// <summary>How long start waits for a print.pdf left from the last run to be claimed before resuming the queue.</summary>
    private static readonly TimeSpan LeftoverWait = TimeSpan.FromSeconds(2);

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
    private readonly SpoolWatcher _spool;
    /// <summary>Paused while no watcher claims print.pdf (exit, or a watcher that cannot start); resumed while one does.</summary>
    private readonly QueueLifecycle _queueLife;
    /// <summary>Finished spooler jobs against claimed files: a job with no file was overwritten at the port.</summary>
    private readonly LossCheckHost _loss;
    /// <summary>Printer port, loss check and queue state, re-read every <see cref="HealthInterval"/> off the UI thread (spooler calls can hang).</summary>
    private readonly PrinterHealth _health;
    private readonly System.Threading.Timer _healthTimer;
    private int _healthBusy;
    /// <summary>Prints again what the printer kept but nobody claimed (a crash, a kill, a replaced file); deletes the rest.</summary>
    private readonly KeptJobRecovery _recovery;
    /// <summary>Held queue until the last run's print.pdf is claimed; null when the watcher could not start.</summary>
    private readonly StartGate? _startGate;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = (int)PollInterval.TotalMilliseconds };
    /// <summary>Test-page jobs (no local PDF, so not in the tracker) awaiting their terminal balloon.</summary>
    private readonly HashSet<string> _submitted = new();
    /// <summary>Which received faxes were already announced (live or by a catch-up); persisted per user.</summary>
    private readonly InboundAnnouncer _inbound;
    /// <summary>Runs the inbound catch-up; a request while one runs is queued, not dropped.</summary>
    private readonly InboundCatchUp _catchUp;
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
    private bool _watching; // the spool watcher started
    private string? _leftoverClaim; // path of the first claim when print.pdf was left from the last run

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
            _installItem.Visible = printer.State != PrinterState.Installed || _queueLife is { Denied: true }; // assigned later in the constructor
            _installItem.Text = printer.State switch
            {
                PrinterState.Misconfigured => $"Fix \"{_cfg.PrinterName}\" printer (prints to {printer.ActualPort})...",
                PrinterState.Installed => $"Repair \"{_cfg.PrinterName}\" printer permissions...",
                _ => $"Install \"{_cfg.PrinterName}\" printer...",
            };
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

        // Before the watcher starts, so a fresh print job is never mistaken for an unsent leftover.
        IReadOnlyList<SendRequest> unsent = [];
        try { unsent = _tracker.Scan(); }
        catch (Exception e) { FileLog.Error(@"scanning spool\jobs failed", e); }
        foreach (var u in unsent) FileLog.Info($"spool {u.SpoolId}: unsent document ({u.Kind})");

        _spool = new SpoolWatcher(_cfg.SpoolDir) { Log = m => FileLog.Info("spool: " + m) };
        _spool.NewPrintJob += (id, path) => OnUi(() =>
        {
            FileLog.Info($"new print job {id}");
            EnqueueSend(new SendRequest(id, path));
        });
        _spool.Error += (reason, ex) =>
        {
            FileLog.Error("spool: " + reason, ex); // Core already rate-limits these
            OnUi(() => Balloon($"A print job could not be picked up: {reason}"));
        };
        // Its only balloon is the access-denied one, whose fix is the install script (it grants the pause right).
        _queueLife = new QueueLifecycle(new SpoolerPrintQueue(_cfg.PrinterName), _cfg.PrinterName,
            m => FileLog.Info(m), text => OnUi(() => Balloon(text, () => _ = InstallPrinterAsync())));
        _recovery = new KeptJobRecovery(new SpoolerKeptJobs(_cfg.PrinterName), new KeptJobLedger(Path.Combine(_cfg.SpoolDir, "kept-jobs.json")))
        {
            Log = m => FileLog.Info("kept jobs: " + m),
        };
        // A claim while no loss check runs is counted by the kept-job sweep against the jobs nobody followed.
        _loss = new LossCheckHost(CreateLossMonitor, TimeSpan.FromSeconds(5), m => FileLog.Info(m), _recovery.ClaimUnpaired);
        _spool.Claimed += _ => _loss.FileClaimed();
        // Held (a crash leaves it printing) while a print.pdf left from the last run is claimed, so the first resumed
        // job cannot overwrite it; the loss check starts after that claim, which belongs to no job of this session.
        _queueLife.Hold();
        bool leftover = File.Exists(_spool.PrintPath); // the last job printed while nobody watched, if any
        // Its claim (the first one) is where recovery reads the leftover's page count to name the job it came from.
        if (leftover) _spool.Claimed += p => Interlocked.CompareExchange(ref _leftoverClaim, p, null);
        try { _spool.Start(); _watching = true; }
        catch (Exception e) { FileLog.Error("spool watcher failed to start", e); Balloon("Cannot watch " + _cfg.SpoolDir + ": " + e.Message); }
        if (_watching)
        {
            // Released now, or by the health check once the leftover is claimed (the queue stays held meanwhile).
            _startGate = new StartGate(() => _spool.IsIdle, () => FinishStart(leftover), m => FileLog.Info(m),
                () => OnUi(() => Balloon(NotificationText.HeldAtStart(_cfg.PrinterName, _spool.PrintPath))));
            if (!_spool.WaitIdle(LeftoverWait) || !_startGate.TryRelease())
                FileLog.Info($"spool: print.pdf still at the port after {LeftoverWait.TotalSeconds:0} s; the queue stays held until it is claimed");
        }
        else
        {
            _loss.Restart();
            _queueLife.WatcherFailed(); // nobody claims: documents wait in the queue
        }
        Microsoft.Win32.SystemEvents.SessionEnded += OnSessionEnded;
        // A crash skips Exit: hold the queue on the way down too (best effort; the next start resumes it).
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _queueLife.Exiting();

        _events = CreateEventStream();
        StartEventStream();
        _poll.Tick += async (_, _) => await PollTrackedAsync();
        _poll.Start();
        var printerNow = PrinterStatusNow();
        _health = new PrinterHealth(PrinterStatusNow, printerNow.State, () => _startGate is { Released: true }, _queueLife, _loss,
            AnnounceMisconfigured, m => FileLog.Info(m))
        {
            // Kept-job recovery needs it; anyone with the printer's administer right can clear it.
            KeepJobs = new KeepPrintedJobsGuard(new SpoolerPrintQueue(_cfg.PrinterName), _cfg.PrinterName, m => FileLog.Info(m),
                text => OnUi(() => Balloon(text, () => _ = InstallPrinterAsync()))),
        };
        _healthTimer = new System.Threading.Timer(_ => HealthTick(), null, HealthInterval, HealthInterval);

        // Unsent documents open straight away, one dialog at a time, not behind a balloon that can be missed.
        if (unsent.Count > 0) OnUi(() => { foreach (var u in unsent) EnqueueSend(u); });
        if (_cfg.LoadError != null)
            Balloon(NotificationText.ConfigProblem(_cfg.LoadError), OpenSettings);
        if (printerNow.State == PrinterState.Missing)
            Balloon($"The \"{_cfg.PrinterName}\" printer is not installed. Click here to install it.", () => _ = InstallPrinterAsync());
        else if (printerNow.State == PrinterState.Misconfigured)
            AnnounceMisconfigured(printerNow);
        else if (string.IsNullOrEmpty(_cfg.Token) && _cfg.LoadError == null)
            Balloon("Set the server token in LANFax settings. Click here.", OpenSettings);
    }

    /// <summary>
    /// The start-up hand-over, once the last run's print.pdf is claimed (the queue is still held): the loss check
    /// starts (that claim belongs to no job of this session), kept jobs printed while nobody watched are printed again
    /// (they wait in the held queue), then the queue is resumed so held and reprinted documents drain one by one.
    /// </summary>
    private void FinishStart(bool leftover)
    {
        _loss.Restart(); // a missing printer is logged here and picked up by the health check once it exists
        int? leftoverPages = Volatile.Read(ref _leftoverClaim) is { } claimed ? PdfInfo.CountPages(claimed) : null;
        var r = _recovery.RecoverAtStart(leftover, id => _loss.Current?.IsFollowed(id) == true, leftoverPages);
        if (r.Unrecovered.Count > 0)
        {
            var docs = r.Unrecovered.Select(k => k.Document).ToList();
            FileLog.Error($"kept jobs: {docs.Count} replaced document(s) could not be printed again: {string.Join(", ", docs)}");
            OnUi(() => Balloon(NotificationText.ForUnrecovered(_cfg.PrinterName, docs)));
        }
        _queueLife.Started();
    }

    /// <summary>Every <see cref="HealthInterval"/> on a pool thread; a tick still running (a hung spooler) skips the next.</summary>
    private void HealthTick()
    {
        if (_exiting || Interlocked.Exchange(ref _healthBusy, 1) == 1) return;
        try
        {
            _startGate?.TryRelease();
            _health.Tick();
            if (_startGate is { Released: true })
            {
                var unrecovered = _recovery.Sweep(id => _loss.Current?.IsFollowed(id) == true);
                if (unrecovered.Count > 0)
                {
                    var docs = unrecovered.Select(k => k.Document).ToList();
                    FileLog.Error($"kept jobs: {docs.Count} possibly replaced document(s) could not be printed again: {string.Join(", ", docs)}");
                    OnUi(() => Balloon(NotificationText.ForUnrecovered(_cfg.PrinterName, docs, whileRunning: true)));
                }
            }
        }
        catch (Exception e) { FileLog.Error("printer check failed", e); }
        finally { Volatile.Write(ref _healthBusy, 0); }
    }

    /// <summary>
    /// A new loss check over the queue's finished jobs. A job trusted as matched to a claimed file is deleted from the
    /// queue (kept printed jobs); a replaced one, and any whose claim may have been its file, is printed again when the
    /// printer kept it. Otherwise "print it again" is shown only when that can help: a printer that prints elsewhere,
    /// or a watcher that is not running, has its own balloon. Claims no followed job took go to the kept-job sweep.
    /// </summary>
    private PrintLossMonitor CreateLossMonitor()
    {
        var jobs = new SpoolerJobSource(_cfg.PrinterName) { Log = m => FileLog.Info("spooler: " + m) };
        var monitor = new PrintLossMonitor(jobs, () => File.Exists(_spool.PrintPath))
        {
            Log = m => FileLog.Info("loss check: " + m),
            IgnoreJob = _recovery.DeletedHere, // deleting an old kept job reads as a finish to a fresh job source
        };
        monitor.Matched += id =>
        {
            _recovery.Claimed(id); // on the watcher's claim loop: the spooler call runs elsewhere
            _ = Task.Run(() => { try { _recovery.Drain(); } catch (Exception e) { FileLog.Error("kept jobs: delete failed", e); } });
        };
        monitor.ClaimUnpaired += _recovery.ClaimUnpaired;
        // A doubtful job not kept is covered by the balloon of the lost job it goes with; one kept but refused is not.
        monitor.Doubtful += id => { if (_recovery.Reprint(id) == ReprintOutcome.Refused) AnnounceLost(id); };
        monitor.Lost += id =>
        {
            if (_recovery.Reprint(id) == ReprintOutcome.PrintingAgain) return; // the watcher claims it like any job
            AnnounceLost(id);
        };
        return monitor;
    }

    private void AnnounceLost(int id) => OnUi(() =>
    {
        var printer = PrinterStatusNow().State;
        if (NotificationText.ForLostJob(_cfg.PrinterName, printer, _watching) is { } text) Balloon(text);
        else FileLog.Info($"loss check: spooler job {id} not reported as replaced (printer {printer}, watching {_watching})");
    });

    private void AnnounceMisconfigured(PrinterStatus s)
    {
        FileLog.Error($"printer \"{_cfg.PrinterName}\" prints to {s.ActualPort}, expected {PrinterSetup.ExpectedPort(_cfg.SpoolDir)}");
        OnUi(() => Balloon(NotificationText.PrinterMisconfigured(_cfg.PrinterName, s.ActualPort ?? ""), () => _ = InstallPrinterAsync()));
    }

    /// <summary>Logoff or shutdown ends the process without the tray Exit: hold the queue for the next start.</summary>
    private void OnSessionEnded(object? sender, Microsoft.Win32.SessionEndedEventArgs e) => _queueLife.Exiting();

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

    private PrinterStatus PrinterStatusNow() => _printer.Status(_cfg.PrinterName, _cfg.SpoolDir);

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
            var err = await _printer.InstallAsync(_cfg.SpoolDir, _cfg.PrinterName);
            if (err == null && _startGate is { Released: true }) // still held for a leftover: the gate resumes it
            {
                _queueLife.Resumed(); // the script leaves the queue paused for a watcher to take over
                _loss.Restart(); // the printer may be new or re-created: a fresh notification handle
            }
            if (err == null) Balloon($"The \"{_cfg.PrinterName}\" printer is installed and prints to {PrinterSetup.ExpectedPort(_cfg.SpoolDir)}.");
            else { FileLog.Error("printer install: " + err); Balloon(err); }
        }
        finally
        {
            _installing = false;
            _installItem.Enabled = true;
        }
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
        _healthTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _noticeTimer.Stop();
        _sseCts?.Cancel();
        _queueLife.Exiting(); // first, so a job already on its way to the port is still claimed; later ones wait in the queue
        // A claim already past its cancellation check is reported before the count is settled below.
        if (!_spool.StopAndWait(TimeSpan.FromSeconds(2))) FileLog.Info("exit: a spool check was still running after 2 s");
        // Kept jobs accounted for are deleted now, or the next start would print them again; the rest stay kept.
        var monitor = _loss.Current;
        try
        {
            monitor?.SettleForExit();
            _recovery.SettleAtExit(id => monitor?.IsFollowed(id) == true);
        }
        catch (Exception e) { FileLog.Error("kept jobs: settling at exit failed", e); }
        _loss.Dispose();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _poll.Dispose();
            _healthTimer.Dispose();
            _noticeTimer.Dispose();
            _showWait?.Unregister(null);
            _sseCts?.Cancel();
            _queueLife.Exiting();
            _spool.Dispose();
            _loss.Dispose();
            try { _recovery.Drain(); } catch (Exception) { /* logged by Exit when it ran; shutting down */ }
            Microsoft.Win32.SystemEvents.SessionEnded -= OnSessionEnded;
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
