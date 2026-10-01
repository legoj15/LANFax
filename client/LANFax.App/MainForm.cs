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
    private readonly MonitorLoader _loader;
    private readonly PdfOpener _pdf;
    private ArmState _arm;

    /// <param name="src">The runtime (its events arrive on this thread).</param>
    /// <param name="cacheDir">Where opened PDFs are downloaded; <see cref="MonitorModel.DefaultCacheDir"/> by default.</param>
    /// <param name="openFile">Opens a downloaded PDF; ShellExecute by default.</param>
    /// <param name="openSettings">The tray's Settings dialog, for a refused token.</param>
    public MainForm(IMonitorSource src, string? cacheDir = null, Action<string>? openFile = null, Action? openSettings = null)
    {
        _src = src;
        _openSettings = openSettings;
        _arm = src.Status.Arm;
        _loader = new MonitorLoader(_model, () => _src.MonitorApi, OnLoaded, OnLoadFailed);
        _pdf = new PdfOpener(() => _src.MonitorApi, cacheDir ?? MonitorModel.DefaultCacheDir, openFile ?? ShellOpen);

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
    /// <summary>The jobs grid, for the smoke test's scroll checks.</summary>
    internal DataGridView JobsGrid => _jobsGrid;
    /// <summary>Set by the tray at exit: a close then really closes instead of hiding.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    /// <summary>The settings may name another server: drop the rows; the stream's next connect loads afresh.</summary>
    public void ServerChanged()
    {
        _loader.ServerChanged();
        RenderGrids();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _ = _loader.ReloadAsync(null); // the first load is a full one
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
            _loader.Stop();
            _tick.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- runtime events (UI thread) ----

    private void OnConnectionChanged(bool connected)
    {
        RenderPill();
        if (connected && IsHandleCreated) _ = _loader.ReloadAsync(null); // whatever changed while disconnected
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
        if (IsHandleCreated) _ = _loader.ReloadAsync(since);
    }

    // ---- loading (MonitorLoader) ----

    private void OnLoaded()
    {
        if (IsDisposed) return;
        RenderGrids();
        _message.Text = Hint;
    }

    private void OnLoadFailed(Exception e)
    {
        if (e is FaxApiUnauthorizedException) { Say(NotificationText.WindowUnauthorized); return; }
        FileLog.Error("monitor: loading the job lists failed", e);
        Say("Could not load the job lists: " + e.Message);
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
            Say(NotificationText.WindowUnauthorized);
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

    /// <summary>Downloads the fax's PDF and opens it (<see cref="PdfOpener"/>).</summary>
    private async Task OpenPdfAsync(string id)
    {
        Say("Downloading the PDF...");
        var r = await _pdf.OpenAsync(id);
        if (r == null || IsDisposed) return;
        _message.Text = r.Message ?? Hint;
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

    /// <summary>Rebuilds a grid (at most <see cref="MonitorModel.Capacity"/> rows), keeping the selected fax selected and, when scrolled down, the top visible fax on top.</summary>
    private static void Fill(DataGridView g, IReadOnlyList<MonitorRow> rows, Func<MonitorRow, object[]> cells, int badgeColumn)
    {
        var selected = g.CurrentRow?.Tag as string;
        var first = g.FirstDisplayedScrollingRowIndex;
        var firstId = first >= 0 && first < g.Rows.Count ? g.Rows[first].Tag as string : null;
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
        // Scrolled down: keep the view on the same fax, not the same index, so a new one at the top does not shift
        // it. At the top (waiting for a fax) stay at the top, so the new one shows.
        int at = first;
        for (int i = 0; first > 0 && firstId != null && i < rows.Count; i++)
            if (rows[i].Id == firstId) { at = i; break; }
        if (at >= 0 && at < g.Rows.Count) g.FirstDisplayedScrollingRowIndex = at;
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
