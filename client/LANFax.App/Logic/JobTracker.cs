using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>Local states of a spooled document's record (<c>spool\jobs\&lt;guid&gt;.json</c>).</summary>
public static class SendState
{
    /// <summary>Written before the first byte of an attempt goes out: key and plan are fixed.</summary>
    public const string Sending = "sending";
    /// <summary>An attempt failed in a way that may have reached the server: same key and plan on the next try.</summary>
    public const string Uncertain = "uncertain";
    /// <summary>Definitely not accepted: plan editable, no key.</summary>
    public const string Draft = "draft";
    /// <summary>The server accepted it (serverId set); polled until terminal.</summary>
    public const string Accepted = "accepted";
    /// <summary>The server answered 404 for the tracked job (its database was reset): may already have been sent.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// The record for one spooled PDF. <see cref="State"/> is a <see cref="SendState"/> or, once the server reports it,
/// a terminal server state (sent, failed, cancelled). Older records may hold a live server state (queued, dialing),
/// read as <see cref="SendState.Accepted"/>. <see cref="Server"/> is the authority (host:port) the key was sent to;
/// null in records written before it was recorded.
/// </summary>
public sealed record TrackedJob(Guid SpoolId, string? ServerId, string Number, string? Subject, bool KeepLocalCopies,
    string State, string? IdempotencyKey = null, bool TestPage = false, string? Server = null)
{
    [JsonIgnore] public bool IsTerminal => State is "sent" or "failed" or "cancelled" or "received";
    /// <summary>Only an accepted job with a server id is polled; any other or unknown state is never asked about.</summary>
    [JsonIgnore] public bool IsPollable => ServerId != null && State == SendState.Accepted;
}

public enum TerminalOutcome { Removed, MovedToSent, KeptForRetry }

public sealed record TerminalResult(TrackedJob Tracked, TerminalOutcome Outcome);

/// <summary>
/// Persists each spooled document's send state so a crash or exit never turns into a duplicate fax or a lost one.
/// Before an attempt goes out the record says <c>sending</c> with the Idempotency-Key and the full plan; a failure
/// makes it <c>uncertain</c> (same key) or <c>draft</c> (no key); acceptance makes it <c>accepted</c> with the
/// server id, and the PDF stays until the job is terminal: <c>sent</c>/<c>cancelled</c> apply keep-local-copies,
/// <c>failed</c> keeps the PDF for a retry. Single-threaded: call from the UI thread only.
/// </summary>
public sealed class JobTracker(string spoolDir, Action<string>? log = null)
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
    /// <summary>A record another process holds open (antivirus, backup) is read again this many times before giving up.</summary>
    private const int ReadRetries = 3;

    /// <summary>The wait between reads of a record another process holds open (tests set it to zero).</summary>
    public TimeSpan ReadRetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>Reads a record's text (tests wrap it to hold, release or delete the file at an exact moment).</summary>
    public Func<string, string> ReadAllText { get; init; } = File.ReadAllText;

    /// <summary>Ok, not a record (BadContent), held by another process (Unavailable), or gone since it was listed (Missing).</summary>
    private enum LoadStatus { Ok, BadContent, Unavailable, Missing }
    private readonly Dictionary<string, TrackedJob> _byServerId = new(StringComparer.Ordinal);

    public string JobsDir => Path.Combine(spoolDir, "jobs");
    public string SentDir => Path.Combine(spoolDir, "sent");
    public string PdfPath(Guid id) => Path.Combine(JobsDir, id + ".pdf");
    public string JsonPath(Guid id) => Path.Combine(JobsDir, id + ".json");
    private string BadPath(Guid id) => JsonPath(id) + ".bad";

    /// <summary>Accepted jobs still waiting for a terminal state (the 60 s poll asks the server about these).</summary>
    public IReadOnlyList<TrackedJob> Pending => _byServerId.Values.Where(t => t.IsPollable).ToList();

