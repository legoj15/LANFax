namespace LANFax.App.Logic;

public enum SendRoute { Document, TestPage }

public enum FileAction { Delete, MoveToSent }

/// <summary>
/// What one send attempt does: which API call, with what, under which Idempotency-Key, and what happens to the
/// local PDFs once the job is terminal. <see cref="JobId"/> is the spool file id of the first document; a fax
/// with more documents lists them all in <see cref="PdfPaths"/> (cover page first) and they share its fate. The
/// key comes from <see cref="SendSession"/> and is never derived from it.
/// </summary>
public sealed record SendPlan(Guid JobId, IReadOnlyList<string> PdfPaths, string Number, string? Subject, SendRoute Route,
    FileAction AfterAccept, string IdempotencyKey)
{
    /// <summary>The first (cover) document; the fax's spool id names it.</summary>
    public string PdfPath => PdfPaths.Count > 0 ? PdfPaths[0] : "";

    /// <summary>Every document's spool id, in send order (<see cref="JobId"/> first).</summary>
    public IReadOnlyList<Guid> PartIds =>
        PdfPaths.Select(p => Guid.TryParse(Path.GetFileNameWithoutExtension(p), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty).ToList();

    public string SentPath(string spoolDir) => Path.Combine(spoolDir, "sent", JobId + ".pdf");

    /// <summary>Value equality over the document list (records compare lists by reference, which is not what a
    /// resumed plan's "exactly what will be sent again" test wants).</summary>
    public bool Equals(SendPlan? other) =>
        other is not null && JobId == other.JobId && Number == other.Number && Subject == other.Subject
        && Route == other.Route && AfterAccept == other.AfterAccept && IdempotencyKey == other.IdempotencyKey
        && PdfPaths.SequenceEqual(other.PdfPaths);

    public override int GetHashCode() => HashCode.Combine(JobId, IdempotencyKey);

    public static SendPlan Create(Guid jobId, string pdfPath, string rawNumber, string? subject, bool useTestPage,
        bool keepLocalCopies, string idempotencyKey) =>
        Create(jobId, new[] { pdfPath }, rawNumber, subject, useTestPage, keepLocalCopies, idempotencyKey);

    public static SendPlan Create(Guid jobId, IReadOnlyList<string> pdfPaths, string rawNumber, string? subject,
        bool useTestPage, bool keepLocalCopies, string idempotencyKey)
    {
        var st = NumberInput.Evaluate(rawNumber);
        if (!st.Allowed) throw new ArgumentException(st.Reason.Length > 0 ? st.Reason : "number required", nameof(rawNumber));
        var subj = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        var route = useTestPage ? SendRoute.TestPage : SendRoute.Document;
        // The test page discards the documents; otherwise keep-vs-delete follows the setting.
        var after = !useTestPage && keepLocalCopies ? FileAction.MoveToSent : FileAction.Delete;
        return new SendPlan(jobId, pdfPaths.ToList(), st.Normalized, subj, route, after, idempotencyKey);
    }
}
