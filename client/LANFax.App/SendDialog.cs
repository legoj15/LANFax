using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

internal enum SendOutcome
{
    /// <summary>The server accepted the job (see <see cref="SendSession.Accepted"/>).</summary>
    Accepted,
    /// <summary>The user pressed Cancel or closed the window: discard the PDF.</summary>
    Cancelled,
    /// <summary>Closed by app exit or Windows shutdown: keep the PDF (offered again at next start).</summary>
    Abandoned,
}

/// <summary>
/// Non-modal send dialog. With a spooled PDF it is the print-to-fax dialog; with no PDF it is the tray's
/// "Send a test page" prompt (test-page mode forced, no checkbox). The dialog never touches the PDF:
/// <see cref="TrayContext"/> tracks, discards or keeps it from <see cref="Outcome"/>.
/// </summary>
internal sealed class SendDialog : Form
{
    private readonly ClientConfig _cfg;
    private readonly FaxApi _api;
    private readonly Action _openSettings;
    private readonly Icon _icon = IconFactory.Create(true);

    private readonly NumberBox _number = new();
    private readonly TextBox _subject = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _testPage = new() { AutoSize = true, Text = "Send the LANFax test page instead of this document" };
    private readonly Label _lockNote = new()
    {
        AutoSize = true, ForeColor = Color.DarkGoldenrod, MaximumSize = new Size(380, 0), Visible = false, Text = SendSession.LockNote,
    };
    private readonly Label _serverError = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(380, 0) };
    private readonly Button _send = new() { Text = "Send", AutoSize = true, Enabled = false, MinimumSize = new Size(90, 28) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 28) };
    private bool _busy;

    public SendRequest Request { get; }
    public SendSession Session { get; }
    public SendOutcome Outcome { get; private set; } = SendOutcome.Abandoned;
    public string BaseTitle { get; }

    public SendDialog(ClientConfig cfg, FaxApi api, SendRequest request, Action openSettings)
    {
        _cfg = cfg; _api = api; _openSettings = openSettings;
        Request = request;
        Session = new SendSession(request.SpoolId, request.PdfPath);
        var pdfPath = request.PdfPath;

        BaseTitle = pdfPath == null ? "Send a test page" : request.Number != null ? "Retry fax" : "Send fax";
        Text = BaseTitle;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        Icon = _icon;
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
            t.Controls.Add(new Label { Text = "Document:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 0);
            t.Controls.Add(new Label { Text = "PDF, " + Describe(pdfPath), AutoSize = true, Anchor = AnchorStyles.Left }, 1, 0);
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
        t.Controls.Add(_lockNote, 0, 4);
        t.SetColumnSpan(_lockNote, 2);
        t.Controls.Add(_serverError, 0, 5);
        t.SetColumnSpan(_serverError, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_send);
        t.Controls.Add(buttons, 0, 6);
        t.SetColumnSpan(buttons, 2);
        Controls.Add(t);

        _number.Changed += UpdateEnabled;
        if (request.Number != null) _number.Input.Text = request.Number; // retry / orphan: prefilled
        if (request.Subject != null) _subject.Text = request.Subject;
        _testPage.CheckedChanged += (_, _) => { _number.Input.Focus(); };
        _send.Click += async (_, _) => await SendAsync();
        _cancel.Click += (_, _) => Close(); // modeless: a DialogResult on the button would not close the form
        Shown += (_, _) => { Activate(); _number.Focus(); };
        FormClosing += OnClosing;
    }

    private static string Describe(string pdfPath)
    {
        try { return PdfInfo.Describe(PdfInfo.CountPages(pdfPath), new FileInfo(pdfPath).Length); }
        catch (Exception) { return "unknown size"; }
    }

    private void UpdateEnabled() => _send.Enabled = !_busy && (Session.FieldsLocked || _number.State.Allowed);

    private void ApplyLock()
    {
        _number.Input.ReadOnly = true;
        _subject.ReadOnly = true;
        _testPage.Enabled = false;
        _lockNote.Visible = true;
    }

    private async Task SendAsync()
    {
        if (_busy) return;
        SendPlan plan;
        try { plan = Session.Build(_number.RawText, _subject.Text, _testPage.Checked, _cfg.KeepLocalCopies); }
        catch (ArgumentException e) { _serverError.Text = e.Message; return; }

        _busy = true;
        _serverError.Text = "";
        _send.Text = "Sending...";
        _send.Enabled = false; _cancel.Enabled = false;

        int? failure; // null = nothing reached the server
        string message;
        bool unauthorized = false;
        try
        {
            Session.Started(plan);
            Job job;
            if (plan.Route == SendRoute.TestPage)
                job = await _api.SendTestPageAsync(plan.Number, plan.Subject, plan.IdempotencyKey);
            else
            {
                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(plan.PdfPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new DocumentUnreadableException(e);
                }
                using var ms = new MemoryStream(bytes, writable: false);
                job = await _api.PostJobAsync(ms, plan.Number, plan.Subject, plan.IdempotencyKey);
            }
            Session.Succeeded(job);
            Outcome = SendOutcome.Accepted;
            _busy = false;
            Close();
            return;
        }
        catch (FaxApiUnauthorizedException)
        {
            failure = 401; unauthorized = true;
            message = "The server rejected the token. Check Settings and try again.";
        }
        catch (FaxApiException e) { failure = e.StatusCode; message = e.Message; }
        catch (DocumentUnreadableException e) { failure = null; message = "Cannot read the document: " + e.InnerException!.Message; }
        catch (ArgumentException e) { failure = null; message = e.Message; } // FaxApi refused the number before sending
        catch (Exception e)
        {
            // Unknown failure mid-request: treat as "the server may have it".
            FileLog.Error("send failed unexpectedly", e);
            failure = 0; message = "Sending failed: " + e.Message;
        }

        var outcome = Session.Failed(failure);
        FileLog.Info($"send attempt {Session.Attempts} for spool {Session.SpoolId}: {outcome} (status {failure?.ToString() ?? "none"})");
        if (Session.FieldsLocked) ApplyLock();
        _serverError.Text = message;
        _busy = false;
        _send.Text = "Send";
        _cancel.Enabled = true;
        UpdateEnabled();
        if (unauthorized) _openSettings();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        if (Outcome == SendOutcome.Accepted) return;
        // Only an explicit user close discards the document; app exit or shutdown keeps it for next start.
        Outcome = e.CloseReason == CloseReason.UserClosing ? SendOutcome.Cancelled : SendOutcome.Abandoned;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }

    private sealed class DocumentUnreadableException(Exception inner) : Exception(inner.Message, inner);
}
