using LANFax.Core;

namespace LANFax.App.Logic;

public enum SendAttemptStatus
{
    /// <summary>The server accepted the job (<see cref="SendAttemptResult.Job"/>).</summary>
    Accepted,
    /// <summary>Nothing went out (unreadable document, record not written). Session unchanged.</summary>
    NotSent,
    /// <summary>The request failed after it may have gone out; <see cref="SendAttemptResult.Outcome"/> says how.</summary>
    Failed,
}

/// <param name="Message">The text for the dialog (null on success).</param>
public sealed record SendAttemptResult(SendAttemptStatus Status, string? Message = null, Job? Job = null,
    AttemptOutcome Outcome = AttemptOutcome.NotSent, Exception? Failure = null);

/// <summary>
/// One send attempt, in the order that keeps a crash from producing a duplicate or a lost fax: read the document,
/// persist <c>sending</c> with the key and plan, <see cref="SendSession.Started"/>, then the request. If the record
/// cannot be written nothing is sent. A failure after Started goes to <see cref="SendSession.Failed(Exception)"/> and
/// is recorded (<c>uncertain</c> or <c>draft</c>). The caller validates the plan (<see cref="SendSession.Build"/>)
/// and updates the UI from the result.
/// </summary>
/// <param name="journal">Persists the state of a spooled PDF; null for the tray's test page (nothing on disk).</param>
public sealed class SendExecutor(ISendApi api, JobTracker? journal, Action<string, Exception?>? log = null)
{
    public async Task<SendAttemptResult> RunAsync(SendSession session, SendPlan plan)
    {
        byte[]? bytes = null;
        if (plan.Route == SendRoute.Document)
        {
            try { bytes = await File.ReadAllBytesAsync(plan.PdfPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return NotSent(session, "Cannot read the document: " + e.Message);
            }
        }
        if (journal != null)
        {
            try { journal.BeginSend(plan); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"recording the send of spool {session.SpoolId} failed", e);
                return NotSent(session, "Nothing was sent: the send could not be recorded on disk (" + e.Message + ").");
            }
        }

        // From here on a failure may mean the server has the job.
        session.Started(plan);
        Exception failure;
        try
        {
            Job job;
            if (plan.Route == SendRoute.TestPage)
                job = await api.SendTestPageAsync(plan.Number, plan.Subject, plan.IdempotencyKey);
            else
            {
                using var ms = new MemoryStream(bytes!, writable: false);
                job = await api.PostJobAsync(ms, plan.Number, plan.Subject, plan.IdempotencyKey);
            }
            session.Succeeded(job);
            return new SendAttemptResult(SendAttemptStatus.Accepted, Job: job);
        }
        catch (Exception e) { failure = e; }

        var outcome = session.Failed(failure);
        if (outcome == AttemptOutcome.Uncertain && failure is not FaxApiException) log?.Invoke("send failed unexpectedly", failure);
        log?.Invoke($"send attempt {session.Attempts} for spool {session.SpoolId}: {outcome} " +
                    $"(status {(failure as FaxApiException)?.StatusCode.ToString() ?? failure.GetType().Name})", null);
        journal?.RecordFailure(session, plan);
        return new SendAttemptResult(SendAttemptStatus.Failed, SendOutcome.Describe(failure), Outcome: outcome, Failure: failure);
    }

    private static SendAttemptResult NotSent(SendSession session, string message)
    {
        session.Failed(AttemptOutcome.NotSent);
        return new SendAttemptResult(SendAttemptStatus.NotSent, message);
    }
}
