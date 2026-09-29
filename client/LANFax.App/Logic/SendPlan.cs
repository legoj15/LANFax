namespace LANFax.App.Logic;

public enum SendRoute { Document, TestPage }

public enum FileAction { Delete, MoveToSent }

/// <summary>
/// What one send attempt does: which API call, with what, under which Idempotency-Key, and what happens to the
/// local PDF once the job is terminal. <see cref="JobId"/> is the spool file id; the key comes from
/// <see cref="SendSession"/> and is never derived from it.
/// </summary>
public sealed record SendPlan(Guid JobId, string PdfPath, string Number, string? Subject, SendRoute Route,
    FileAction AfterAccept, string IdempotencyKey)
{
    public string SentPath(string spoolDir) => Path.Combine(spoolDir, "sent", JobId + ".pdf");

    public static SendPlan Create(Guid jobId, string pdfPath, string rawNumber, string? subject, bool useTestPage,
        bool keepLocalCopies, string idempotencyKey)
    {
        var st = NumberInput.Evaluate(rawNumber);
        if (!st.Allowed) throw new ArgumentException(st.Reason.Length > 0 ? st.Reason : "number required", nameof(rawNumber));
        var subj = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        var route = useTestPage ? SendRoute.TestPage : SendRoute.Document;
        // The test page discards the document; otherwise keep-vs-delete follows the setting.
        var after = !useTestPage && keepLocalCopies ? FileAction.MoveToSent : FileAction.Delete;
        return new SendPlan(jobId, pdfPath, st.Normalized, subj, route, after, idempotencyKey);
    }
}
