using System.Diagnostics;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly ConfigLocation _loc;
    private readonly ClientConfig _cfg;
    private readonly PrinterSetup _printer = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _sseHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly FaxApi _api;
    private readonly NotifyIcon _tray;
    private readonly Control _ui = new();
    private readonly Icon _iconOn = IconFactory.Create(true);
    private readonly Icon _iconOff = IconFactory.Create(false);
    private readonly SpoolWatcher _spool;
    private readonly HashSet<string> _submitted = new();
    private readonly ToolStripMenuItem _installItem;
    private readonly RegisteredWaitHandle? _showWait;
    private readonly EventWaitHandle _showEvent;
    private CancellationTokenSource? _sseCts;
    private bool _connected;
    private bool _everConnected;
    private Action? _balloonClick;
    private bool _exiting;

    public TrayContext()
    {
        _ = _ui.Handle; // force handle creation on the UI thread so BeginInvoke works from workers
        _loc = ConfigLocation.Default();
        _cfg = ClientConfig.Load(_loc.ReadPath);
        _api = new FaxApi(_cfg, _http);
        FileLog.Info($"config: {_loc.Path} (fallback={_loc.IsFallback}), server {_cfg.Server}:{_cfg.Port}, token set={!string.IsNullOrEmpty(_cfg.Token)}");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open LANFax", null, (_, _) => OpenWeb());
        menu.Items.Add("Send a test page...", null, (_, _) => SendTestPage());
        menu.Items.Add("Receive a test (arm 20 min)", null, async (_, _) => await ReceiveTestAsync());
        menu.Items.Add(new ToolStripSeparator());
        _installItem = new ToolStripMenuItem($"Install \"{_cfg.PrinterName}\" printer...", null, (_, _) => InstallPrinter());
        menu.Items.Add(_installItem);
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        menu.Opening += (_, _) => _installItem.Visible = !_printer.IsInstalled(_cfg.PrinterName);

        _tray = new NotifyIcon { Icon = _iconOff, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenWeb();
        _tray.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _tray.BalloonTipClosed += (_, _) => _balloonClick = null;
        UpdateConnectionState(false);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => OnUi(() => Balloon("LANFax is already running")), null, Timeout.Infinite, false);

        _spool = new SpoolWatcher(_cfg.SpoolDir);
        _spool.NewPrintJob += (id, path) => OnUi(() => ShowSendDialog(id, path));
        try { _spool.Start(); }
        catch (Exception e) { FileLog.Error("spool watcher failed to start", e); Balloon("Cannot watch " + _cfg.SpoolDir + ": " + e.Message); }

        StartEventStream();

        if (!_printer.IsInstalled(_cfg.PrinterName))
            Balloon($"The \"{_cfg.PrinterName}\" printer is not installed. Click here to install it.", InstallPrinter);
        else if (string.IsNullOrEmpty(_cfg.Token))
            Balloon("Set the server token in LANFax settings. Click here.", OpenSettings);
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

    private void UpdateConnectionState(bool connected)
    {
        _connected = connected;
        _tray.Icon = connected ? _iconOn : _iconOff;
        _tray.Text = connected
            ? $"LANFax: connected to {_cfg.Server}"
            : (_everConnected ? "LANFax: disconnected, retrying" : $"LANFax: connecting to {_cfg.Server}...");
    }

    // ---- event stream ----

    private void StartEventStream()
    {
        _sseCts?.Cancel();
        _sseCts = new CancellationTokenSource();
        var ct = _sseCts.Token;
        var es = new EventStream(_cfg, _sseHttp);
        es.Connected += () => OnUi(() => { _everConnected = true; UpdateConnectionState(true); });
        es.Disconnected += _ => OnUi(() => { if (!ct.IsCancellationRequested) UpdateConnectionState(false); });
        es.JobChanged += j => OnUi(() => OnJobChanged(j));
        es.Received += j => OnUi(() => Balloon(NotificationText.ForReceived(j)));
        UpdateConnectionState(false);
        if (string.IsNullOrEmpty(_cfg.Token)) return; // nothing to authenticate with yet
        _ = Task.Run(async () =>
        {
            try { await es.StartAsync(ct); }
            catch (Exception e) { FileLog.Error("event stream ended", e); }
        });
    }

    private void OnJobChanged(Job j)
    {
        if (!j.IsTerminal || !_submitted.Contains(j.Id)) return;
        var text = NotificationText.ForTerminalJob(j);
        if (text == null) return;
        _submitted.Remove(j.Id);
        Balloon(text);
    }

    // ---- actions ----

    private void OpenWeb()
    {
        try { Process.Start(new ProcessStartInfo(_cfg.BaseUri.ToString()) { UseShellExecute = true }); }
        catch (Exception e) { FileLog.Error("open browser failed", e); }
    }

    private void ShowSendDialog(Guid id, string pdfPath)
    {
        FileLog.Info($"new print job {id}");
        using var dlg = new SendDialog(_cfg, _api, id, pdfPath, OpenSettings);
        if (dlg.ShowDialog() == DialogResult.OK && dlg.Accepted is { } job && dlg.AcceptedPlan is { } plan)
        {
            _submitted.Add(job.Id);
            Balloon(NotificationText.Queued(plan.Number));
        }
    }

    private void SendTestPage()
    {
        using var dlg = new SendDialog(_cfg, _api, Guid.NewGuid(), null, OpenSettings);
        if (dlg.ShowDialog() == DialogResult.OK && dlg.Accepted is { } job && dlg.AcceptedPlan is { } plan)
        {
            _submitted.Add(job.Id);
            Balloon(NotificationText.Queued(plan.Number));
        }
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
        catch (FaxApiException e) { MessageBox.Show(e.Message, "Receive a test", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void InstallPrinter()
    {
        FileLog.Info("printer install requested");
        var err = _printer.Install(_cfg.SpoolDir, _cfg.PrinterName);
        if (err == null) Balloon($"The \"{_cfg.PrinterName}\" printer is installed.");
        else { FileLog.Error("printer install: " + err); Balloon(err); }
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_cfg, _loc);
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            FileLog.Info($"settings saved: {_cfg.Server}:{_cfg.Port}");
            StartEventStream();
        }
    }

    private void ExitApp()
    {
        _exiting = true;
        _sseCts?.Cancel();
        _spool.Stop();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showWait?.Unregister(null);
            _showEvent.Dispose();
            _sseCts?.Cancel();
            _spool.Dispose();
            _tray.Dispose();
            _iconOn.Dispose(); _iconOff.Dispose();
            _ui.Dispose();
            _http.Dispose(); _sseHttp.Dispose();
        }
        base.Dispose(disposing);
    }
}
