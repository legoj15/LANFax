using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App;

internal enum DialogOutcome
{
    /// <summary>The server accepted the job (see <see cref="SendSession.Accepted"/>).</summary>
    Accepted,
    /// <summary>The user pressed Cancel or closed the window: discard the PDF.</summary>
    Cancelled,
    /// <summary>The user cancelled a locked or suspect (possibly already sent) job: keep the PDF and its record.</summary>
    Kept,
    /// <summary>Closed by app exit or Windows shutdown: keep the PDF (offered again at next start).</summary>
    Abandoned,
}

/// <summary>One document of the fax being prepared: its spool file (the id) and where it is.</summary>
internal sealed record SendDoc(Guid Id, string Path);

/// <summary>
/// Non-modal send dialog. With a spooled PDF it is the print-to-fax dialog; from the tray's or window's send button it
/// is the compose dialog that picks files (and takes further prints) for one multi-document fax; with no PDF and no
/// documents it is the tray's "Send a test page" prompt. Documents can be added and removed until the send starts;
/// printing another document while this dialog is composing offers to attach it (a setting can turn that question
/// off). One attempt: validate the number, ask the server for a blank-page check, then <see cref="SendExecutor"/> reads
/// the documents, persists <c>sending</c>, calls <see cref="SendSession.Started"/> and sends; the dialog shows the
/// result and locks or unlocks its fields. The dialog never deletes a PDF: <see cref="TrayContext"/> tracks, discards
/// or keeps them from <see cref="Outcome"/> and <see cref="SpoolIds"/>.
/// </summary>
internal sealed class SendDialog : Form
{
    private readonly ClientConfig _cfg;
    private readonly SendExecutor _executor;
    private readonly Action _openSettings;
    private readonly Icon _icon = IconFactory.Create(true);

