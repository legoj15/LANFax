using LANFax.Core;

namespace LANFax.App.Logic;

public enum AttemptOutcome
{
    /// <summary>Nothing reached the server (bad number, unreadable file). Key and fields unchanged.</summary>
    NotSent,
    /// <summary>
    /// A 4xx: the server refused the request. faxd looks the Idempotency-Key up before it validates or reads the
    /// body, so any 4xx but 401 proves it holds nothing under the key: rotate it (and unlock a locked session).
    /// A 401 fails before the lookup, so it proves nothing about a job the key may already have.
    /// </summary>
    Rejected,
    /// <summary>Timeout, connection error, 3xx, 5xx, an unreadable 2xx or anything unexpected: the server may have queued the job.</summary>
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

    private readonly Func<Guid> _newKey;
    private SendPlan? _inFlight;
    private Uri? _inFlightServer;

    public SendSession(Guid spoolId, string? pdfPath, Func<Guid>? newKey = null)
    {
        SpoolId = spoolId;
        PdfPath = pdfPath;
        _newKey = newKey ?? Guid.NewGuid;
        IdempotencyKey = _newKey().ToString();
    }

    private SendSession(SendPlan locked, Func<Guid> newKey)
    {
        SpoolId = locked.JobId;
        PdfPath = locked.PdfPath;
        _newKey = newKey;
        IdempotencyKey = locked.IdempotencyKey;
        LockedPlan = locked;
        Resumed = true;
    }

    /// <summary>A send that may have reached the server before the app stopped: locked to the stored key and plan.</summary>
    public static SendSession Resume(SendPlan locked, Func<Guid>? newKey = null) => new(locked, newKey ?? Guid.NewGuid);

    public Guid SpoolId { get; }
    public string? PdfPath { get; }
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
    public bool CancelKeepsDocument => FieldsLocked && PdfPath != null;

    /// <summary>
    /// <see cref="CancelKeepsDocument"/>, and also for a <see cref="SendKind.Suspect"/> document: its warning says it
    /// may already have been sent, so a user who cancels rather than risk a duplicate must not lose it.
    /// </summary>
    public bool CancelKeepsDocumentFor(SendKind kind) => PdfPath != null && (FieldsLocked || kind == SendKind.Suspect);
    /// <summary>Locked from a record on disk (a previous run), not by a failure in this dialog.</summary>
    public bool Resumed { get; }
    public bool InFlight => _inFlight != null;
    public Job? Accepted { get; private set; }
    public SendPlan? AcceptedPlan { get; private set; }

    /// <summary>The plan for the next attempt: the frozen one when locked, else built from the fields (may throw ArgumentException).</summary>
    public SendPlan Build(string rawNumber, string? subject, bool useTestPage, bool keepLocalCopies) =>
        LockedPlan ?? SendPlan.Create(SpoolId, PdfPath ?? "", rawNumber, subject, useTestPage || PdfPath == null,
            keepLocalCopies, IdempotencyKey);

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
