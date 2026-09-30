using System.Runtime.InteropServices;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

internal enum DialogOutcome
{
    /// <summary>The server accepted the job (see <see cref="SendSession.Accepted"/>).</summary>
    Accepted,
    /// <summary>The user pressed Cancel or closed the window: discard the PDF.</summary>
    Cancelled,
    /// <summary>The user cancelled a resumed (possibly already sent) job: keep the PDF and its record.</summary>
    Kept,
    /// <summary>Closed by app exit or Windows shutdown: keep the PDF (offered again at next start).</summary>
    Abandoned,
}

/// <summary>
/// Non-modal send dialog. With a spooled PDF it is the print-to-fax dialog; with no PDF it is the tray's
/// "Send a test page" prompt (test-page mode forced, no checkbox). Order of one attempt: validate the number, read
/// the document, persist <c>sending</c> through the journal, then <see cref="SendSession.Started"/> and the request;
/// whatever is thrown after Started is classified by <see cref="SendOutcome"/>. The dialog never deletes the PDF:
/// <see cref="TrayContext"/> tracks, discards or keeps it from <see cref="Outcome"/>.
/// </summary>
internal sealed class SendDialog : Form
{
    private readonly ClientConfig _cfg;
    private readonly FaxApi _api;
    private readonly JobTracker? _journal;
    private readonly Action _openSettings;
    private readonly Icon _icon = IconFactory.Create(true);

