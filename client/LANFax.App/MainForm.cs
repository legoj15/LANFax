using System.Diagnostics;
using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

/// <summary>
/// The monitor window, a thin view over <see cref="IMonitorSource"/> (the runtime) and <see cref="MonitorModel"/>:
/// status pill, arm card with countdown and buttons, jobs grid, inbox grid; double-click opens a fax's PDF. One
/// instance, owned by the tray: the user's close hides it, the app lives in the tray. UI thread only (the runtime's
/// owner thread); public only so the smoke test can build it.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly (string Text, int Minutes)[] ArmChoices = [("Arm 1 h", 60), ("Arm 4 h", 240), ("Arm 8 h", 480)];

    private const string Hint = "Double-click a fax to open its PDF.";

    private readonly IMonitorSource _src;
    private readonly MonitorModel _model = new(TimeZoneInfo.Local);
    private readonly string _cacheDir;
    private readonly Action<string> _openFile;
    private readonly Action? _openSettings;
    private readonly Icon _icon = IconFactory.Create(true);
    private readonly Label _pill = new() { AutoSize = true, Padding = new Padding(8, 3, 8, 3), Anchor = AnchorStyles.Right, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Label _armState = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 16f, FontStyle.Bold) };
    private readonly Label _armDetail = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 6) };
    private readonly Label _message = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 0) };
    private readonly List<Button> _armButtons = [];
    private readonly DataGridView _jobsGrid = MakeGrid(("Time", 22), ("To", 22), ("State", 16), ("Pages", 10), ("Error", 40));
    private readonly DataGridView _inboxGrid = MakeGrid(("Time", 30), ("From", 50), ("Pages", 20));
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly HashSet<string> _opening = [];
    private ArmState _arm;
    private bool _loading;
    private bool _reloadQueued;
    private DateTimeOffset? _queuedSince;
    /// <summary>Bumped by <see cref="ServerChanged"/>: a load that started before it is dropped (old server's rows).</summary>
    private int _generation;

    /// <param name="src">The runtime (its events arrive on this thread).</param>
    /// <param name="cacheDir">Where opened PDFs are downloaded; <see cref="MonitorModel.DefaultCacheDir"/> by default.</param>
    /// <param name="openFile">Opens a downloaded PDF; ShellExecute by default.</param>
    /// <param name="openSettings">The tray's Settings dialog, for a refused token.</param>
    public MainForm(IMonitorSource src, string? cacheDir = null, Action<string>? openFile = null, Action? openSettings = null)
    {
        _src = src;
        _cacheDir = cacheDir ?? MonitorModel.DefaultCacheDir;
        _openFile = openFile ?? ShellOpen;
        _openSettings = openSettings;
        _arm = src.Status.Arm;

        Text = "LANFax";
        Icon = _icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 680);
        MinimumSize = new Size(560, 480);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "LANFax", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14f, FontStyle.Bold) }, 0, 0);
        header.Controls.Add(_pill, 1, 0);

        var arm = new GroupBox { Text = "Receive faxes", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var armBody = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        foreach (var (text, minutes) in ArmChoices)
            buttons.Controls.Add(ArmButton(text, () => _src.MonitorApi.ArmAsync(minutes), "Arming"));
        buttons.Controls.Add(ArmButton("Test window (20 min)", () => _src.MonitorApi.ReceiveTestAsync(20), "Arming a test window"));
        buttons.Controls.Add(ArmButton("Disarm", () => _src.MonitorApi.DisarmAsync(), "Disarming"));
        armBody.Controls.Add(_armState);
        armBody.Controls.Add(_armDetail);
        armBody.Controls.Add(buttons);
        arm.Controls.Add(armBody);

        root.Controls.Add(header);
        root.Controls.Add(arm);
        root.Controls.Add(Heading("Sent and queued"));
        root.Controls.Add(_jobsGrid);
        root.Controls.Add(Heading("Inbox"));
        root.Controls.Add(_inboxGrid);
        root.Controls.Add(_message);
        for (int i = 0; i < 7; i++)
            root.RowStyles.Add(i == 3 ? new RowStyle(SizeType.Percent, 55) : i == 5 ? new RowStyle(SizeType.Percent, 45) : new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _jobsGrid.CellDoubleClick += (_, e) => OnRowDoubleClick(_jobsGrid, e.RowIndex);
        _inboxGrid.CellDoubleClick += (_, e) => OnRowDoubleClick(_inboxGrid, e.RowIndex);
        _tick.Tick += (_, _) => { RenderArm(); RenderPill(); };
        VisibleChanged += (_, _) => { _tick.Enabled = Visible; if (Visible) { RenderArm(); RenderPill(); } };

        _src.ConnectionChanged += OnConnectionChanged;
        _src.StatusChanged += OnStatusChanged;
        _src.JobChanged += OnJob;
        _src.InboxChanged += OnJob;
        _src.Resynced += OnResynced;
        _model.NoteStatus(src.Status, DateTimeOffset.UtcNow);
        _message.Text = Hint;
        RenderPill();
        RenderArm();
    }

    /// <summary>Rows in the jobs grid (the smoke test reads it).</summary>
    public int JobRowCount => _jobsGrid.Rows.Count;
    /// <summary>Rows in the inbox grid.</summary>
    public int InboxRowCount => _inboxGrid.Rows.Count;
    /// <summary>Set by the tray at exit: a close then really closes instead of hiding.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    /// <summary>The settings may name another server: drop the rows; the stream's next connect loads afresh.</summary>
    public void ServerChanged()
    {
        _generation++;
        _model.Clear();
        RenderGrids();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _ = ReloadAsync(null); // the first load is a full one
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The user's close hides the window; sign-out, shutdown and the tray's Exit close it.
        if (e.CloseReason == CloseReason.UserClosing && !AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _src.ConnectionChanged -= OnConnectionChanged;
            _src.StatusChanged -= OnStatusChanged;
            _src.JobChanged -= OnJob;
            _src.InboxChanged -= OnJob;
            _src.Resynced -= OnResynced;
            _tick.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- runtime events (UI thread) ----

    private void OnConnectionChanged(bool connected)
    {
        RenderPill();
        if (connected && IsHandleCreated) _ = ReloadAsync(null); // whatever changed while disconnected
    }

    private void OnStatusChanged(ServerStatus s)
    {
        _model.NoteStatus(s, DateTimeOffset.UtcNow);
        _arm = s.Arm;
        RenderPill();
        RenderArm();
    }

    private void OnJob(Job j)
    {
        if (_model.Apply(j)) RenderGrids();
    }

    private void OnResynced(DateTimeOffset? since)
    {
        if (IsHandleCreated) _ = ReloadAsync(since);
    }

    // ---- loading ----

    /// <summary>
    /// A full load the first time (and after <see cref="ServerChanged"/>), else <c>GET /jobs?updated_since=</c> from
    /// <see cref="MonitorModel.ReloadSince"/>; merged by id. One at a time: a request meanwhile runs once more after.
    /// </summary>
    private async Task ReloadAsync(DateTimeOffset? resyncSince)
    {
        if (_loading)
        {
            _reloadQueued = true;
            if (resyncSince is { } r && (_queuedSince is not { } q || r < q)) _queuedSince = r;
            return;
        }
        _loading = true;
        try
        {
            while (true)
            {
                var generation = _generation;
                var since = _model.ReloadSince(resyncSince);
                var started = DateTimeOffset.UtcNow;
                IEnumerable<Job> got;
                if (since == null)
                {
                    var jobs = await _src.MonitorApi.GetJobsAsync(direction: "out", limit: MonitorModel.FullLoadLimit);
                    var inbox = await _src.MonitorApi.GetInboxAsync(MonitorModel.FullLoadLimit);
                    got = jobs.Concat(inbox);
                }
                else got = await _src.MonitorApi.GetJobsAsync(limit: MonitorModel.SinceLoadLimit, updatedSince: since);
                if (IsDisposed) return;
                if (generation == _generation) _model.ApplyLoad(got, started);
                RenderGrids();
                _message.Text = Hint;
                if (!_reloadQueued) break;
                _reloadQueued = false;
                resyncSince = _queuedSince;
                _queuedSince = null;
            }
        }
        catch (FaxApiUnauthorizedException) { Say("The server refused the token. Check it in Settings."); }
        catch (Exception e) when (e is FaxApiException or JsonException)
        {
            FileLog.Error("monitor: loading the job lists failed", e);
            Say("Could not load the job lists: " + e.Message);
        }
        finally { _loading = false; }
    }

    // ---- actions ----

    private Button ArmButton(string text, Func<Task<ArmState>> call, string what)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(80, 28) };
        b.Click += async (_, _) => await ArmCallAsync(call, what);
        _armButtons.Add(b);
        return b;
    }

    /// <summary>Arm, test window or disarm: the answer shows at once; the stream's arm event confirms it.</summary>
    private async Task ArmCallAsync(Func<Task<ArmState>> call, string what)
    {
        foreach (var b in _armButtons) b.Enabled = false;
        try
        {
            _arm = await call();
            if (IsDisposed) return;
            _message.Text = Hint;
            RenderArm();
        }
        catch (FaxApiUnauthorizedException)
        {
            Say("The server refused the token. Check it in Settings.");
            _openSettings?.Invoke();
        }
        catch (Exception e) when (e is FaxApiException or JsonException)
        {
            FileLog.Error($"monitor: {what} failed", e);
            Say($"{what} failed: {e.Message}");
        }
        finally
        {
            if (!IsDisposed) foreach (var b in _armButtons) b.Enabled = true;
        }
    }

    private void OnRowDoubleClick(DataGridView grid, int row)
    {
        if (row < 0 || row >= grid.Rows.Count || grid.Rows[row].Tag is not string id) return;
        _ = OpenPdfAsync(id);
    }

    /// <summary>Downloads the fax's PDF to the cache (fresh each time: the server says no-store), then opens it.</summary>
    private async Task OpenPdfAsync(string id)
    {
        if (!_opening.Add(id)) return; // a double double-click
        try
        {
            var path = MonitorModel.PdfCachePath(_cacheDir, id);
            Say("Downloading the PDF...");
            try { await _src.MonitorApi.DownloadPdfAsync(id, path); }
            catch (IOException e) when (File.Exists(path))
            {
                // Most likely still open in a viewer that locks it: show the copy already here.
                FileLog.Info($"monitor: pdf {id} not replaced ({e.Message}); opening the cached copy");
            }
            if (IsDisposed) return;
            _message.Text = Hint;
            _openFile(path);
        }
        catch (FaxApiUnauthorizedException) { Say("The server refused the token. Check it in Settings."); }
        catch (FaxApiException e) when (e.StatusCode == 404) { Say("The server has no PDF for this fax."); }
        catch (Exception e) when (e is FaxApiException or IOException or UnauthorizedAccessException or ArgumentException
                                      or System.ComponentModel.Win32Exception)
        {
            FileLog.Error($"monitor: opening the pdf of {id} failed", e);
            Say("Could not open the PDF: " + e.Message);
        }
        finally { _opening.Remove(id); }
    }

    private static void ShellOpen(string path)
    {
        using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ---- rendering ----

    private void Say(string text)
    {
        if (!IsDisposed) _message.Text = text;
    }

    private void RenderPill()
    {
        var now = DateTimeOffset.UtcNow;
        var p = MonitorModel.PillFor(_src.Connected, _src.EverConnected, _src.Status, _model.IsReceiving(_src.Status, now));
        _pill.Text = p.Text;
        (_pill.BackColor, _pill.ForeColor) = p.Tone switch
        {
            PillTone.Ok => (Color.FromArgb(200, 240, 208), Color.FromArgb(20, 90, 40)),
            PillTone.Bad => (Color.FromArgb(255, 205, 205), Color.FromArgb(140, 20, 20)),
            _ => (Color.FromArgb(225, 225, 225), Color.FromArgb(70, 70, 70)),
        };
    }

    private void RenderArm()
    {
        _armState.Text = MonitorModel.ArmTitle(_arm);
        _armState.ForeColor = _arm.Armed ? Color.FromArgb(20, 120, 50) : SystemColors.ControlText;
        _armDetail.Text = _model.ArmDetail(_arm, DateTimeOffset.UtcNow);
    }

    private void RenderGrids()
    {
        Fill(_jobsGrid, _model.Jobs, r => [r.Time, r.Party, r.Badge, r.Pages, r.Error], badgeColumn: 2);
        Fill(_inboxGrid, _model.Inbox, r => [r.Time, r.Party, r.Pages], badgeColumn: -1);
    }

    /// <summary>Rebuilds a grid (at most <see cref="MonitorModel.Capacity"/> rows), keeping the selected fax selected.</summary>
    private static void Fill(DataGridView g, IReadOnlyList<MonitorRow> rows, Func<MonitorRow, object[]> cells, int badgeColumn)
    {
        var selected = g.CurrentRow?.Tag as string;
        var first = g.FirstDisplayedScrollingRowIndex;
        g.Rows.Clear();
        foreach (var r in rows)
        {
            var row = g.Rows[g.Rows.Add(cells(r))];
            row.Tag = r.Id;
            if (badgeColumn >= 0)
            {
                var (back, fore) = BadgeColors(r.Color);
                row.Cells[badgeColumn].Style.BackColor = back;
                row.Cells[badgeColumn].Style.ForeColor = fore;
                row.Cells[badgeColumn].Style.SelectionBackColor = back;
                row.Cells[badgeColumn].Style.SelectionForeColor = fore;
            }
        }
        g.ClearSelection();
        foreach (DataGridViewRow row in g.Rows)
            if (selected != null && (string?)row.Tag == selected) { row.Selected = true; g.CurrentCell = row.Cells[0]; break; }
        if (first >= 0 && first < g.Rows.Count) g.FirstDisplayedScrollingRowIndex = first;
    }

    private static (Color Back, Color Fore) BadgeColors(BadgeColor c) => c switch
    {
        BadgeColor.Amber => (Color.FromArgb(255, 236, 179), Color.FromArgb(102, 77, 3)),
        BadgeColor.Green => (Color.FromArgb(200, 240, 208), Color.FromArgb(20, 90, 40)),
        BadgeColor.Red => (Color.FromArgb(255, 205, 205), Color.FromArgb(140, 20, 20)),
        BadgeColor.Blue => (Color.FromArgb(205, 225, 255), Color.FromArgb(20, 60, 140)),
        _ => (Color.FromArgb(225, 225, 225), Color.FromArgb(70, 70, 70)),
    };

    private static Label Heading(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Margin = new Padding(3, 10, 3, 3) };

    private static DataGridView MakeGrid(params (string Header, int Weight)[] columns)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle,
        };
        foreach (var (header, weight) in columns)
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        return g;
    }
}
