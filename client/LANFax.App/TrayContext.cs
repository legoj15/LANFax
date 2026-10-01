using System.Diagnostics;
using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

/// <summary>
/// The tray: icon, menu, balloons, send dialogs (one at a time), settings, the shutdown guard and Exit. Everything
/// that is not UI lives in the <see cref="ClientRuntime"/> it is given, whose events arrive on this (the UI) thread.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);
    private const string ReceivingReason = "Receiving a document from the LAN Fax printer";

    private readonly ClientRuntime _runtime;
    private readonly ConfigLocation _loc;
    private readonly ClientConfig _cfg;
    private readonly DialogQueue<SendRequest> _queue = new();
    private readonly OfferBook _offers = new();
    private readonly NotificationQueue _notices;
    private readonly System.Windows.Forms.Timer _noticeTimer = new() { Interval = (int)NotificationQueue.Duration.TotalMilliseconds };
    private readonly NotifyIcon _tray;
    private readonly Icon _iconOn = IconFactory.Create(true);
    private readonly Icon _iconOff = IconFactory.Create(false);
    /// <summary>Asks Windows to wait for sign-out/shutdown while a print job is arriving through the pipe.</summary>
    private readonly ShutdownBlocker _shutdown;
    private readonly ToolStripMenuItem _installItem;
    private readonly ToolStripMenuItem _unsentItem;
    private readonly ToolStripMenuItem _retryItem;
    private readonly RegisteredWaitHandle? _showWait;
    private readonly EventWaitHandle _showEvent;
    /// <summary>"Arm window ends in 5 minutes", once per window; checked on arm changes and by <see cref="_armTimer"/>.</summary>
    private readonly ArmExpiryWatch _armWatch = new();
    private readonly System.Windows.Forms.Timer _armTimer = new() { Interval = 15_000 };
    private SendDialog? _openDialog;
    /// <summary>The one monitor window, made on first open; closing it only hides it.</summary>
    private MainForm? _monitor;
    private long _noticeId;
    private bool _settingsOpen;
    private bool _exiting;
    private bool _exitPending;

    /// <param name="runtime">Not started yet; created on this thread with a post onto it. Owned (disposed) by Program.</param>
    /// <param name="loc">Where the settings dialog saves the runtime's config.</param>
    /// <param name="showSignal">The per-session "show yourself" event, created with the instance mutex (owned by Program).</param>
    public TrayContext(ClientRuntime runtime, ConfigLocation loc, EventWaitHandle showSignal)
    {
        _runtime = runtime;
        _loc = loc;
        _cfg = runtime.Config;

        _notices = new NotificationQueue(ShowBalloon, stopTimer: () => _noticeTimer.Stop());
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notices.OnTimeout(_noticeId); };

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open LANFax", null, (_, _) => ShowMonitor());
        open.Font = new Font(open.Font, FontStyle.Bold); // the double-click action
        menu.Items.Add(open);
        menu.Items.Add("Open web page", null, (_, _) => OpenWeb());
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
            var printer = _runtime.PrinterStatusNow();
            _installItem.Visible = printer.State != PrinterState.Installed;
            _installItem.Text = printer.State == PrinterState.Misconfigured
                ? $"Fix \"{_cfg.PrinterName}\" printer (prints to {printer.ActualPort})..."
                : $"Install \"{_cfg.PrinterName}\" printer...";
            UpdateOfferItem(_unsentItem, OfferCategory.Unsent);
            UpdateOfferItem(_retryItem, OfferCategory.Retry);
        };

        _tray = new NotifyIcon { Icon = _iconOff, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowMonitor();
        _tray.BalloonTipClicked += (_, _) => _notices.OnClicked();
        _tray.BalloonTipClosed += (_, _) => _notices.OnClosed();
        UpdateConnectionState(false);

        _showEvent = showSignal;
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => OnUi(() => Balloon("LANFax is already running")), null, Timeout.Infinite, false);

        // Stop runs on the UI thread inside WM_ENDSESSION: nothing it raises while stopping needs the UI thread.
        _shutdown = new ShutdownBlocker(() => _runtime.IsReceiving, _runtime.StopListener, m => FileLog.Info("shutdown: " + m));
        Subscribe();
        _armTimer.Tick += (_, _) => CheckArmExpiry();
        _armTimer.Start();

        // Problems found while starting (old port leftover, pipe) are raised from inside, so subscribe first.
        var start = _runtime.Start();

        // Unsent documents open straight away, one dialog at a time, not behind a balloon that can be missed.
        if (start.Unsent.Count > 0) OnUi(() => { foreach (var u in start.Unsent) EnqueueSend(u); });
        if (_cfg.LoadError != null)
            Balloon(NotificationText.ConfigProblem(_cfg.LoadError), OpenSettings);
        if (start.Printer.State != PrinterState.Installed)
            AnnouncePrinter(start.Printer);
        else if (string.IsNullOrEmpty(_cfg.Token) && _cfg.LoadError == null)
            Balloon("Set the server token in LANFax settings. Click here.", OpenSettings);
    }

    /// <summary>The runtime raises on this thread: handlers touch the UI directly.</summary>
    private void Subscribe()
    {
        _runtime.ConnectionChanged += UpdateConnectionState;
        _runtime.Unauthorized += () => Balloon(NotificationText.Unauthorized, OpenSettings);
        _runtime.JobFinished += OnJobFinished;
        _runtime.FaxReceived += j => Balloon(NotificationText.ForReceived(j));
        _runtime.FaxesCaughtUp += news => { foreach (var text in NotificationText.ForCaughtUp(news)) Balloon(text); };
        _runtime.SendOffered += EnqueueSend;
        // In order, once per job: block sign-out while a job arrives, release once it is saved.
        _runtime.ReceivingChanged += on => { if (on) _shutdown.Block(ReceivingReason); else _shutdown.Release(); };
        _runtime.PrinterProblem += AnnouncePrinter;
        _runtime.StuckPrintJobs += n => Balloon(NotificationText.StuckPrintJobs(_cfg.PrinterName, n), OpenPrinterQueue);
        _runtime.Notice += text => Balloon(text);
        _runtime.ArmChanged += _ => CheckArmExpiry();
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

    // ---- monitor window ----

    /// <summary>Shows the monitor window, making it the first time; raises it when it is already open.</summary>
    private void ShowMonitor()
    {
        if (_exiting || _exitPending) return;
        try
        {
            if (_monitor == null || _monitor.IsDisposed)
            {
                var form = new MainForm(_runtime, openSettings: OpenSettings);
                form.FormClosed += (_, _) => { if (_monitor == form) _monitor = null; form.Dispose(); };
                _monitor = form;
            }
            if (!_monitor.Visible) _monitor.Show();
            if (_monitor.WindowState == FormWindowState.Minimized) _monitor.WindowState = FormWindowState.Normal;
            _monitor.Activate();
        }
        catch (Exception e) { FileLog.Error("opening the monitor window failed", e); }
    }

    private void CheckArmExpiry()
    {
        if (_armWatch.Check(_runtime.Arm, DateTimeOffset.UtcNow) is { } text) Balloon(text, ShowMonitor);
    }

    // ---- threading and notifications ----

    /// <summary>Queues <paramref name="a"/> on the UI thread through the runtime's owner context (the one way back to it).</summary>
    private void OnUi(Action a)
    {
        if (_exiting) return;
        try
        {
            _runtime.Owner.Post(_ =>
            {
                if (_exiting) return;
                try { a(); } catch (Exception e) { FileLog.Error("ui action failed", e); }
            }, null);
        }
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
            if (r.PdfPath == null || File.Exists(r.PdfPath)) EnqueueSend(_runtime.Tracker.Reoffer(r));
    }

    private void UpdateConnectionState(bool connected)
    {
        _tray.Icon = connected ? _iconOn : _iconOff;
        _tray.Text = connected
            ? $"LANFax: connected to {_cfg.Server}"
            : (_runtime.EverConnected ? "LANFax: disconnected, retrying" : $"LANFax: connecting to {_cfg.Server}...");
    }

    // ---- job results ----

    private void OnJobFinished(Job j, TerminalResult? tracked)
    {
        if (tracked is { Outcome: TerminalOutcome.KeptForRetry })
        {
            var req = _runtime.Tracker.RequestFor(tracked.Tracked);
            _offers.Add(OfferCategory.Retry, req); // stays in the tray menu if the balloon is missed
            Balloon(NotificationText.ForFailedWithRetry(j), () => OpenOffer(req));
        }
        else if (NotificationText.ForTerminalJob(j) is { } text) Balloon(text);
    }

    /// <summary>A balloon's click: opens the offer unless it was already opened (e.g. from the tray menu).</summary>
    private void OpenOffer(SendRequest req)
    {
        if (_offers.Take(req.SpoolId) && req.PdfPath != null && File.Exists(req.PdfPath)) EnqueueSend(_runtime.Tracker.Reoffer(req));
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
        var dlg = new SendDialog(_cfg, _runtime.Api, req, _runtime.Tracker, OpenSettings);
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
                        _runtime.Tracker.Track(plan, job);
                        _runtime.ApplyJobUpdate(job); // the jobs grid sees it; a terminal one also finishes the record
                    }
                    else
                    {
                        if (req.PdfPath != null) _runtime.Tracker.Discard(req.SpoolId); // test page instead: document not needed
                        _runtime.TrackSubmitted(job.Id);
                    }
                    FileLog.Info($"job {job.Id} queued (spool {req.SpoolId}, attempts {s.Attempts})");
                    Balloon(NotificationText.Queued(job, plan.Number));
                    break;
                case DialogOutcome.Cancelled when !_exiting:
                    if (req.PdfPath != null) _runtime.Tracker.Discard(req.SpoolId);
                    FileLog.Info($"send cancelled (spool {req.SpoolId}, attempts {s.Attempts})");
                    break;
                case DialogOutcome.Kept when !_exiting:
                    // What the dialog learnt (lock, key, server pin) is in the record, not in the request it opened with.
                    var again = _runtime.Tracker.Reoffer(req, s);
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
            var arm = await _runtime.Api.ReceiveTestAsync(20);
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
        if (_runtime.Installing) return;
        _installItem.Enabled = false;
        try
        {
            // The result's balloon comes before any about the old port's leftover the runtime then rescues.
            await _runtime.InstallPrinterAsync(r =>
            {
                if (r.Error == null) Balloon(NotificationText.PrinterInstalled(_cfg.PrinterName, r.Warnings));
                else Balloon(r.Error);
            });
        }
        finally { _installItem.Enabled = true; }
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
                _monitor?.ServerChanged(); // maybe another server: its rows load on the next connect
                _runtime.SettingsSaved();
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
        _noticeTimer.Stop();
        _armTimer.Stop();
        _monitor?.Hide();
        // A document still arriving gets PrintPipeListener.StopGrace: say so, and keep the UI thread free meanwhile.
        if (_runtime.IsReceiving) _tray.Text = "LANFax: finishing a document that is still printing...";
        await _runtime.StopAsync();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _noticeTimer.Dispose();
            _armTimer.Dispose();
            var monitor = _monitor; _monitor = null;
            if (monitor != null) { monitor.AllowClose = true; monitor.Dispose(); }
            _showWait?.Unregister(null);
            _shutdown.Dispose();
            var open = _openDialog; _openDialog = null;
            open?.Dispose();
            _tray.Dispose();
            _iconOn.Dispose(); _iconOff.Dispose();
        }
        base.Dispose(disposing);
    }
}
