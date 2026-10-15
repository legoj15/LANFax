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

/// <summary>Why <see cref="SendExecutor.PreviewAsync"/> has no pages to show.</summary>
public enum SendPreviewFailure
{
    /// <summary>Pages are shown.</summary>
    None,
    /// <summary>No documents to render (a test page's sheet is built on the server when it is sent).</summary>
    NoDocuments,
    /// <summary>The settings cannot form a server address.</summary>
    Config,
    /// <summary>A document could not be read.</summary>
    Unreadable,
    /// <summary>The documents together exceed the server's upload limit.</summary>
    TooLarge,
    /// <summary>The server is older than the preview route.</summary>
    NoServerRoute,
    /// <summary>The server could not be reached, or refused the render.</summary>
    Unavailable,
}

/// <param name="Error">The server's reason, when it gave one (shown under the empty preview).</param>
public sealed record SendPreview(IReadOnlyList<byte[]> Pages, int? TotalPages, int Shown, bool Stamped,
    SendPreviewFailure Failure, string? Error = null)
{
    public bool Ok => Failure == SendPreviewFailure.None;

    public static SendPreview Fail(SendPreviewFailure failure, string? error = null) =>
        new(Array.Empty<byte[]>(), null, 0, false, failure, error);
}

/// <summary>
/// One send attempt, in the order that keeps a crash from producing a duplicate or a lost fax: check the documents'
/// sizes and read them, snapshot the endpoint (server, port, token) from the config, persist <c>sending</c> with the key
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

    /// <summary>
    /// Reads a plan's documents (each file, and the fax together, within the server's limit) as the upload's parts.
    /// The first unreadable file means nothing is sent; its name says which one.
    /// </summary>
    private static async Task<IReadOnlyList<(byte[] Bytes, string Name)>?> ReadDocumentsAsync(IReadOnlyList<string> paths)
    {
        var parts = new List<(byte[], string)>();
        long total = 0;
        foreach (var path in paths)
        {
            byte[] bytes;
            try
            {
                if (new FileInfo(path).Length > FaxApi.MaxBodyBytes) return null;
                bytes = await File.ReadAllBytesAsync(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"cannot read {Path.GetFileName(path)}: {e.Message}", e);
            }
            if (bytes.Length > FaxApi.MaxBodyBytes) return null; // grew since the check
            total += bytes.Length;
            parts.Add((bytes, Path.GetFileName(path)));
        }
        return total > FaxApi.MaxBodyBytes ? null : parts;
    }

    public async Task<SendAttemptResult> RunAsync(SendSession session, SendPlan plan)
    {
        IReadOnlyList<(byte[] Bytes, string Name)>? parts = null;
        if (plan.Route == SendRoute.Document)
        {
            try { parts = await ReadDocumentsAsync(plan.PdfPaths); }
            catch (IOException e) { return NotSent(session, "Cannot read the document (" + e.Message + ")."); }
            if (parts == null) return NotSent(session, TooLargeMessage);
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
            // One document goes as its exact bytes; several go as one multipart fax the server combines.
            var job = plan.Route == SendRoute.TestPage
                ? await api.SendTestPageAsync(endpoint, plan.Number, plan.Subject, plan.IdempotencyKey)
                : parts!.Count == 1
                    ? await api.PostJobAsync(endpoint, parts[0].Bytes, plan.Number, plan.Subject, plan.IdempotencyKey)
                    : await api.PostJobAsync(endpoint, parts, plan.Number, plan.Subject, plan.IdempotencyKey);
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

    /// <summary>
    /// The blank-page check before anything is sent: the server renders the combined documents and names pages that
    /// come out blank paper (the incident's silent-loss signature). Null pages means it could not check (an older
    /// server, or a token problem the send itself will report); <paramref name="error"/> blocks the send when the
    /// documents provably cannot go out as one fax.
    /// </summary>
    public async Task<(IReadOnlyList<int>? BlankPages, string? Error)> PreCheckAsync(SendPlan plan)
    {
        if (plan.Route != SendRoute.Document) return (null, null);
        SendEndpoint endpoint;
        try { endpoint = SendEndpoint.From(cfg); }
        catch (FaxApiConfigException) { return (null, null); } // the send itself will say it
        IReadOnlyList<(byte[] Bytes, string Name)> parts;
        try { parts = (await ReadDocumentsAsync(plan.PdfPaths))!; }
        catch (IOException e) { return (null, "Cannot read the document (" + e.Message + ")."); }
        if (parts == null) return (null, TooLargeMessage);
        try
        {
            var check = await api.AnalyzeJobAsync(endpoint, parts);
            return (check?.BlankPages, null);
        }
        catch (FaxApiException e) when (e.StatusCode is 401 or 0 or >= 500)
        {
            return (null, null); // unreachable/busy/forbidden now: do not block on the courtesy check
        }
        catch (FaxApiException e)
        {
            return (null, "The documents could not be checked: " + e.Message);
        }
    }

    /// <summary>
    /// The send window's page preview: the documents as the server would combine and stamp them, rendered. Never
    /// throws (a preview is a courtesy, like <see cref="PreCheckAsync"/>); the failure kind tells the dialog what
    /// note to show. Cancelled by the caller's token when the documents or number changed again.
    /// </summary>
    public async Task<SendPreview> PreviewAsync(IReadOnlyList<string> pdfPaths, string normalizedNumber,
        CancellationToken ct = default)
    {
        if (pdfPaths.Count == 0) return SendPreview.Fail(SendPreviewFailure.NoDocuments);
        SendEndpoint endpoint;
        try { endpoint = SendEndpoint.From(cfg); }
        catch (FaxApiConfigException) { return SendPreview.Fail(SendPreviewFailure.Config); }
        IReadOnlyList<(byte[] Bytes, string Name)>? parts;
        try { parts = await ReadDocumentsAsync(pdfPaths); }
        catch (IOException e) { return SendPreview.Fail(SendPreviewFailure.Unreadable, e.Message); }
        if (parts == null) return SendPreview.Fail(SendPreviewFailure.TooLarge);
        FaxPreview? preview;
        try { preview = await api.PreviewJobAsync(endpoint, parts, normalizedNumber, ct); }
        catch (OperationCanceledException) { throw; }
        catch (FaxApiException e) { return SendPreview.Fail(SendPreviewFailure.Unavailable, e.Message); }
        if (preview == null) return SendPreview.Fail(SendPreviewFailure.NoServerRoute);
        return new SendPreview(preview.Rendered, preview.Pages, preview.Shown, preview.Stamped, SendPreviewFailure.None);
    }

    private static SendAttemptResult NotSent(SendSession session, string message)
    {
        session.Failed(AttemptOutcome.NotSent);
        return new SendAttemptResult(SendAttemptStatus.NotSent, message);
    }
}
