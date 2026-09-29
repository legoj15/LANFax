using System.Diagnostics;
using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly ConfigLocation _loc;
    private readonly ClientConfig _cfg;
    private readonly PrinterSetup _printer = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _sseHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly FaxApi _api;
    private readonly JobTracker _tracker;
    private readonly DialogQueue<SendRequest> _queue = new();
    private readonly UnauthorizedNotice _unauthorized = new();
    private readonly NotifyIcon _tray;
    private readonly Control _ui = new();
    private readonly Icon _iconOn = IconFactory.Create(true);
    private readonly Icon _iconOff = IconFactory.Create(false);
    private readonly SpoolWatcher _spool;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = (int)PollInterval.TotalMilliseconds };
    /// <summary>Test-page jobs (no local PDF, so not in the tracker) awaiting their terminal balloon.</summary>
    private readonly HashSet<string> _submitted = new();
    private readonly Queue<(string Text, Action? OnClick)> _startupNotices = new();
    private readonly ToolStripMenuItem _installItem;
    private readonly RegisteredWaitHandle? _showWait;
    private readonly EventWaitHandle _showEvent;
    private CancellationTokenSource? _sseCts;
    private Task? _sseTask;
    private SendDialog? _openDialog;
    private bool _everConnected;
    private bool _polling;
    private bool _installing;
    private bool _settingsOpen;
    private Action? _balloonClick;
    private bool _exiting;

    public TrayContext()
    {
        _ = _ui.Handle; // force handle creation on the UI thread so BeginInvoke works from workers
        _loc = ConfigLocation.Default();
        _cfg = ClientConfig.Load(_loc.ReadPath); // never throws; problems land in LoadError
        _api = new FaxApi(_cfg, _http) { Log = m => FileLog.Info("api: " + m) };
        _tracker = new JobTracker(_cfg.SpoolDir, m => FileLog.Info("tracker: " + m));
        FileLog.Info($"config: {_loc.ReadPath} (save to {_loc.Path}, fallback={_loc.IsFallback}), server {_cfg.Server}:{_cfg.Port}, token set={!string.IsNullOrEmpty(_cfg.Token)}");
        if (_cfg.LoadError != null) FileLog.Error("config: " + _cfg.LoadError);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open LANFax", null, (_, _) => OpenWeb());
        menu.Items.Add("Send a test page...", null, (_, _) => EnqueueSend(new SendRequest(Guid.NewGuid(), null)));
        menu.Items.Add("Receive a test (arm 20 min)", null, async (_, _) => await ReceiveTestAsync());
        menu.Items.Add(new ToolStripSeparator());
        _installItem = new ToolStripMenuItem($"Install \"{_cfg.PrinterName}\" printer...", null, async (_, _) => await InstallPrinterAsync());
        menu.Items.Add(_installItem);
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        menu.Opening += (_, _) => _installItem.Visible = !_printer.IsInstalled(_cfg.PrinterName);

        _tray = new NotifyIcon { Icon = _iconOff, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenWeb();
        _tray.BalloonTipClicked += (_, _) =>
        {
            var a = _balloonClick; _balloonClick = null;
            a?.Invoke();
            ShowNextStartupNotice();
        };
        _tray.BalloonTipClosed += (_, _) => ShowNextStartupNotice();
        UpdateConnectionState(false);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => OnUi(() => Balloon("LANFax is already running")), null, Timeout.Infinite, false);

        // Before the watcher starts, so a fresh print job is never mistaken for an orphan.
        IReadOnlyList<Orphan> orphans = [];
        try { orphans = _tracker.Scan(); }
        catch (Exception e) { FileLog.Error("scanning spool\\jobs failed", e); }
        if (orphans.Count > 0) FileLog.Info($"{orphans.Count} unsent document(s) in spool\\jobs");

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

        StartEventStream();
        _poll.Tick += async (_, _) => await PollTrackedAsync();
        _poll.Start();

        // One balloon at a time: the rest follow when it closes or is clicked.
        if (_cfg.LoadError != null)
            _startupNotices.Enqueue((NotificationText.ConfigProblem(_cfg.LoadError), OpenSettings));
        if (orphans.Count > 0)
            _startupNotices.Enqueue((NotificationText.Orphans(orphans.Count), () =>
            {
                foreach (var o in orphans)
                    if (File.Exists(o.PdfPath)) EnqueueSend(new SendRequest(o.SpoolId, o.PdfPath, o.Number, o.Subject));
            }));
        if (!_printer.IsInstalled(_cfg.PrinterName))
            _startupNotices.Enqueue(($"The \"{_cfg.PrinterName}\" printer is not installed. Click here to install it.",
                () => _ = InstallPrinterAsync()));
        else if (string.IsNullOrEmpty(_cfg.Token) && _cfg.LoadError == null)
            _startupNotices.Enqueue(("Set the server token in LANFax settings. Click here.", OpenSettings));
        ShowNextStartupNotice();
    }

    // ---- threading and notifications ----

    private void OnUi(Action a)
    {
        if (_exiting || _ui.IsDisposed) return;
        try { _ui.BeginInvoke(() => { try { a(); } catch (Exception e) { FileLog.Error("ui action failed", e); } }); }
        catch (Exception) { /* shutting down */ }
    }

    private void Balloon(string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _tray.BalloonTipTitle = "LANFax";
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = ToolTipIcon.Info;
        _tray.ShowBalloonTip(6000);
    }

    private void ShowNextStartupNotice()
    {
        if (_startupNotices.Count == 0) return;
        var (text, click) = _startupNotices.Dequeue();
        Balloon(text, click);
    }

    private void UpdateConnectionState(bool connected)
    {
        _tray.Icon = connected ? _iconOn : _iconOff;
        _tray.Text = connected
            ? $"LANFax: connected to {_cfg.Server}"
            : (_everConnected ? "LANFax: disconnected, retrying" : $"LANFax: connecting to {_cfg.Server}...");
    }

    // ---- event stream and job tracking ----

    private void StartEventStream()
    {
        // Cancel the old stream; dispose its CTS only once its loop has observed the cancellation.
        var oldCts = _sseCts;
        var oldTask = _sseTask;
        oldCts?.Cancel();
        if (oldCts != null)
        {
            if (oldTask == null) oldCts.Dispose();
            else oldTask.ContinueWith(_ => oldCts.Dispose(), TaskScheduler.Default);
        }
        _sseCts = new CancellationTokenSource();
        _sseTask = null;
        var ct = _sseCts.Token;
        var es = new EventStream(_cfg, _sseHttp) { Log = m => FileLog.Info(m) };
        es.Connected += () => OnUi(() =>
        {
            if (ct.IsCancellationRequested) return;
            _everConnected = true;
            UpdateConnectionState(true);
            _ = PollTrackedAsync(); // catch up on anything that finished while disconnected
        });
        es.Disconnected += e => OnUi(() =>
        {
            if (ct.IsCancellationRequested) return;
            UpdateConnectionState(false);
            if (_unauthorized.ShouldShow(e))
            {
                FileLog.Error("event stream: unauthorized");
                Balloon(NotificationText.Unauthorized, OpenSettings);
            }
        });
        es.JobChanged += j => OnUi(() => OnJobChanged(j));
        es.Received += j => OnUi(() => Balloon(NotificationText.ForReceived(j)));
        UpdateConnectionState(false);
        if (string.IsNullOrEmpty(_cfg.Token)) return; // nothing to authenticate with yet
        _sseTask = Task.Run(async () =>
        {
            try { await es.StartAsync(ct); }
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
                var t = r.Tracked;
                Balloon(NotificationText.ForFailedWithRetry(j), () =>
                {
                    var pdf = _tracker.PdfPath(t.SpoolId);
                    if (File.Exists(pdf)) EnqueueSend(new SendRequest(t.SpoolId, pdf, t.Number, t.Subject));
                });
            }
            else if (NotificationText.ForTerminalJob(j) is { } text) Balloon(text);
            return;
        }
        if (_submitted.Remove(j.Id) && NotificationText.ForTerminalJob(j) is { } msg) Balloon(msg);
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
                catch (FaxApiException e)
                {
                    FileLog.Info($"poll job {id}: {e.StatusCode} {e.Message}");
                    if (e.StatusCode is 0 or 401) break; // server unreachable or token bad: try next tick
                }
            }
        }
        finally { _polling = false; }
    }

    // ---- send dialogs (one at a time) ----

    private void EnqueueSend(SendRequest req)
    {
        if (_exiting) return;
        var now = _queue.Enqueue(req);
        if (now != null) OpenSendDialog(now);
        else if (_openDialog != null) _openDialog.Text = _queue.Title(_openDialog.BaseTitle);
    }

    private void OpenSendDialog(SendRequest req)
    {
        var dlg = new SendDialog(_cfg, _api, req, OpenSettings);
        dlg.Text = _queue.Title(dlg.BaseTitle);
        dlg.FormClosed += (_, _) => OnSendDialogClosed(dlg);
        _openDialog = dlg;
        dlg.Show();
    }

    private void OnSendDialogClosed(SendDialog dlg)
    {
        if (_exiting) return; // app exit: leave every file where it is for the next start
        var req = dlg.Request;
        var s = dlg.Session;
        switch (dlg.Outcome)
        {
            case SendOutcome.Accepted when s.Accepted is { } job && s.AcceptedPlan is { } plan:
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
            case SendOutcome.Cancelled:
                if (req.PdfPath != null) _tracker.Discard(req.SpoolId);
                FileLog.Info($"send cancelled (spool {req.SpoolId}, attempts {s.Attempts})");
                break;
            default:
                FileLog.Info($"send dialog abandoned (spool {req.SpoolId}); the document stays for the next start");
                break;
        }
        _openDialog = null;
        dlg.Dispose();
        var next = _queue.Complete();
        if (next != null && !_exiting) OnUi(() => OpenSendDialog(next));
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

    private void ExitApp()
    {
        _exiting = true;
        _poll.Stop();
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
