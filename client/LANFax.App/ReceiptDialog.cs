using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

/// <summary>
/// The confirmation for one fax: what was sent, to whom, when it finished, and the transmission log the server kept
/// (its job event history). Opened from the sent balloon or a sent row in the monitor window; the PDF can be opened
/// and the number copied from here. Modeless; the tray keeps one per job.
/// </summary>
internal sealed class ReceiptDialog : Form
{
    private readonly Func<IMonitorApi> _api;
    private readonly PdfOpener _pdf;
    private readonly Icon _icon = IconFactory.Create(true);
    private readonly Label _heading = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12f, FontStyle.Bold) };
    private readonly Label _lines = new() { AutoSize = true, MaximumSize = new Size(460, 0) };
    private readonly ListView _events = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        MultiSelect = false, HideSelection = true,
    };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(460, 0) };
    private readonly Button _openPdf = new() { Text = "Open PDF", AutoSize = true, Enabled = false };
    private readonly Button _copyNumber = new() { Text = "Copy number", AutoSize = true, Enabled = false };
    private readonly Button _close = new() { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
    private string? _number;

    public string JobId { get; }

    public ReceiptDialog(Func<IMonitorApi> api, string jobId, string? cacheDir = null)
    {
        _api = api;
        JobId = jobId;
        _pdf = new PdfOpener(api, cacheDir ?? MonitorModel.DefaultCacheDir, ShellOpen);

        Text = "Fax confirmation";
        Icon = _icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(560, 480);
        MinimumSize = new Size(420, 320);
        _heading.Text = "Loading...";

        _events.Columns.Add("When", 110);
        _events.Columns.Add("Transmission log", 380);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(_heading);
        root.Controls.Add(_lines);
        root.Controls.Add(_events, 0, 2);
        root.Controls.Add(_error);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_copyNumber);
        buttons.Controls.Add(_openPdf);
        root.Controls.Add(buttons);
        for (int i = 0; i < 5; i++)
            root.RowStyles.Add(i == 2 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        CancelButton = _close;

        _openPdf.Click += async (_, _) => await OpenPdfAsync();
        _copyNumber.Click += (_, _) => CopyNumber();
        Shown += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var detail = await _api().GetJobDetailAsync(JobId);
            if (IsDisposed) return;
            Render(detail);
        }
        catch (Exception e) when (e is FaxApiException or JsonException)
        {
            if (!IsDisposed)
            {
                _heading.Text = "Confirmation unavailable";
                _error.Text = "Could not load the fax's details: " + e.Message;
            }
        }
    }

    private void Render(JobDetail detail)
    {
        var j = detail.Job;
        var zone = TimeZoneInfo.Local;
        string When(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone).ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
        _heading.Text = j.State switch
        {
            "sent" => $"Fax to {Party(j)} sent",
            "failed" => $"Fax to {Party(j)} failed",
            "cancelled" => $"Fax to {Party(j)} cancelled",
            _ => $"Fax to {Party(j)} ({MonitorModel.Badge(j.State).Text})",
        };
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(j.Subject)) lines.Add("Subject: " + j.Subject);
        if (j.Pages is int p) lines.Add($"{p} page{(p == 1 ? "" : "s")}");
        if (j.FinishedAt is { } done) lines.Add("Finished " + When(done));
        else lines.Add("Queued " + When(j.CreatedAt));
        if (!string.IsNullOrWhiteSpace(j.Detail)) lines.Add(j.Detail);
        if (!string.IsNullOrWhiteSpace(j.Error)) lines.Add("Error: " + j.Error);
        _lines.Text = string.Join("   ·   ", lines);
        _number = Party(j);

        _events.BeginUpdate();
        _events.Items.Clear();
        foreach (var e in detail.Events)
        {
            var what = string.Join(" — ", new[] { e.State, e.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (what.Length == 0) what = "—";
            _events.Items.Add(new ListViewItem([When(e.Ts), what]));
        }
        _events.EndUpdate();

        _openPdf.Enabled = true;
        _copyNumber.Enabled = _number is not (null or "-");
    }

    private static string Party(Job j) =>
        string.IsNullOrWhiteSpace(j.Direction == "in" ? j.FromNumber : j.ToNumber) ? "unknown number"
            : (j.Direction == "in" ? j.FromNumber : j.ToNumber)!;

    private async Task OpenPdfAsync()
    {
        _openPdf.Enabled = false;
        try
        {
            var r = await _pdf.OpenAsync(JobId);
            if (r != null && !IsDisposed && r.Message != null) _error.Text = r.Message;
        }
        finally { if (!IsDisposed) _openPdf.Enabled = true; }
    }

    private void CopyNumber()
    {
        if (_number == null) return;
        try { Clipboard.SetText(_number); _error.Text = ""; }
        catch (Exception e) { _error.Text = "Could not copy: " + e.Message; }
    }

    private static void ShellOpen(string path)
    {
        using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }
}
