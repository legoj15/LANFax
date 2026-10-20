using LANFax.Core;

namespace LANFax.App.Logic;

public enum AttemptOutcome
{
    /// <summary>Nothing reached the server (bad number, unreadable file). Key and fields unchanged.</summary>
    NotSent,
    /// <summary>
    /// A 4xx other than 409: the server refused the request. faxd looks the Idempotency-Key up before it validates or
    /// reads the body, so any such 4xx but 401 proves it holds nothing under the key: rotate it (and unlock a locked
    /// session). A 401 fails before the lookup, so it proves nothing about a job the key may already have.
    /// </summary>
    Rejected,
    /// <summary>
    /// Timeout, connection error, 3xx, 5xx, an unreadable 2xx, anything unexpected, or a 409 (the key already holds a
    /// job for another number, so a job exists under it): the server may have queued the job.
    /// </summary>
    Uncertain,
}

/// <summary>
/// Maps what a send attempt threw to its outcome. Only called for failures after <see cref="SendSession.Started"/>:
/// the number and the document are validated before that, so anything thrown once the request may be on the wire
/// is uncertain unless the server gave a definite 4xx.
/// </summary>
public static class SendOutcome
{
    public static AttemptOutcome Classify(int? statusCode) => statusCode switch
    {
        null => AttemptOutcome.NotSent,
        409 => AttemptOutcome.Uncertain, // faxd: "idempotency key already used for a different number": a job exists
        >= 400 and < 500 => AttemptOutcome.Rejected,
        _ => AttemptOutcome.Uncertain,
    };

    public static AttemptOutcome Classify(Exception e) => e switch
    {
        FaxApiConfigException => AttemptOutcome.NotSent, // the request URI could not be built: nothing went out
        FaxApiException f => Classify(f.StatusCode),
        _ => AttemptOutcome.Uncertain,
    };

    /// <summary>The text shown under the fields after a failed attempt.</summary>
    public static string Describe(Exception e) => e switch
    {
        FaxApiUnauthorizedException => "The server rejected the token. Check Settings and try again.",
        FaxApiException f => f.Message,
        _ => "Sending failed: " + e.Message,
    };
}

/// <summary>Whether the Send button is enabled.</summary>
public static class SendGate
{
    /// <param name="needsConfirmation">The document may already have been sent (see <see cref="SendKind.Suspect"/>).</param>
    public static bool CanSend(bool busy, bool locked, bool numberAllowed, bool needsConfirmation, bool confirmed) =>
        !busy && (locked || (numberAllowed && (!needsConfirmation || confirmed)));
}

/// <summary>
/// State of one send dialog across attempts. Money rule: after an uncertain failure the next attempt must be
/// byte-for-byte the same request under the same Idempotency-Key (the server may already be dialling it), so the
/// plan is frozen and the dialog locks its fields. After a definite rejection (a 4xx other than 401, see
/// <see cref="AttemptOutcome.Rejected"/>) the server holds nothing, so the fields become or stay editable and the key
/// rotates (an edited number must never reuse a key the server may know).
/// Each <see cref="Started"/> is answered by exactly one <see cref="Succeeded"/> or <see cref="Failed"/>.
/// </summary>
public sealed class SendSession
{
    public const string LockNote =
        "The server may already have this job; fields are locked. Send will not create a duplicate. " +
        "Cancel keeps the document (see \"Unsent documents\" in the LANFax menu).";
    public const string ResumeNote =
        "This document may already have been sent; Send will not create a duplicate. Cancel keeps the document.";
    public const string SuspectNote =
        "This document may already have been sent. Sending it again may fax it a second time.";
    /// <summary>Added to <see cref="ResumeNote"/> for a record written before the server was recorded.</summary>
    public const string ServerNotRecordedNote = "(It was sent from an earlier version; server not recorded.)";

    private readonly Func<Guid> _newKey;
    private SendPlan? _inFlight;
    private Uri? _inFlightServer;

