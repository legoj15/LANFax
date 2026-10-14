using System.Diagnostics;
using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

/// <summary>
/// The monitor window, a pure view over <see cref="IMonitorSource"/> (the runtime, which owns the rows, their loads and
/// the arm state): status pill, arm card with countdown and buttons, jobs grid, inbox grid; double-click opens a fax's
/// PDF. One instance, owned by the tray: the user's close hides it, the app lives in the tray. UI thread only (the
/// runtime's owner thread); public only so the smoke test can build it.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly (string Text, int Minutes)[] ArmChoices = [("Arm 1 h", 60), ("Arm 4 h", 240), ("Arm 8 h", 480)];

    private const string Hint = "Double-click a fax to open its PDF.";

    private readonly IMonitorSource _src;
    private readonly Action? _openSettings;
    private readonly UpdateService? _updates;
    private readonly Action? _sendFax;
    private readonly Action<string>? _showReceipt;
    private readonly FlowLayoutPanel _banner = new() { Dock = DockStyle.Fill, AutoSize = true, Visible = false, BackColor = Color.FromArgb(255, 244, 204), Padding = new Padding(8, 6, 8, 6), Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _bannerText = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(80, 60, 0), Margin = new Padding(0, 6, 12, 0) };
    private readonly Button _bannerInstall = new() { Text = "Download and install", AutoSize = true, MinimumSize = new Size(80, 28) };
    private readonly Button _bannerLater = new() { Text = "Later", AutoSize = true, MinimumSize = new Size(60, 28) };
    private readonly Icon _icon = IconFactory.Create(true);
    private readonly Label _pill = new() { AutoSize = true, Padding = new Padding(8, 3, 8, 3), Anchor = AnchorStyles.Right, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Label _armState = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 16f, FontStyle.Bold) };
    private readonly Label _armDetail = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 6) };
    private readonly Label _message = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 0) };
    private readonly List<Button> _armButtons = [];
    private readonly DataGridView _jobsGrid = MakeGrid(("Time", 22), ("To", 22), ("State", 16), ("Pages", 10), ("Error", 40));
    private readonly DataGridView _inboxGrid = MakeGrid(("Time", 30), ("From", 50), ("Pages", 20));
    private readonly ContextMenuStrip _jobsMenu = new();
    private readonly ToolStripMenuItem _menuCopy = new("Copy number");
    private readonly ToolStripMenuItem _menuCancel = new("Cancel this fax...");
    private readonly ToolStripMenuItem _menuReceipt = new("View confirmation");
    private readonly ToolStripMenuItem _menuPdf = new("Open PDF");
    private readonly ContextMenuStrip _inboxMenu = new();
    private readonly ToolStripMenuItem _menuInboxCopy = new("Copy number");
    private readonly ToolStripMenuItem _menuInboxPdf = new("Open PDF");
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly PdfOpener _pdf;
    /// <summary>The message line shows a load failure (cleared once a load succeeds, unless something else was said since).</summary>
    private bool _showingLoadError;

    /// <param name="src">The runtime (its events arrive on this thread).</param>
    /// <param name="cacheDir">Where opened PDFs are downloaded; <see cref="MonitorModel.DefaultCacheDir"/> by default.</param>
    /// <param name="openFile">Opens a downloaded PDF; ShellExecute by default.</param>
    /// <param name="openSettings">The tray's Settings dialog, for a refused token.</param>
    /// <param name="updates">When a newer release is known (and not dismissed) a banner offers to install it.</param>
    /// <param name="sendFax">Opens the compose-a-fax dialog (the tray's), for the Send a fax button.</param>
    /// <param name="showReceipt">Opens the confirmation window for a job id, for sent rows.</param>
    public MainForm(IMonitorSource src, string? cacheDir = null, Action<string>? openFile = null, Action? openSettings = null,
        UpdateService? updates = null, Action? sendFax = null, Action<string>? showReceipt = null)
    {
        _src = src;
        _openSettings = openSettings;
        _updates = updates;
        _sendFax = sendFax;
        _showReceipt = showReceipt;
        _pdf =new PdfOpener(() => _src.MonitorApi, cacheDir ?? MonitorModel.DefaultCacheDir, openFile ?? ShellOpen);

        Text = "LANFax";
        Icon = _icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 680);
        MinimumSize = new Size(560, 480);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        title.Controls.Add(new Label { Text = "LANFax", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14f, FontStyle.Bold) });
        if (_sendFax != null)
        {
            var send = new Button { Text = "Send a fax...", AutoSize = true, MinimumSize = new Size(0, 28), Margin = new Padding(12, 2, 0, 0) };
            send.Click += (_, _) => _sendFax();
            title.Controls.Add(send);
        }
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(_pill, 2, 0);

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

        _banner.Controls.Add(_bannerText);
        _banner.Controls.Add(_bannerInstall);
        _banner.Controls.Add(_bannerLater);

        root.Controls.Add(_banner);
        root.Controls.Add(header);
        root.Controls.Add(arm);
        root.Controls.Add(Heading("Sent and queued"));
        root.Controls.Add(_jobsGrid);
        root.Controls.Add(Heading("Inbox"));
        root.Controls.Add(_inboxGrid);
        root.Controls.Add(_message);
        for (int i = 0; i < 8; i++)
            root.RowStyles.Add(i == 4 ? new RowStyle(SizeType.Percent, 55) : i == 6 ? new RowStyle(SizeType.Percent, 45) : new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        _jobsGrid.CellDoubleClick += (_, e) => OnRowDoubleClick(_jobsGrid, e.RowIndex);
        _inboxGrid.CellDoubleClick += (_, e) => OnRowDoubleClick(_inboxGrid, e.RowIndex);

        BuildMenus();
        _jobsGrid.CellMouseDown += (_, e) => SelectOnRightClick(_jobsGrid, e);
        _inboxGrid.CellMouseDown += (_, e) => SelectOnRightClick(_inboxGrid, e);
        _menuCopy.Click += (_, _) => CopyRow(RowOf(_jobsGrid));
        _menuInboxCopy.Click += (_, _) => CopyRow(RowOf(_inboxGrid));
        _menuPdf.Click += (_, _) => OpenRowPdf(_jobsGrid);
        _menuInboxPdf.Click += (_, _) => OpenRowPdf(_inboxGrid);
        _menuReceipt.Click += (_, _) =>
        {
            if (RowOf(_jobsGrid) is { State: "sent" } r) (_showReceipt ?? (id => _ = OpenPdfAsync(id)))(r.Id);
        };
        _menuCancel.Click += async (_, _) => { if (RowOf(_jobsGrid) is { } r) await CancelFaxAsync(r); };

        _tick.Tick += (_, _) => { RenderArm(); RenderPill(); };
        VisibleChanged += (_, _) => { _tick.Enabled = Visible; if (Visible) { RenderArm(); RenderPill(); } };

        _bannerInstall.Click += async (_, _) => await InstallUpdateAsync();
        _bannerLater.Click += (_, _) => _updates?.Dismiss();
        if (_updates != null) _updates.Changed += RenderBanner;
        RenderBanner();

        _src.ConnectionChanged += OnConnectionChanged;
        _src.StatusChanged += OnStatusChanged;
        _src.MonitorChanged += OnMonitorChanged;
        _src.MonitorLoadFailed += OnLoadFailed;
        // Opened after the runtime has been running: show what it has, including a load that already failed.
        if (src.MonitorLoadError is { } failed) OnLoadFailed(failed);
        else _message.Text = Hint;
        RenderPill();
        RenderArm();
        RenderGrids();
    }

    /// <summary>Rows in the jobs grid (the smoke test reads it).</summary>
    public int JobRowCount => _jobsGrid.Rows.Count;
    /// <summary>Rows in the inbox grid.</summary>
    public int InboxRowCount => _inboxGrid.Rows.Count;
    /// <summary>The jobs grid, for the smoke test's scroll checks.</summary>
    internal DataGridView JobsGrid => _jobsGrid;
    /// <summary>The arm card's title, for the smoke test.</summary>
    internal string ArmTitleText => _armState.Text;
    /// <summary>Whether the update banner shows, for the smoke test.</summary>
    internal bool BannerVisible => _banner.Visible;
    /// <summary>The message line, for the smoke test.</summary>
    internal string MessageText => _message.Text;
    /// <summary>Set by the tray at exit: a close then really closes instead of hiding.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

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
            _src.MonitorChanged -= OnMonitorChanged;
            _src.MonitorLoadFailed -= OnLoadFailed;
            if (_updates != null) _updates.Changed -= RenderBanner;
            _tick.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- runtime events (UI thread) ----

    private void OnConnectionChanged(bool connected) => RenderPill();

    private void OnStatusChanged(ServerStatus s)
    {
        RenderPill();
        RenderArm();
    }

    private void OnMonitorChanged()
    {
        if (IsDisposed) return;
        RenderGrids();
        if (_showingLoadError && _src.MonitorLoadError == null) Say(Hint); // a load succeeded since
    }

    /// <summary>The runtime logged it; the window says it.</summary>
    private void OnLoadFailed(Exception e)
    {
        Say(e is FaxApiUnauthorizedException ? NotificationText.WindowUnauthorized : "Could not load the job lists: " + e.Message);
        _showingLoadError = true;
    }

    // ---- actions ----

    private Button ArmButton(string text, Func<Task<ArmState>> call, string what)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(80, 28) };
        b.Click += async (_, _) => await ArmCallAsync(call, what);
        _armButtons.Add(b);
        return b;
    }

    /// <summary>
    /// Arm, test window or disarm: the answer goes into the runtime's status at once (<see cref="IMonitorSource.NoteArm"/>,
    /// which redraws the card); the stream's arm event confirms it. An answer (or error) that lands after a settings
    /// save (<see cref="IMonitorSource.ServerGeneration"/> moved) is the old server's and is not noted or shown.
    /// </summary>
    private async Task ArmCallAsync(Func<Task<ArmState>> call, string what)
    {
        foreach (var b in _armButtons) b.Enabled = false;
        int gen = _src.ServerGeneration;
        try
        {
            var answer = await call();
            if (IsDisposed || gen != _src.ServerGeneration) return; // saved meanwhile: the old server's answer
            _src.NoteArm(answer);
            Say(Hint);
        }
        catch (FaxApiUnauthorizedException)
        {
            if (gen != _src.ServerGeneration) return; // the old server refused the old token: the save already answered it
            Say(NotificationText.WindowUnauthorized);
            _openSettings?.Invoke();
        }
        catch (Exception e) when (e is FaxApiException or JsonException)
        {
            FileLog.Error($"monitor: {what} failed", e);
            if (gen == _src.ServerGeneration) Say($"{what} failed: {e.Message}");
        }
        finally
        {
            if (!IsDisposed) foreach (var b in _armButtons) b.Enabled = true;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_updates == null) return;
        var problem = await _updates.InstallAsync();
        if (problem != null && !IsDisposed) Say(problem);
    }

    private void RenderBanner()
    {
        if (IsDisposed || _updates == null) return;
        var a = _updates.Available;
        _banner.Visible = a != null && !_updates.Dismissed;
        if (a == null) return;
        _bannerText.Text = _updates.Downloading ? $"Downloading LANFax {a.Version}..." : $"LANFax {a.Version} is available (you have {_updates.Current}).";
        _bannerInstall.Enabled = !_updates.Busy;
        _bannerLater.Enabled = !_updates.Busy;
    }

    private void OnRowDoubleClick(DataGridView grid, int row)
    {
        if (row < 0 || row >= grid.Rows.Count || grid.Rows[row].Tag is not string id) return;
        // A sent fax's paper trail is its confirmation; everything else opens the PDF directly.
        if (grid == _jobsGrid && RowOf(grid) is { State: "sent" } && _showReceipt != null) { _showReceipt(id); return; }
        _ = OpenPdfAsync(id);
    }

    // ---- row actions (context menus) ----

    private void BuildMenus()
    {
        _jobsMenu.Items.AddRange([_menuCopy, _menuCancel, new ToolStripSeparator(), _menuReceipt, _menuPdf]);
        _jobsGrid.ContextMenuStrip = _jobsMenu;
        _jobsMenu.Opening += (_, _) =>
        {
            var r = RowOf(_jobsGrid);
            bool active = r is { State: "queued" or "dialing" or "sending" };
            _menuCancel.Enabled = active;
            _menuReceipt.Enabled = r is { State: "sent" };
            _menuCopy.Enabled = r != null;
            _menuPdf.Enabled = r != null;
        };
        _inboxMenu.Items.AddRange([_menuInboxCopy, _menuInboxPdf]);
        _inboxGrid.ContextMenuStrip = _inboxMenu;
        _inboxMenu.Opening += (_, _) =>
        {
            var has = RowOf(_inboxGrid) != null;
            _menuInboxCopy.Enabled = has;
            _menuInboxPdf.Enabled = has;
        };
    }

    /// <summary>Right-click selects the row (WinForms does not), so the menu acts on what is under it.</summary>
    private static void SelectOnRightClick(DataGridView g, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.RowIndex >= g.Rows.Count) return;
        g.ClearSelection();
        g.Rows[e.RowIndex].Selected = true;
        g.CurrentCell = g.Rows[e.RowIndex].Cells[Math.Min(e.ColumnIndex, g.Columns.Count - 1)];
    }

    /// <summary>The monitor's row for the grid's current selection (the grid rows carry only its id).</summary>
    private MonitorRow? RowOf(DataGridView g)
    {
        if (g.CurrentRow?.Tag is not string id) return null;
        var rows = g == _jobsGrid ? _src.Monitor.Jobs : _src.Monitor.Inbox;
        return rows.FirstOrDefault(r => r.Id == id);
    }

    private void CopyRow(MonitorRow? r)
    {
        if (r == null || string.IsNullOrEmpty(r.Party) || r.Party == "-") return;
        try { Clipboard.SetText(r.Party); Say($"Copied {r.Party}."); }
        catch (Exception e) { Say("Could not copy: " + e.Message); }
    }

    private void OpenRowPdf(DataGridView g)
    {
        if (g.CurrentRow?.Tag is string id) _ = OpenPdfAsync(id);
    }

    /// <summary>Cancelling is a deliberate act: it stops a fax already going out, so it is confirmed first.</summary>
    private async Task CancelFaxAsync(MonitorRow row)
    {
        if (MessageBox.Show(this,
                $"Cancel the fax to {row.Party} ({MonitorModel.Badge(row.State).Text.ToLowerInvariant()})?\n\nA fax already transmitting stops mid-send; the recipient keeps any pages that got through.",
                "Cancel fax", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            var j = await _src.CancelJobAsync(row.Id);
            Say(j.State == "cancelled" ? $"Fax to {row.Party} cancelled." : $"Fax to {row.Party} is {MonitorModel.Badge(j.State).Text.ToLowerInvariant()}.");
        }
        catch (FaxApiUnauthorizedException)
        {
            Say(NotificationText.WindowUnauthorized);
            _openSettings?.Invoke();
        }
        catch (Exception e) when (e is FaxApiException or JsonException)
        {
            Say("Could not cancel: " + e.Message);
        }
    }

    /// <summary>Downloads the fax's PDF and opens it (<see cref="PdfOpener"/>).</summary>
    private async Task OpenPdfAsync(string id)
    {
        Say("Downloading the PDF...");
        var r = await _pdf.OpenAsync(id);
        if (r == null || IsDisposed) return;
        Say(r.Message ?? Hint);
    }

    private static void ShellOpen(string path)
    {
        using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ---- rendering ----

    private void Say(string text)
    {
        if (IsDisposed) return;
        _message.Text = text;
        _showingLoadError = false;
    }

    private void RenderPill()
    {
        var now = DateTimeOffset.UtcNow;
        var p = MonitorModel.PillFor(_src.Connected, _src.EverConnected, _src.Status, _src.Monitor.IsReceiving(_src.Status, now));
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
        var arm = _src.Status.Arm;
        _armState.Text = MonitorModel.ArmTitle(arm);
        _armState.ForeColor = arm.Armed ? Color.FromArgb(20, 120, 50) : SystemColors.ControlText;
        _armDetail.Text = _src.Monitor.ArmDetail(arm, DateTimeOffset.UtcNow);
    }

    private void RenderGrids()
    {
        Fill(_jobsGrid, _src.Monitor.Jobs, r => [r.Time, r.Party, r.Badge, r.Pages, r.Error], badgeColumn: 2);
        Fill(_inboxGrid, _src.Monitor.Inbox, r => [r.Time, r.Party, r.Pages], badgeColumn: -1);
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
