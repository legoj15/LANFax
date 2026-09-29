using LANFax.Core;

namespace LANFax.App.Logic;

public enum AttemptOutcome
{
    /// <summary>Nothing reached the server (bad number, unreadable file). Key and fields unchanged.</summary>
    NotSent,
    /// <summary>A 4xx: the server refused the request and holds nothing under the key. Rotate the key.</summary>
    Rejected,
    /// <summary>Timeout, connection error, 5xx or an unreadable 2xx: the server may have queued the job.</summary>
    Uncertain,
}

/// <summary>
/// State of one send dialog across attempts. Money rule: after an uncertain failure the next attempt must be
/// byte-for-byte the same request under the same Idempotency-Key (the server may already be dialling it), so the
/// plan is frozen and the dialog locks its fields. After a definite rejection the server holds nothing, so the
/// fields stay editable and the key rotates (an edited number must never reuse a key the server may know).
/// </summary>
public sealed class SendSession
{
    public const string LockNote = "The server may already have this job; fields are locked. Cancel to discard.";

    private readonly Func<Guid> _newKey;
    private SendPlan? _inFlight;

    public SendSession(Guid spoolId, string? pdfPath, Func<Guid>? newKey = null)
    {
        SpoolId = spoolId;
        PdfPath = pdfPath;
        _newKey = newKey ?? Guid.NewGuid;
        IdempotencyKey = _newKey().ToString();
    }

    public Guid SpoolId { get; }
    public string? PdfPath { get; }
    public string IdempotencyKey { get; private set; }
    public int Attempts { get; private set; }
    public SendPlan? LockedPlan { get; private set; }
    public bool FieldsLocked => LockedPlan != null;
    public Job? Accepted { get; private set; }
    public SendPlan? AcceptedPlan { get; private set; }

    /// <summary>The plan for the next attempt: the frozen one when locked, else built from the fields (may throw ArgumentException).</summary>
    public SendPlan Build(string rawNumber, string? subject, bool useTestPage, bool keepLocalCopies) =>
        LockedPlan ?? SendPlan.Create(SpoolId, PdfPath ?? "", rawNumber, subject, useTestPage || PdfPath == null,
            keepLocalCopies, IdempotencyKey);

    public void Started(SendPlan plan)
    {
        Attempts++;
        _inFlight = plan;
    }

    public void Succeeded(Job job)
    {
        Accepted = job;
        AcceptedPlan = _inFlight;
    }

    /// <param name="statusCode">The FaxApiException status (0 = no response), or null when nothing was sent.</param>
    public AttemptOutcome Failed(int? statusCode)
    {
        var o = Classify(statusCode);
        if (LockedPlan != null) return o; // once uncertain, always the same request
        if (o == AttemptOutcome.Uncertain) LockedPlan = _inFlight;
        else if (o == AttemptOutcome.Rejected) IdempotencyKey = _newKey().ToString();
        return o;
    }

    public static AttemptOutcome Classify(int? statusCode) => statusCode switch
    {
        null => AttemptOutcome.NotSent,
        >= 400 and < 500 => AttemptOutcome.Rejected,
        _ => AttemptOutcome.Uncertain,
    };
}