    public SendSession(Guid spoolId, string? pdfPath, Func<Guid>? newKey = null)
        : this(spoolId, pdfPath == null ? null : new[] { pdfPath }, testPageWhenEmpty: true, newKey) { }

    /// <param name="testPageWhenEmpty">What no documents means: the tray's test page (true) or the compose
    /// dialog, which adds its documents before sending (false).</param>
    public SendSession(Guid spoolId, IReadOnlyList<string>? pdfPaths, bool testPageWhenEmpty, Func<Guid>? newKey = null)
    {
        SpoolId = spoolId;
        PdfPaths = pdfPaths?.ToList() ?? [];
        _testPageWhenEmpty = testPageWhenEmpty;
        _newKey = newKey ?? Guid.NewGuid;
        IdempotencyKey = _newKey().ToString();
    }

    private SendSession(SendPlan locked, Uri? server, Func<Guid> newKey)
    {
        SpoolId = locked.JobId;
        PdfPaths = locked.PdfPaths.ToList();
        _testPageWhenEmpty = true;
        _newKey = newKey;
        IdempotencyKey = locked.IdempotencyKey;
        LockedPlan = locked;
        LockedServer = server;
        Resumed = true;
    }

    /// <summary>
    /// A send that may have reached the server before the app stopped: locked to the stored key and plan, and pinned
    /// to <paramref name="server"/> (the recorded authority, host:port). A record from an older version has no server:
    /// the session is then unpinned (<see cref="ServerNotRecorded"/>).
    /// </summary>
    public static SendSession Resume(SendPlan locked, string? server, Func<Guid>? newKey = null) =>
        new(locked, ServerUri(server), newKey ?? Guid.NewGuid);

    /// <summary>The session a dialog opens for <paramref name="request"/>: resumed (locked and pinned) or fresh.</summary>
    public static SendSession For(SendRequest request) => request.LockedPlan is { } locked
        ? Resume(locked, request.Server)
        : new SendSession(request.SpoolId, request.AllPdfPaths,
            testPageWhenEmpty: request.PdfPath == null && request.Kind != SendKind.Compose);

    private static Uri? ServerUri(string? authority) =>
        !string.IsNullOrEmpty(authority) && Uri.TryCreate("http://" + authority + "/", UriKind.Absolute, out var u) ? u : null;

    private readonly bool _testPageWhenEmpty;

    public Guid SpoolId { get; }
    public IReadOnlyList<string> PdfPaths { get; private set; }

    /// <summary>
    /// The dialog's current document list, in send order: what the next unlocked attempt sends. Ignored while locked
    /// or in flight, where the frozen plan must go out unchanged.
    /// </summary>
    public void SetDocuments(IEnumerable<string> pdfPaths)
    {
        if (LockedPlan != null || _inFlight != null) return;
        PdfPaths = pdfPaths.ToList();
    }

    /// <summary>The first (cover) document; null with none.</summary>
    public string? PdfPath => PdfPaths.Count > 0 ? PdfPaths[0] : null;
    public string IdempotencyKey { get; private set; }
    public int Attempts { get; private set; }
    public SendPlan? LockedPlan { get; private set; }
    /// <summary>
    /// The server the uncertain attempt went to (null when not locked, or locked from disk). The key may be queued
    /// there, so the locked request must not go to any other server.
    /// </summary>
    public Uri? LockedServer { get; private set; }
    public bool FieldsLocked => LockedPlan != null;
    /// <summary>
    /// Cancel keeps the document (listed under "Unsent documents") instead of deleting it: the server may have the
    /// job, so the document and its record must not be lost. The tray's test page has no document to keep.
    /// </summary>
    public bool CancelKeepsDocument => FieldsLocked && PdfPaths.Count > 0;

