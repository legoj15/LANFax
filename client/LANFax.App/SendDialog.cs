using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

/// <summary>
/// Send dialog. With a spooled PDF it is the print-to-fax dialog; with pdfPath null it is the tray's
/// "Send a test page" prompt (test-page mode forced, no checkbox).
/// </summary>
internal sealed class SendDialog : Form
{
    private readonly ClientConfig _cfg;
    private readonly FaxApi _api;
    private readonly Guid _jobId;
    private readonly string? _pdfPath;
    private readonly Action _openSettings;

    private readonly NumberBox _number = new();
    private readonly TextBox _subject = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _testPage = new() { AutoSize = true, Text = "Send the LANFax test page instead of this document" };
    private readonly Label _serverError = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(380, 0) };
    private readonly Button _send = new() { Text = "Send", AutoSize = true, Enabled = false, MinimumSize = new Size(90, 28) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, MinimumSize = new Size(90, 28) };
    private bool _busy;

    public Job? Accepted { get; private set; }
    public SendPlan? AcceptedPlan { get; private set; }

    public SendDialog(ClientConfig cfg, FaxApi api, Guid jobId, string? pdfPath, Action openSettings)
    {
        _cfg = cfg; _api = api; _jobId = jobId; _pdfPath = pdfPath; _openSettings = openSettings;

        Text = pdfPath == null ? "Send a test page" : "Send fax";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        Icon = IconFactory.Create(true);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        AcceptButton = _send;
        CancelButton = _cancel;

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));

        if (pdfPath != null)
        {
            string size = "unknown size";
            try { size = FormatSize(new FileInfo(pdfPath).Length); } catch (Exception) { }
            t.Controls.Add(new Label { Text = "Document:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 0);
            t.Controls.Add(new Label { Text = "PDF, " + size, AutoSize = true, Anchor = AnchorStyles.Left }, 1, 0);
        }
        t.Controls.Add(new Label { Text = "Number:", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 1);
        t.Controls.Add(_number, 1, 1);
        t.Controls.Add(new Label { Text = "Subject:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 2);
        t.Controls.Add(_subject, 1, 2);
        if (pdfPath != null)
        {
            t.Controls.Add(_testPage, 0, 3);
            t.SetColumnSpan(_testPage, 2);
        }
        else
        {
            _testPage.Checked = true;
        }
        t.Controls.Add(_serverError, 0, 4);
        t.SetColumnSpan(_serverError, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_send);
        t.Controls.Add(buttons, 0, 5);
        t.SetColumnSpan(buttons, 2);
        Controls.Add(t);

        _number.Changed += UpdateEnabled;
        _testPage.CheckedChanged += (_, _) => { _number.Input.Focus(); };
        _send.Click += async (_, _) => await SendAsync();
        Shown += (_, _) => { Activate(); _number.Focus(); };
        FormClosing += OnClosing;
    }

    private void UpdateEnabled() => _send.Enabled = !_busy && _number.State.Allowed;

    private async Task SendAsync()
    {
        if (_busy) return;
        SendPlan plan;
        try
        {
            plan = SendPlan.Create(_jobId, _pdfPath ?? "", _number.RawText, _subject.Text,
                _testPage.Checked || _pdfPath == null, _cfg.KeepLocalCopies);
        }
        catch (ArgumentException e) { _serverError.Text = e.Message; return; }

        _busy = true;
        _serverError.Text = "";
        _send.Text = "Sending...";
        _send.Enabled = false; _cancel.Enabled = false;
        try
        {
            Job job;
            if (plan.Route == SendRoute.TestPage)
                job = await _api.SendTestPageAsync(plan.Number, plan.Subject, plan.IdempotencyKey);
            else
            {
                await using var fs = new FileStream(plan.PdfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                job = await _api.PostJobAsync(fs, plan.Number, plan.Subject, plan.IdempotencyKey);
            }
            Accepted = job;
            AcceptedPlan = plan;
            ApplyFileAction(plan);
            _busy = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (FaxApiUnauthorizedException)
        {
            _serverError.Text = "The server rejected the token. Opening Settings...";
            _openSettings();
            _serverError.Text = "The server rejected the token. Check Settings and try again.";
        }
        catch (FaxApiException e)
        {
            _serverError.Text = e.Message;
        }
        catch (IOException e)
        {
            _serverError.Text = "Cannot read the document: " + e.Message;
        }
        finally
        {
            if (DialogResult != DialogResult.OK)
            {
                _busy = false;
                _send.Text = "Send";
                _cancel.Enabled = true;
                UpdateEnabled();
            }
        }
    }

    private void ApplyFileAction(SendPlan plan)
    {
        if (_pdfPath == null || !File.Exists(_pdfPath)) return;
        try
        {
            if (plan.AfterAccept == FileAction.MoveToSent)
            {
                var dest = plan.SentPath(_cfg.SpoolDir);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(_pdfPath, dest, overwrite: true);
            }
            else File.Delete(_pdfPath);
        }
        catch (Exception e) { FileLog.Error("post-send file handling failed", e); }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        // Cancel (or the close box) discards the spooled copy.
        if (DialogResult != DialogResult.OK && _pdfPath != null)
        {
            try { File.Delete(_pdfPath); } catch (Exception ex) { FileLog.Error("delete spool copy failed", ex); }
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} bytes";
}