    /// <summary>
    /// Persists <c>sending</c> with the key, plan and the server it goes to (<paramref name="server"/>, the endpoint
    /// snapshot). Throws (IOException, UnauthorizedAccessException) when the record cannot be written: the caller must
    /// then not send.
    /// </summary>
    public void BeginSend(SendPlan plan, Uri? server = null) =>
        Write(FromPlan(plan, SendState.Sending, plan.IdempotencyKey) with { Server = server?.Authority });

    /// <summary>
    /// After a failed attempt: <c>uncertain</c> with the locked plan, key and pinned server
    /// (<see cref="SendSession.LockedServer"/>), else <c>draft</c> without a key.
    /// </summary>
    public void RecordFailure(SendSession session, SendPlan attempted)
    {
        var t = session.LockedPlan is { } locked
            ? FromPlan(locked, SendState.Uncertain, locked.IdempotencyKey) with { Server = session.LockedServer?.Authority }
            : FromPlan(attempted, SendState.Draft, null);
        Save(t);
    }

    /// <summary>Records an accepted document job. Replaces any earlier entry for the same spool file (a retry).</summary>
    public void Track(SendPlan plan, Job accepted)
    {
        ForgetSpool(plan.JobId);
        var t = FromPlan(plan, SendState.Accepted, plan.IdempotencyKey) with { ServerId = accepted.Id };
        _byServerId[accepted.Id] = t;
        Save(t);
    }

    /// <summary>A job event or poll result. Non-null only the first time a tracked job is seen terminal.</summary>
    public TerminalResult? OnJobUpdate(Job j)
    {
        if (!j.IsTerminal || !_byServerId.TryGetValue(j.Id, out var t) || !t.IsPollable) return null;
        t = t with { State = j.State };
        if (j.State == "failed")
        {
            _byServerId[j.Id] = t;
            Save(t);
            return new TerminalResult(t, TerminalOutcome.KeptForRetry);
        }
        _byServerId.Remove(j.Id);
        return new TerminalResult(t, Finish(t));
    }

    /// <summary>
    /// The server answered 404 for a tracked job (e.g. its database was wiped): stop polling it, keep the PDF and
    /// return the record so the user can be offered it with the "may already have been sent" warning. Null when the
    /// job is not tracked or was already handled.
    /// </summary>
    public TrackedJob? MarkUnknown(string serverId)
    {
        if (!_byServerId.TryGetValue(serverId, out var t) || !t.IsPollable) return null;
        t = t with { State = SendState.Unknown };
        _byServerId[serverId] = t;
        Save(t);
        return t;
    }

    /// <summary>The dialog request for a record that needs the user (failed or unknown).</summary>
    public SendRequest RequestFor(TrackedJob t) => new(t.SpoolId, PdfPath(t.SpoolId), t.Number, t.Subject,
        t.State == "failed" ? SendKind.Retry : SendKind.Suspect);

    /// <summary>The user cancelled a dialog: the PDF and its record go.</summary>
    public void Discard(Guid spoolId)
    {
        ForgetSpool(spoolId);
        TryDelete(PdfPath(spoolId));
        TryDelete(JsonPath(spoolId));
        TryDelete(BadPath(spoolId));
    }