    private readonly NumberBox _number = new();
    private readonly TextBox _subject = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _testPage = new() { AutoSize = true, Text = "Send the LANFax test page instead of this document" };
    private readonly Label _document = new() { AutoSize = true, Anchor = AnchorStyles.Left, Text = "PDF, counting pages..." };
    private readonly Label _lockNote = new()
    {
        AutoSize = true, ForeColor = Color.DarkGoldenrod, MaximumSize = new Size(380, 0), Visible = false, Text = SendSession.LockNote,
    };
    private readonly CheckBox _confirm = new()
    {
        AutoSize = true, Visible = false, ForeColor = Color.Firebrick, MaximumSize = new Size(380, 0),
        Text = SendSession.SuspectNote + " Send it anyway.",
    };
    private readonly Label _serverError = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(380, 0) };
    private readonly Button _send = new() { Text = "Send", AutoSize = true, Enabled = false, MinimumSize = new Size(90, 28) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 28) };
    private bool _busy;
    private bool _blockingShutdown;
    private TaskCompletionSource _idle = CompletedIdle();

    public SendRequest Request { get; }
    public SendSession Session { get; }
    public DialogOutcome Outcome { get; private set; } = DialogOutcome.Abandoned;
    public string BaseTitle { get; }
    /// <summary>An attempt is being prepared or is on the wire.</summary>
    public bool Sending => _busy;
    /// <summary>Completes when no attempt is in progress.</summary>
    public Task Idle => _idle.Task;

    /// <param name="journal">Persists the send state of a spooled PDF; null for the tray's test page (nothing on disk).</param>
    public SendDialog(ClientConfig cfg, FaxApi api, SendRequest request, JobTracker? journal, Action openSettings)
    {
        _cfg = cfg; _api = api; _openSettings = openSettings;
        _journal = request.PdfPath != null ? journal : null;
        Request = request;
        Session = request.LockedPlan is { } locked ? SendSession.Resume(locked) : new SendSession(request.SpoolId, request.PdfPath);
        var pdfPath = request.PdfPath;

        BaseTitle = pdfPath == null ? "Send a test page" : request.Kind switch
        {
            SendKind.Retry => "Retry fax",
            SendKind.Resume or SendKind.Suspect => "Unsent fax",
            _ => "Send fax",
        };
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
            t.Controls.Add(_document, 1, 0);
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
        t.Controls.Add(_confirm, 0, 5);
        t.SetColumnSpan(_confirm, 2);
        t.Controls.Add(_serverError, 0, 6);
        t.SetColumnSpan(_serverError, 2);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_send);
        t.Controls.Add(buttons, 0, 7);
        t.SetColumnSpan(buttons, 2);
        Controls.Add(t);

        _number.Changed += UpdateEnabled;
        _confirm.CheckedChanged += (_, _) => UpdateEnabled();
        if (Session.LockedPlan is { } plan)
        {
            // Resumed from disk: show exactly what will be sent again.
            _number.Input.Text = plan.Number;
            _subject.Text = plan.Subject ?? "";
            _testPage.Checked = plan.Route == SendRoute.TestPage;
            _lockNote.Text = SendSession.ResumeNote;
            ApplyLock();
        }
        else
        {
            if (request.Number != null) _number.Input.Text = request.Number; // retry / draft / suspect: prefilled
            if (request.Subject != null) _subject.Text = request.Subject;
            _confirm.Visible = request.NeedsConfirmation;
        }
        UpdateEnabled();
        _testPage.CheckedChanged += (_, _) => { _number.Input.Focus(); };
        _send.Click += async (_, _) => await SendAsync();
        _cancel.Click += (_, _) => Close(); // modeless: a DialogResult on the button would not close the form
        Shown += (_, _) => { Activate(); _number.Focus(); };
        FormClosing += OnClosing;
        if (pdfPath != null) Load += async (_, _) => await DescribeDocumentAsync(pdfPath);
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    private async Task DescribeDocumentAsync(string pdfPath)
    {
        var text = await PdfInfo.DescribeFileAsync(pdfPath); // off the UI thread; resumes on it
        if (!IsDisposed) _document.Text = "PDF, " + text;
    }

    private void UpdateEnabled() => _send.Enabled =
        SendGate.CanSend(_busy, Session.FieldsLocked, _number.State.Allowed, Request.NeedsConfirmation, _confirm.Checked);

    private void ApplyLock()
    {
        _number.Input.ReadOnly = true;
        _subject.ReadOnly = true;
        _testPage.Enabled = false;
        _lockNote.Visible = true;
        _confirm.Visible = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _send.Text = busy ? "Sending..." : "Send";
        _cancel.Enabled = !busy;
        if (busy) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        else
        {
            _idle.TrySetResult();
            if (_blockingShutdown) { _blockingShutdown = false; ShutdownBlockReasonDestroy(Handle); }
        }
        UpdateEnabled();
    }

    private async Task SendAsync()
    {
        if (_busy) return;
        // 1. Validate: nothing has been sent, so failures here change nothing.
        SendPlan plan;
        try { plan = Session.Build(_number.RawText, _subject.Text, _testPage.Checked, _cfg.KeepLocalCopies); }
        catch (ArgumentException e) { _serverError.Text = e.Message; return; }

        SetBusy(true);
        _serverError.Text = "";
        byte[]? bytes = null;
        if (plan.Route == SendRoute.Document)
        {
            try { bytes = await File.ReadAllBytesAsync(plan.PdfPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                NotSent("Cannot read the document: " + e.Message);
                return;
            }
        }
        // 2. Persist "sending" with the key and plan before the first byte goes out.
        if (_journal != null)
        {
            try { _journal.BeginSend(plan); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                FileLog.Error($"recording the send of spool {Session.SpoolId} failed", e);
                NotSent("Nothing was sent: the send could not be recorded on disk (" + e.Message + ").");
                return;
            }
        }

        // 3. Send. From here on a failure may mean the server has the job.
        Session.Started(plan);
        Exception failure;
        try
        {
            Job job;
            if (plan.Route == SendRoute.TestPage)
                job = await _api.SendTestPageAsync(plan.Number, plan.Subject, plan.IdempotencyKey);
            else
            {
                using var ms = new MemoryStream(bytes!, writable: false);
                job = await _api.PostJobAsync(ms, plan.Number, plan.Subject, plan.IdempotencyKey);
            }
            Session.Succeeded(job);
            Outcome = DialogOutcome.Accepted;
            SetBusy(false);
            Close();
            return;
        }
        catch (Exception e) { failure = e; }

        var outcome = Session.Failed(SendOutcome.Classify(failure));
        if (outcome == AttemptOutcome.Uncertain && failure is not FaxApiException) FileLog.Error("send failed unexpectedly", failure);
        FileLog.Info($"send attempt {Session.Attempts} for spool {Session.SpoolId}: {outcome} " +
                     $"(status {(failure as FaxApiException)?.StatusCode.ToString() ?? failure.GetType().Name})");
        _journal?.RecordFailure(Session, plan);
        if (Session.FieldsLocked) ApplyLock();
        _serverError.Text = SendOutcome.Describe(failure);
        SetBusy(false);
        if (failure is FaxApiUnauthorizedException) _openSettings();
    }

    private void NotSent(string message)
    {
        Session.Failed(AttemptOutcome.NotSent);
        _serverError.Text = message;
        SetBusy(false);
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            if (e.CloseReason == CloseReason.WindowsShutDown)
            {
                // Ask Windows to wait; the user sees the reason and can still shut down (the record prevents a duplicate).
                e.Cancel = true;
                if (!_blockingShutdown) _blockingShutdown = ShutdownBlockReasonCreate(Handle, "A fax is being sent.");
                return;
            }
        }
        if (Outcome == DialogOutcome.Accepted) return;
        // Only an explicit user close discards the document; app exit or shutdown keeps it for next start.
        // A resumed send may already be at the server: Cancel keeps it rather than lose track of it.
        Outcome = e.CloseReason != CloseReason.UserClosing ? DialogOutcome.Abandoned
            : Session.Resumed ? DialogOutcome.Kept : DialogOutcome.Cancelled;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
