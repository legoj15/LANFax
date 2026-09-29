using System.Text.Json;
using System.Text.Json.Serialization;
using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>One accepted document job, persisted as <c>spool\jobs\&lt;SpoolId&gt;.json</c> beside its PDF.</summary>
public sealed record TrackedJob(Guid SpoolId, string? ServerId, string Number, string? Subject, bool KeepLocalCopies, string State)
{
    [JsonIgnore] public bool IsTerminal => State is "sent" or "failed" or "cancelled" or "received";
}

public enum TerminalOutcome { Removed, MovedToSent, KeptForRetry }

public sealed record TerminalResult(TrackedJob Tracked, TerminalOutcome Outcome);

/// <summary>A spooled PDF that nobody sent: no json (never accepted) or a failed job waiting for a retry.</summary>
public sealed record Orphan(Guid SpoolId, string PdfPath, string? Number, string? Subject);

/// <summary>
/// Keeps each accepted PDF until its job is terminal, across restarts. <c>sent</c> and <c>cancelled</c> apply the
/// keep-local-copies choice (move to <c>spool\sent\</c> or delete); <c>failed</c> keeps the PDF for a retry.
/// Single-threaded: call from the UI thread only. File errors are logged and retried at the next <see cref="Scan"/>.
/// </summary>
public sealed class JobTracker(string spoolDir, Action<string>? log = null)
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, TrackedJob> _byServerId = new(StringComparer.Ordinal);

    public string JobsDir => Path.Combine(spoolDir, "jobs");
    public string SentDir => Path.Combine(spoolDir, "sent");
    public string PdfPath(Guid id) => Path.Combine(JobsDir, id + ".pdf");
    public string JsonPath(Guid id) => Path.Combine(JobsDir, id + ".json");

    /// <summary>Non-terminal tracked jobs (the 60 s poll asks the server about these).</summary>
    public IReadOnlyList<TrackedJob> Pending => _byServerId.Values.Where(t => !t.IsTerminal).ToList();

    /// <summary>Records an accepted document job. Replaces any earlier entry for the same spool file (a retry).</summary>
    public void Track(SendPlan plan, Job accepted)
    {
        foreach (var k in _byServerId.Where(kv => kv.Value.SpoolId == plan.JobId).Select(kv => kv.Key).ToList())
            _byServerId.Remove(k);
        var t = new TrackedJob(plan.JobId, accepted.Id, plan.Number, plan.Subject,
            plan.AfterAccept == FileAction.MoveToSent, "queued");
        _byServerId[accepted.Id] = t;
        Save(t);
    }

    /// <summary>A job event or poll result. Non-null only the first time a tracked job is seen terminal.</summary>
    public TerminalResult? OnJobUpdate(Job j)
    {
        if (!j.IsTerminal || !_byServerId.TryGetValue(j.Id, out var t) || t.IsTerminal) return null;
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

    /// <summary>The user cancelled a (retry) dialog: the PDF and its record go.</summary>
    public void Discard(Guid spoolId)
    {
        foreach (var k in _byServerId.Where(kv => kv.Value.SpoolId == spoolId).Select(kv => kv.Key).ToList())
            _byServerId.Remove(k);
        TryDelete(PdfPath(spoolId));
        TryDelete(JsonPath(spoolId));
    }

    /// <summary>
    /// Startup: re-tracks accepted jobs, completes terminal leftovers, and returns PDFs that still need the user
    /// (no json, unreadable json, or a failed job). Call before the spool watcher starts.
    /// </summary>
    public IReadOnlyList<Orphan> Scan()
    {
        var orphans = new List<Orphan>();
        if (!Directory.Exists(JobsDir)) return orphans;
        foreach (var json in Directory.EnumerateFiles(JobsDir, "*.json"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(json), out var id)) continue;
            var t = Load(json);
            if (t == null) { TryDelete(json); continue; } // its pdf is offered below as unsent
            var pdf = PdfPath(id);
            if (t.State == "failed")
            {
                if (File.Exists(pdf)) orphans.Add(new Orphan(id, pdf, t.Number, t.Subject));
                else TryDelete(json);
            }
            else if (t.IsTerminal) Finish(t);
            else if (t.ServerId != null) _byServerId[t.ServerId] = t;
        }
        foreach (var pdf in Directory.EnumerateFiles(JobsDir, "*.pdf"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(pdf), out var id)) continue;
            if (!File.Exists(JsonPath(id))) orphans.Add(new Orphan(id, pdf, null, null));
        }
        return orphans;
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
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep the json with the terminal state so the next Scan finishes the job.
            log?.Invoke($"job {t.ServerId}: finishing local copy failed ({e.GetType().Name}: {e.Message})");
            Save(t);
        }
        return outcome;
    }

    private void Save(TrackedJob t)
    {
        var path = JsonPath(t.SpoolId);
        var tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(JobsDir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(t, Opts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"job {t.ServerId}: saving tracker record failed ({e.GetType().Name}: {e.Message})");
        }
    }

    private TrackedJob? Load(string path)
    {
        try
        {
            var t = JsonSerializer.Deserialize<TrackedJob>(File.ReadAllText(path), Opts);
            return t is { Number: not null, State: not null } ? t : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"tracker record {Path.GetFileName(path)} unreadable ({e.GetType().Name})");
            return null;
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
