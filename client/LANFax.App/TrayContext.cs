using System.Diagnostics;
using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

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
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = (int)PollInterval.TotalMilliseconds };
    /// <summary>Test-page jobs (no local PDF, so not in the tracker) awaiting their terminal balloon.</summary>
    private readonly HashSet<string> _submitted = new();
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

    public TrayContext()
    {
        _ = _ui.Handle; // force handle creation on the UI thread so BeginInvoke works from workers
        _loc = ConfigLocation.Default();
        _cfg = ClientConfig.Load(_loc.ReadPath); // never throws; problems land in LoadError
        _api = new FaxApi(_cfg, _http) { Log = m => FileLog.Info("api: " + m) };
        _tracker = new JobTracker(_cfg.SpoolDir, m => FileLog.Info("tracker: " + m));
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
            _installItem.Visible = !_printer.IsInstalled(_cfg.PrinterName);
            UpdateOfferItem(_unsentItem, OfferCategory.Unsent);
            UpdateOfferItem(_retryItem, OfferCategory.Retry);
        };

        _tray = new NotifyIcon { Icon = _iconOff, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenWeb();
        _tray.BalloonTipClicked += (_, _) => _notices.OnClicked();
        _tray.BalloonTipClosed += (_, _) => _notices.OnClosed();
        UpdateConnectionState(false);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
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
        try { _spool.Start(); }
        catch (Exception e) { FileLog.Error("spool watcher failed to start", e); Balloon("Cannot watch " + _cfg.SpoolDir + ": " + e.Message); }

        _events = CreateEventStream();
        StartEventStream();
        _poll.Tick += async (_, _) => await PollTrackedAsync();
        _poll.Start();

        // Unsent documents open straight away, one dialog at a time, not behind a balloon that can be missed.
        if (unsent.Count > 0) OnUi(() => { foreach (var u in unsent) EnqueueSend(u); });
        if (_cfg.LoadError != null)
            Balloon(NotificationText.ConfigProblem(_cfg.LoadError), OpenSettings);
        if (!_printer.IsInstalled(_cfg.PrinterName))
            Balloon($"The \"{_cfg.PrinterName}\" printer is not installed. Click here to install it.", () => _ = InstallPrinterAsync());
        else if (string.IsNullOrEmpty(_cfg.Token) && _cfg.LoadError == null)
            Balloon("Set the server token in LANFax settings. Click here.", OpenSettings);
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

    private void OpenOffers(OfferCategory category)
    {
        foreach (var r in _offers.TakeAll(category))
            if (r.PdfPath == null || File.Exists(r.PdfPath)) EnqueueSend(r);
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
                FileLog.Info($"event stream: resync requested (since {since?.ToString("o") ?? "unknown"}); polling tracked jobs");
                _ = PollTrackedAsync();
            });
        };
        es.JobChanged += j => OnUi(() => OnJobChanged(j));
        es.Received += j => OnUi(() => Balloon(NotificationText.ForReceived(j)));
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
        bool run = !string.IsNullOrEmpty(_cfg.Token); // nothing to authenticate with yet
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
        if (_offers.Take(req.SpoolId) && req.PdfPath != null && File.Exists(req.PdfPath)) EnqueueSend(req);
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
                    _offers.Add(OfferCategory.Unsent, req);
                    FileLog.Info($"resumed send postponed (spool {req.SpoolId}); kept for the next start");
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
            if (err == null) Balloon($"The \"{_cfg.PrinterName}\" printer is installed.");
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
            using var dlg = new SettingsDialog(_cfg, _loc);
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
                FileLog.Info("exit: waiting for the send in flight");
                var done = await Task.WhenAny(dlg.Idle, Task.Delay(ExitWait));
                FileLog.Info(done == dlg.Idle ? "exit: send finished" : "exit: gave up waiting after 30 s");
            }
        }
        _exiting = true;
        _poll.Stop();
        _noticeTimer.Stop();
        _sseCts?.Cancel();
        _spool.Stop();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _poll.Dispose();
            _noticeTimer.Dispose();
            _showWait?.Unregister(null);
            _showEvent.Dispose();
            _sseCts?.Cancel();
            _spool.Dispose();
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
