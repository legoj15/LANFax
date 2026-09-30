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
/// One send attempt, in the order that keeps a crash from producing a duplicate or a lost fax: check the document's
/// size and read it, snapshot the endpoint (server, port, token) from the config, persist <c>sending</c> with the key
/// and plan, <see cref="SendSession.Started"/>, then the request, whose retries all use that snapshot. A document over
/// faxd's limit, a config that cannot form a URI, or a record that cannot be written means nothing is sent. A failure after Started goes to <see cref="SendSession.Failed(Exception)"/> and
/// is recorded (<c>uncertain</c> or <c>draft</c>). The caller validates the plan (<see cref="SendSession.Build"/>)
/// and updates the UI from the result.
/// </summary>
/// <param name="journal">Persists the state of a spooled PDF; null for the tray's test page (nothing on disk).</param>
/// <param name="cfg">Read once per attempt, when it starts; Settings saved during the attempt do not move it.</param>
public sealed class SendExecutor(ISendApi api, ClientConfig cfg, JobTracker? journal, Action<string, Exception?>? log = null)
{
    public static readonly string TooLargeMessage =
        $"Nothing was sent: the document is larger than the server accepts ({FaxApi.MaxBodyBytes / (1024 * 1024)} MB). " +
        "Print fewer pages at a time, or send the test page instead.";

    public async Task<SendAttemptResult> RunAsync(SendSession session, SendPlan plan)
    {
        byte[]? bytes = null;
        if (plan.Route == SendRoute.Document)
        {
            try
            {
                if (new FileInfo(plan.PdfPath).Length > FaxApi.MaxBodyBytes) return NotSent(session, TooLargeMessage);
                bytes = await File.ReadAllBytesAsync(plan.PdfPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return NotSent(session, "Cannot read the document: " + e.Message);
            }
            if (bytes.Length > FaxApi.MaxBodyBytes) return NotSent(session, TooLargeMessage); // grew since the check
        }

        SendEndpoint endpoint;
        try { endpoint = SendEndpoint.From(cfg); }
        catch (FaxApiConfigException e)
        {
            return NotSent(session, $"Nothing was sent: the server address in Settings cannot be used ({e.Message}).");
        }
        if (session.LockedServer is { } pinned &&
            !string.Equals(pinned.Authority, endpoint.BaseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            // The key may be queued on the first server; another faxd would treat it as new and dial again.
            return NotSent(session, $"Nothing was sent: this fax may already be queued on {pinned.Authority}, but Settings now " +
                $"point at {endpoint.BaseUri.Authority}. Change the server back to send it, or Cancel (the document is kept).");
        }

        if (journal != null)
        {
            try { journal.BeginSend(plan, endpoint.BaseUri); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"recording the send of spool {session.SpoolId} failed", e);
                return NotSent(session, "Nothing was sent: the send could not be recorded on disk (" + e.Message + ").");
            }
        }

        // From here on a failure may mean the server has the job.
        session.Started(plan, endpoint.BaseUri);
        Exception failure;
        try
        {
            var job = plan.Route == SendRoute.TestPage
                ? await api.SendTestPageAsync(endpoint, plan.Number, plan.Subject, plan.IdempotencyKey)
                : await api.PostJobAsync(endpoint, bytes!, plan.Number, plan.Subject, plan.IdempotencyKey);
            session.Succeeded(job);
            return new SendAttemptResult(SendAttemptStatus.Accepted, Job: job);
        }
        catch (Exception e) { failure = e; }

        var outcome = session.Failed(failure);
        if (outcome == AttemptOutcome.NotSent)
        {
            // Defensive: a config error the snapshot did not catch. Nothing went out; the record goes back to what
            // the session says (draft, or uncertain while locked).
            log?.Invoke($"send for spool {session.SpoolId} not sent: {failure.Message}", null);
            journal?.RecordFailure(session, plan);
            return new SendAttemptResult(SendAttemptStatus.NotSent, "Nothing was sent: " + failure.Message, Failure: failure);
        }
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