    private readonly NumberBox _number = new();
    private readonly TextBox _subject = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _testPage = new() { AutoSize = true, Text = "Send the LANFax test page instead of this document" };
    private readonly ListBox _documents = new() { Dock = DockStyle.Fill, Height = 76, IntegralHeight = false, Sorted = false };
    private readonly Button _addDocs = new() { Text = "Add documents...", AutoSize = true };
    private readonly Button _removeDoc = new() { Text = "Remove", AutoSize = true, Enabled = false };
    private readonly Label _attachHint = new()
    {
        AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0), Visible = false,
        Text = "To add more pages or documents, print them now or click Add documents.",
    };
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
    private readonly List<SendDoc> _docs = [];
    private readonly bool _compose;
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

    /// <summary>Every document's spool id, in send order: what to track, discard or offer again.</summary>
    public IReadOnlyList<Guid> SpoolIds => _docs.Select(d => d.Id).ToList();

    /// <summary>
    /// A freshly printed document can join this fax: not locked or resumed, not mid-attempt, and the dialog owns real
    /// documents (a test page cannot take pages). Retried and suspect faxes keep their documents as recorded.
    /// </summary>
    public bool CanAttach => !_busy && !Session.FieldsLocked && !_compose
        && Request.Kind is SendKind.New or SendKind.Compose && _docs.Count > 0;

    /// <param name="journal">Persists the send state of a spooled PDF; null for the tray's test page (nothing on disk).</param>
    public SendDialog(ClientConfig cfg, ISendApi api, SendRequest request, JobTracker? journal, Action openSettings)
    {
        _cfg = cfg; _openSettings = openSettings;
        // A compose fax journals like a printed one: its record must exist before the first byte
        // goes out, or a crash mid-upload means a duplicate fax (a test page has nothing to journal).
        _executor = new SendExecutor(api, cfg,
            request.PdfPath != null || request.Kind == SendKind.Compose ? journal : null,
            (m, e) => { if (e == null) FileLog.Info(m); else FileLog.Error(m, e); });
        Request = request;
        Session = SendSession.For(request);
        _compose = request.Kind == SendKind.Compose && request.PdfPath == null;
        foreach (var path in request.AllPdfPaths)
            _docs.Add(new SendDoc(Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var g) ? g : Guid.NewGuid(), path));

        BaseTitle = _compose ? "Send a fax" : request.PdfPath == null ? "Send a test page" : request.Kind switch
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
        int row = 0;

        var showDocuments = request.PdfPath != null || _compose;
        if (showDocuments)
        {
            t.Controls.Add(new Label { Text = "Documents:", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
            var docPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
            docPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            docPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _documents.Dock = DockStyle.Fill;
            docPanel.Controls.Add(_documents, 0, 0);
            var docButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 0) };
            docButtons.Controls.Add(_addDocs);
            docButtons.Controls.Add(_removeDoc);
            docPanel.Controls.Add(docButtons, 1, 0);
            docPanel.SetRowSpan(_documents, 2);
            t.Controls.Add(docPanel, 1, row++);
            t.Controls.Add(_attachHint, 1, row++);
        }
        t.Controls.Add(new Label { Text = "Number:", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
        t.Controls.Add(_number, 1, row++);
        t.Controls.Add(new Label { Text = "Subject:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
        t.Controls.Add(_subject, 1, row++);
        if (request.PdfPath != null)
        {
            t.Controls.Add(_testPage, 0, row);
            t.SetColumnSpan(_testPage, 2);
            row++;
        }
        else if (!_compose)
        {
            _testPage.Checked = true;
        }
        t.Controls.Add(_lockNote, 0, row);
        t.SetColumnSpan(_lockNote, 2);
        row++;
        t.Controls.Add(_confirm, 0, row);
        t.SetColumnSpan(_confirm, 2);
        row++;
        t.Controls.Add(_serverError, 0, row);
        t.SetColumnSpan(_serverError, 2);
        row++;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_send);
        t.Controls.Add(buttons, 0, row);
        t.SetColumnSpan(buttons, 2);
        Controls.Add(t);

        _number.Changed += UpdateEnabled;
        _confirm.CheckedChanged += (_, _) => UpdateEnabled();
        _documents.SelectedIndexChanged += (_, _) => _removeDoc.Enabled = _documents.SelectedIndex >= 0 && CanChangeDocuments;
        _removeDoc.Click += (_, _) => RemoveSelected();
        _addDocs.Click += (_, _) => PickDocuments();
        if (Session.LockedPlan is { } plan)
        {
            // Resumed from disk: show exactly what will be sent again.
            _number.Input.Text = plan.Number;
            _subject.Text = plan.Subject ?? "";
            _testPage.Checked = plan.Route == SendRoute.TestPage;
            _lockNote.Text = Session.ResumeText; // notes a record from an older version that did not pin the server
            ApplyLock();
        }
        else
        {
            if (request.Number != null) _number.Input.Text = request.Number; // retry / draft / suspect: prefilled
            if (request.Subject != null) _subject.Text = request.Subject;
            _confirm.Visible = request.NeedsConfirmation;
            _attachHint.Visible = showDocuments && cfg.PromptToAddPages && request.Kind is SendKind.New or SendKind.Compose;
        }
        RenderDocuments();
        UpdateEnabled();
        _testPage.CheckedChanged += (_, _) => { _number.Input.Focus(); };
        _send.Click += async (_, _) => await SendAsync();
        _cancel.Click += (_, _) => Close(); // modeless: a DialogResult on the button would not close the form
        Shown += (_, _) => { Activate(); _number.Focus(); if (_compose && _docs.Count == 0) PickDocuments(); };
        FormClosing += OnClosing;
    }

    private bool CanChangeDocuments => !_busy && !Session.FieldsLocked;

    private static TaskCompletionSource CompletedIdle()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    // ---- the document list ----

    private void RenderDocuments()
    {
        int at = _documents.SelectedIndex;
        _documents.Items.Clear();
        foreach (var d in _docs)
            _documents.Items.Add(Path.GetFileName(d.Path));
        if (at >= 0 && at < _documents.Items.Count) _documents.SelectedIndex = at;
        UpdateEnabled();
    }

    private async Task DescribeAsync(SendDoc doc)
    {
        var text = await PdfInfo.DescribeFileAsync(doc.Path); // off the UI thread; resumes on it
        if (IsDisposed || !_docs.Contains(doc)) return;
        int i = _docs.IndexOf(doc);
        if (i >= 0 && i < _documents.Items.Count) _documents.Items[i] = $"{Path.GetFileName(doc.Path)} — {text}";
    }

    private void AddDoc(SendDoc doc)
    {
        _docs.Add(doc);
        DocumentStaged?.Invoke(doc.Id);
        RenderDocuments();
        _ = DescribeAsync(doc);
    }

    /// <summary>Raised with each staged document's spool id, so the runtime never re-offers it as a new print.</summary>
    public event Action<Guid>? DocumentStaged;

    /// <summary>
    /// A document printed while this dialog was open joins the fax (the tray asked the user first). False when it
    /// could not be taken now (busy, locked, already staged): the caller then offers it its own dialog.
    /// </summary>
    public bool AddPrinted(Guid spoolId, string pdfPath)
    {
        if (!CanChangeDocuments || _docs.Any(d => d.Id == spoolId)) return false;
        AddDoc(new SendDoc(spoolId, pdfPath));
        return true;
    }

    private void RemoveSelected()
    {
        if (!CanChangeDocuments || _documents.SelectedIndex < 0) return;
        _docs.RemoveAt(_documents.SelectedIndex);
        RenderDocuments();
    }

    /// <summary>The file picker of "Send a fax" and of Add documents: any number of PDFs, copied into the spool.</summary>
    private void PickDocuments()
    {
        if (!CanChangeDocuments) return;
        using var dlg = new OpenFileDialog
        {
            Title = "Choose the document(s) to fax",
            Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var dir = Path.Combine(_cfg.SpoolDir, "jobs");
            Directory.CreateDirectory(dir);
            foreach (var file in dlg.FileNames)
            {
                var id = Guid.NewGuid();
                var dest = Path.Combine(dir, id + ".pdf");
                File.Copy(file, dest, overwrite: true);
                AddDoc(new SendDoc(id, dest));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _serverError.Text = "Cannot add the document(s): " + e.Message;
        }
    }

    // ---- sending ----

    private void UpdateEnabled() => _send.Enabled = _compose
        ? SendGate.CanSend(_busy, Session.FieldsLocked, _number.State.Allowed, Request.NeedsConfirmation, _confirm.Checked) && _docs.Count > 0
        : SendGate.CanSend(_busy, Session.FieldsLocked, _number.State.Allowed, Request.NeedsConfirmation, _confirm.Checked);

    private void ApplyLock()
    {
        _number.Input.ReadOnly = true;
        _subject.ReadOnly = true;
        _testPage.Enabled = false;
        _lockNote.Visible = true;
        _confirm.Visible = false;
        _addDocs.Enabled = false;
        _removeDoc.Enabled = false;
        _attachHint.Visible = false;
    }

    /// <summary>A definite rejection proved the server has nothing under the old key: the fields are editable again.</summary>
    private void ReleaseLock()
    {
        _number.Input.ReadOnly = false;
        _subject.ReadOnly = false;
        _testPage.Enabled = true;
        _lockNote.Visible = false;
        _addDocs.Enabled = true;
        _removeDoc.Enabled = _documents.SelectedIndex >= 0;
    }

    private void SetBusy(bool busy)
    {
        if (IsDisposed) return;
        _busy = busy;
        _send.Text = busy ? "Sending..." : "Send";
        _cancel.Enabled = !busy;
        _addDocs.Enabled = !busy && CanChangeDocuments;
        _removeDoc.Enabled = !busy && CanChangeDocuments && _documents.SelectedIndex >= 0;
        if (busy) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        else
        {
            _idle.TrySetResult();
            if (_blockingShutdown) { _blockingShutdown = false; ShutdownBlocker.ShutdownBlockReasonDestroy(Handle); }
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
        if (plan.Route == SendRoute.Document && plan.PdfPaths.Count == 0) { _serverError.Text = "Add at least one document."; return; }

        SetBusy(true);
        _serverError.Text = "";
        try
        {
            // 2. The blank-page check (the incident's silent loss): the server renders what would be faxed.
            var (blanks, preError) = await _executor.PreCheckAsync(plan);
            if (preError != null)
            {
                _serverError.Text = preError;
                return;
            }
            if (blanks is { Count: > 0 })
            {
                var which = blanks.Count == 1 ? $"Page {blanks[0]} renders" : $"Pages {string.Join(", ", blanks)} render";
                var r = MessageBox.Show(this,
                    $"{which} as blank paper in the document(s) about to be faxed.\n\nSend the fax anyway?",
                    "Blank page", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
            }

            // 3. The attempt itself.
            var result = await _executor.RunAsync(Session, plan);
            if (result.Status == SendAttemptStatus.Accepted)
            {
                Outcome = DialogOutcome.Accepted;
                SetBusy(false);
                Close();
                return;
            }
            if (Session.FieldsLocked) ApplyLock();
            else if (_number.Input.ReadOnly) ReleaseLock();
            _serverError.Text = result.Message ?? "";
            if (result.Failure is FaxApiUnauthorizedException) _openSettings();
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
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
                if (!_blockingShutdown) _blockingShutdown = ShutdownBlocker.ShutdownBlockReasonCreate(Handle, "A fax is being sent.");
                return;
            }
        }
        if (Outcome == DialogOutcome.Accepted) return;
        // Only an explicit user close discards the document; app exit or shutdown keeps it for next start.
        // While locked (resumed, or uncertain in this run) the server may have the job, and a Suspect document says
        // it may already have been sent: Cancel keeps it.
        Outcome = e.CloseReason != CloseReason.UserClosing ? DialogOutcome.Abandoned
            : Session.CancelKeepsDocumentFor(Request.Kind) ? DialogOutcome.Kept : DialogOutcome.Cancelled;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon.Dispose();
        base.Dispose(disposing);
    }
}