    /// <summary>
    /// <see cref="CancelKeepsDocument"/>, and also for a <see cref="SendKind.Suspect"/> document: its warning says it
    /// may already have been sent, so a user who cancels rather than risk a duplicate must not lose it.
    /// </summary>
    public bool CancelKeepsDocumentFor(SendKind kind) => PdfPaths.Count > 0 && (FieldsLocked || kind == SendKind.Suspect);
    /// <summary>Locked from a record on disk (a previous run), not by a failure in this dialog.</summary>
    public bool Resumed { get; }
    /// <summary>Resumed from a record written before the server was recorded: nothing pins it to one server.</summary>
    public bool ServerNotRecorded => Resumed && LockedPlan != null && LockedServer == null;
    /// <summary>The note a resumed dialog shows.</summary>
    public string ResumeText => ServerNotRecorded ? ResumeNote + " " + ServerNotRecordedNote : ResumeNote;
    public bool InFlight => _inFlight != null;
    public Job? Accepted { get; private set; }
    public SendPlan? AcceptedPlan { get; private set; }

    /// <summary>The plan for the next attempt: the frozen one when locked, else built from the fields (may throw ArgumentException).</summary>
    public SendPlan Build(string rawNumber, string? subject, bool useTestPage, bool keepLocalCopies) =>
        LockedPlan ?? SendPlan.Create(PrimaryId(), PdfPaths, rawNumber, subject,
            useTestPage || (PdfPaths.Count == 0 && _testPageWhenEmpty),
            keepLocalCopies, IdempotencyKey);

    /// <summary>
    /// The spool id the fax is journalled under: the first document's own id when its file is a spool file
    /// (the print path already uses exactly that id; a compose fax adopts its first document's id so the
    /// record's primary PDF is a real file), else the session's own id (a test page: nothing on disk).
    /// </summary>
    public Guid PrimaryId() =>
        PdfPaths.Count > 0 && Guid.TryParse(Path.GetFileNameWithoutExtension(PdfPaths[0]), out var g) ? g : SpoolId;

    /// <summary>The request is about to go out. Call after the plan is validated and persisted.</summary>
    /// <param name="server">The base URI it goes to; pinned as <see cref="LockedServer"/> if the attempt turns out uncertain.</param>
    public void Started(SendPlan plan, Uri? server = null)
    {
        if (_inFlight != null) throw new InvalidOperationException("an attempt is already in flight");
        Attempts++;
        _inFlight = plan;
        _inFlightServer = server;
    }

    public void Succeeded(Job job)
    {
        var plan = _inFlight ?? throw new InvalidOperationException("Succeeded without Started");
        _inFlight = null;
        Accepted = job;
        AcceptedPlan = plan;
    }

    /// <summary>Records what the attempt in flight threw: classified by <see cref="SendOutcome"/>, with its status.</summary>
    public AttemptOutcome Failed(Exception e)
    {
        var o = SendOutcome.Classify(e);
        if (o == AttemptOutcome.NotSent)
        {
            // Thrown before any request was built (FaxApiConfigException): the attempt in flight provably sent
            // nothing, so it ends without changing the key or the lock.
            _inFlight = null;
            return o;
        }
        return Failed(o, (e as FaxApiException)?.StatusCode);
    }

    /// <summary>
    /// Records a failed attempt. <see cref="AttemptOutcome.NotSent"/> is for validation failures before
    /// <see cref="Started"/>; the other outcomes answer the attempt in flight and throw without one. A locked session
    /// unlocks only on a rejection whose <paramref name="status"/> is known and is not 401.
    /// </summary>
    public AttemptOutcome Failed(AttemptOutcome o, int? status = null)
    {
        if (o == AttemptOutcome.NotSent)
        {
            if (_inFlight != null) throw new InvalidOperationException("an attempt in flight cannot be 'not sent'");
            return o;
        }
        var plan = _inFlight ?? throw new InvalidOperationException($"{o} without Started");
        _inFlight = null;
        if (o == AttemptOutcome.Uncertain)
        {
            LockedPlan ??= plan; // once uncertain, the same request until the server proves it has nothing
            LockedServer ??= _inFlightServer; // ...and to the same server
            return o;
        }
        if (LockedPlan != null)
        {
            if (status is null or 401) return o; // no proof the server lacks the job: stay locked, same key
            LockedPlan = null;
            LockedServer = null;
        }
        IdempotencyKey = _newKey().ToString();
        return o;
    }
}