    /// <summary>
    /// Startup: re-tracks accepted jobs, completes terminal leftovers, and returns the documents that need the user,
    /// each as the dialog request to open (resume locked, draft, retry, suspect or new). Call before the spool
    /// watcher starts. Records are never left without an outcome.
    /// </summary>
    public IReadOnlyList<SendRequest> Scan()
    {
        var offers = new List<SendRequest>();
        if (!Directory.Exists(JobsDir)) return offers;
        AdoptLeftoverTemps();
        var skipped = new HashSet<Guid>();
        foreach (var json in Directory.EnumerateFiles(JobsDir, "*.json"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(json), out var id)) continue;
            var pdf = PdfPath(id);
            var (t, status) = Load(json);
            if (status == LoadStatus.Missing)
            {
                // Removed since it was listed (a Discard, a finished job): no record, so its PDF, if any, is new.
                log?.Invoke($"record {id} disappeared before it was read; treated as no record");
                continue;
            }
            if (status == LoadStatus.Unavailable)
            {
                // Held open by another process: not corrupt. Leave it for the next start rather than guess.
                log?.Invoke($"record {id} could not be read (in use); skipped this scan");
                skipped.Add(id);
                continue;
            }
            if (t == null)
            {
                // Unreadable: keep it aside as evidence; its PDF is offered below with the warning.
                try { File.Move(json, BadPath(id), overwrite: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"renaming {Path.GetFileName(json)} to .bad failed ({e.GetType().Name}: {e.Message})");
                    if (File.Exists(pdf)) offers.Add(new SendRequest(id, pdf, Kind: SendKind.Suspect));
                }
                continue;
            }
            if (t.IsTerminal && t.State != "failed") { Finish(t); continue; }
            if (!File.Exists(pdf))
            {
                if (t.IsPollable) _byServerId[t.ServerId!] = t; // nothing to resend, but the job still gets its balloon
                else
                {
                    log?.Invoke($"record {id} ({t.State}) has no PDF; removed");
                    TryDelete(json);
                }
                continue;
            }
            switch (t.State)
            {
                case SendState.Sending or SendState.Uncertain when !string.IsNullOrEmpty(t.IdempotencyKey):
                    offers.Add(new SendRequest(id, pdf, t.Number, t.Subject, SendKind.Resume, ToPlan(t, pdf), t.Server));
                    break;
                case SendState.Draft:
                    offers.Add(new SendRequest(id, pdf, t.Number, t.Subject, SendKind.Draft));
                    break;
                case "failed":
                    offers.Add(new SendRequest(id, pdf, t.Number, t.Subject, SendKind.Retry));
                    break;
                case SendState.Accepted when t.IsPollable:
                    _byServerId[t.ServerId!] = t;
                    break;
                default:
                    // unknown, a sending record without a key, an accepted one without a server id, or a state this
                    // version does not know: it may have been sent, so the user decides.
                    offers.Add(new SendRequest(id, pdf, t.Number, t.Subject, SendKind.Suspect));
                    break;
            }
        }
        foreach (var pdf in Directory.EnumerateFiles(JobsDir, "*.pdf"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(pdf), out var id) || File.Exists(JsonPath(id))) continue;
            if (skipped.Contains(id)) continue;
            if (offers.Any(o => o.SpoolId == id)) continue;
            offers.Add(new SendRequest(id, pdf, Kind: File.Exists(BadPath(id)) ? SendKind.Suspect : SendKind.New));
        }
        return offers;
    }

    /// <summary>
    /// A crash between the flushed temp write and its rename leaves <c>&lt;guid&gt;.json.tmp</c> with the newest
    /// record and no <c>&lt;guid&gt;.json</c>: rename it into place when it reads as a record. A temp file beside an
    /// existing record, or one that does not parse (a torn write), is left alone; the next write replaces it.
    /// </summary>
    private void AdoptLeftoverTemps()
    {
        foreach (var tmp in Directory.EnumerateFiles(JobsDir, "*.json.tmp"))
        {
            var json = tmp[..^".tmp".Length];
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(json), out _) || File.Exists(json)) continue;
            if (Load(tmp).Status != LoadStatus.Ok) continue;
            try
            {
                File.Move(tmp, json);
                log?.Invoke($"adopted leftover {Path.GetFileName(tmp)} as the record");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"adopting {Path.GetFileName(tmp)} failed ({e.GetType().Name}: {e.Message})");
            }
        }
    }

    private static TrackedJob FromPlan(SendPlan p, string state, string? key) =>
        new(p.JobId, null, p.Number, p.Subject, p.AfterAccept == FileAction.MoveToSent, state, key,
            p.Route == SendRoute.TestPage);

    /// <summary>Rebuilds the exact plan of a stored attempt (same key, number, subject, route and file action).</summary>
    private static SendPlan ToPlan(TrackedJob t, string pdf) =>
        new(t.SpoolId, pdf, t.Number, t.Subject, t.TestPage ? SendRoute.TestPage : SendRoute.Document,
            !t.TestPage && t.KeepLocalCopies ? FileAction.MoveToSent : FileAction.Delete, t.IdempotencyKey!);

    private void ForgetSpool(Guid spoolId)
    {
        foreach (var k in _byServerId.Where(kv => kv.Value.SpoolId == spoolId).Select(kv => kv.Key).ToList())
            _byServerId.Remove(k);
    }

    private TerminalOutcome Finish(TrackedJob t)
    {
        var pdf = PdfPath(t.SpoolId);
        var outcome = t.KeepLocalCopies ? TerminalOutcome.MovedToSent : TerminalOutcome.Removed;
        try
        {
            if (File.Exists(pdf))
            {
                if (t.KeepLocalCopies)
                {
                    Directory.CreateDirectory(SentDir);
                    File.Move(pdf, Path.Combine(SentDir, t.SpoolId + ".pdf"), overwrite: true);
                }
                else File.Delete(pdf);
            }
            File.Delete(JsonPath(t.SpoolId));
            File.Delete(BadPath(t.SpoolId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep the json with the terminal state so the next Scan finishes the job.
            log?.Invoke($"job {t.ServerId}: finishing local copy failed ({e.GetType().Name}: {e.Message})");
            Save(t);
        }
        return outcome;
    }

    /// <summary>Best effort: logs a failure (the next Scan works from whatever is on disk).</summary>
    private void Save(TrackedJob t)
    {
        try { Write(t); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"spool {t.SpoolId}: saving the {t.State} record failed ({e.GetType().Name}: {e.Message})");
        }
    }

    /// <summary>Durable atomic write: temp file flushed to disk, then renamed over the record.</summary>
    private void Write(TrackedJob t)
    {
        var path = JsonPath(t.SpoolId);
        var tmp = path + ".tmp";
        Directory.CreateDirectory(JobsDir);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(t, Opts));
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reads a record. A sharing violation or denied access is retried <see cref="ReadRetries"/> times
    /// <see cref="ReadRetryDelay"/> apart and then reported as <see cref="LoadStatus.Unavailable"/>; only content
    /// that is not a record is <see cref="LoadStatus.BadContent"/>.
    /// </summary>
    private (TrackedJob? Record, LoadStatus Status) Load(string path)
    {
        string text;
        for (int attempt = 0; ; attempt++)
        {
            try { text = ReadAllText(path); break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (e is FileNotFoundException or DirectoryNotFoundException) return (null, LoadStatus.Missing);
                if (attempt >= ReadRetries)
                {
                    log?.Invoke($"tracker record {Path.GetFileName(path)} unreadable after {ReadRetries} retries ({e.GetType().Name}: {e.Message})");
                    return (null, LoadStatus.Unavailable);
                }
                Thread.Sleep(ReadRetryDelay);
            }
        }
        try
        {
            var t = JsonSerializer.Deserialize<TrackedJob>(text, Opts);
            if (t is not { Number: not null, State: not null }) return (null, LoadStatus.BadContent);
            // Older versions stored the server's live state; with a server id that is an accepted job.
            if (t.ServerId != null && t.State is "queued" or "dialing") t = t with { State = SendState.Accepted };
            return (t, LoadStatus.Ok);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            log?.Invoke($"tracker record {Path.GetFileName(path)} is not a valid record ({e.GetType().Name})");
            return (null, LoadStatus.BadContent);
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"delete {Path.GetFileName(path)} failed ({e.GetType().Name}: {e.Message})");
        }
    }
}
