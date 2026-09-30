using System.Text.Json;
using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>
/// Decides which received faxes get a "Fax received" balloon, so each is announced exactly once whether it arrives
/// as a live <c>received</c> event or is found by a catch-up (<c>GET /jobs?direction=in&amp;updated_since=</c>) after a
/// reconnect or a <c>resync</c>. Keeps the last <see cref="Capacity"/> announced ids, the newest inbound
/// <c>updated_at</c> seen, and whether the first-run baseline was taken, persisted in
/// <c>%LocalAppData%\LANFax\announced.json</c> so a restart does not announce them again. The first completed
/// catch-up takes whatever the server lists (even nothing) as a silent baseline. Never throws on I/O; UI thread only.
/// </summary>
public sealed class InboundAnnouncer
{
    public const int Capacity = 200;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LANFax", "announced.json");

    /// <param name="BaselineTaken">Null in files written before the flag existed: then any saved id or time means taken.</param>
    private sealed record State(List<string>? Ids, DateTimeOffset? LastSeen, bool? BaselineTaken = null, DateTimeOffset? BaselineAt = null);

    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
    private readonly string? _path;
    private readonly Action<string>? _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <param name="path">State file; null keeps the state in memory only.</param>
    /// <param name="now">Clock for <see cref="BaselineAt"/> (tests pin it).</param>
    public InboundAnnouncer(string? path, Action<string>? log = null, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        Load();
    }

    /// <summary>The newest <c>updated_at</c> of an inbound fax seen so far (null before the first one).</summary>
    public DateTimeOffset? LastSeen { get; private set; }

    /// <summary>
    /// True until a catch-up has completed once: that one's list is the inbox as it stood, not news. Read it before
    /// the catch-up request goes out (see <see cref="InboundCatchUp"/>), so a live fax meanwhile cannot change it.
    /// </summary>
    public bool NeedsBaseline { get; private set; } = true;

    /// <summary>When the baseline was taken (this machine's clock; informational).</summary>
    public DateTimeOffset? BaselineAt { get; private set; }

    public int Count => _ids.Count;

    /// <summary>The <c>updated_since</c> for a catch-up: the server's <c>since</c> when it sent one, else <see cref="LastSeen"/>.</summary>
    public DateTimeOffset? SinceFor(DateTimeOffset? requested) => requested ?? LastSeen;

    private static bool IsReceived(Job j) => j.Direction == "in" && j.State == "received";

    /// <summary>A live <c>received</c> event: true when its balloon should show (not announced before).</summary>
    public bool TryAnnounce(Job j)
    {
        if (!IsReceived(j)) return false;
        bool fresh = Add(j);
        Save();
        return fresh;
    }

    /// <summary>
    /// A catch-up list: marks every received fax in it as announced and returns the ones not announced before, oldest
    /// first. <paramref name="asBaseline"/> (the caller's <see cref="NeedsBaseline"/> from before its request) returns
    /// nothing and ends the baseline, even for an empty list: the inbox as it stands is not news.
    /// </summary>
    public IReadOnlyList<Job> Absorb(IEnumerable<Job> fetched, bool asBaseline)
    {
        var news = new List<Job>();
        foreach (var j in fetched.Where(IsReceived).OrderBy(j => j.UpdatedAt).ThenBy(j => j.Id, StringComparer.Ordinal))
            if (Add(j) && !asBaseline) news.Add(j);
        if (NeedsBaseline && asBaseline)
        {
            NeedsBaseline = false;
            BaselineAt = _now();
        }
        Save();
        return news;
    }

    private bool Add(Job j)
    {
        if (LastSeen is not { } seen || j.UpdatedAt > seen) LastSeen = j.UpdatedAt;
        if (!_ids.Add(j.Id)) return false;
        _order.AddLast(j.Id);
        while (_order.Count > Capacity)
        {
            _ids.Remove(_order.First!.Value);
            _order.RemoveFirst();
        }
        return true;
    }

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var st = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Opts);
            LastSeen = st?.LastSeen;
            BaselineAt = st?.BaselineAt;
            foreach (var id in (st?.Ids ?? []).Where(i => !string.IsNullOrEmpty(i)).TakeLast(Capacity))
                if (_ids.Add(id)) _order.AddLast(id);
            NeedsBaseline = !(st?.BaselineTaken ?? (LastSeen != null || _ids.Count > 0));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Invoke($"announced faxes state unreadable ({e.GetType().Name}); starting empty");
            LastSeen = null;
            BaselineAt = null;
            NeedsBaseline = true;
            _ids.Clear();
            _order.Clear();
        }
    }

    private void Save()
    {
        if (_path == null) return;
        var tmp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(new State(_order.ToList(), LastSeen, !NeedsBaseline, BaselineAt), Opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"saving announced faxes failed ({e.GetType().Name}: {e.Message})");
            try { File.Delete(tmp); } catch { }
        }
    }
}

/// <summary>
/// One inbound catch-up: asks faxd for received faxes updated since the resync's time (or the newest one seen) and
/// hands the ones not announced yet to <c>onNews</c>. Whether this is the first-run baseline is decided before the
/// request goes out. faxd lists newest first with no cursor, so a full page (<see cref="PageSize"/>) is asked again
/// with <see cref="MaxJobs"/> (faxd's own cap) instead of paging; anything older than that is skipped and logged.
/// A catch-up requested while one runs is queued (one more run, with the earliest requested time), never dropped.
/// UI thread only.
/// </summary>
/// <param name="fetch">(updated_since, limit) to the jobs list: <c>GET /jobs?direction=in&amp;state=received</c>.</param>
public sealed class InboundCatchUp(InboundAnnouncer announcer, Func<DateTimeOffset?, int, Task<IReadOnlyList<Job>>> fetch,
    Action<IReadOnlyList<Job>> onNews, Action<Exception>? onError = null, Action<string>? log = null)
{
    public const int PageSize = 100;
    public const int MaxJobs = 1000;

    private bool _again;
    private bool _againDefault;
    private DateTimeOffset? _againSince;

    public bool Running { get; private set; }

    /// <param name="since">The resync's <c>since</c>, or null for "since the newest fax seen".</param>
    public async Task RunAsync(DateTimeOffset? since)
    {
        if (Running)
        {
            Queue(since);
            return;
        }
        Running = true;
        try
        {
            await RunOnceAsync(since);
            while (_again)
            {
                var next = _againSince;
                // A plain reconnect merged with a resync: use whichever reaches further back.
                if (next is { } s && _againDefault && announcer.LastSeen is { } seen && seen < s) next = null;
                _again = false; _againDefault = false; _againSince = null;
                await RunOnceAsync(next);
            }
        }
        finally { Running = false; }
    }

    private void Queue(DateTimeOffset? since)
    {
        _again = true;
        if (since is not { } s) _againDefault = true;
        else if (_againSince is not { } q || s < q) _againSince = s;
    }

    private async Task RunOnceAsync(DateTimeOffset? since)
    {
        bool baseline = announcer.NeedsBaseline; // before the request: a live fax meanwhile must not change it
        var after = announcer.SinceFor(since);
        try
        {
            int limit = PageSize;
            var jobs = await fetch(after, limit);
            if (jobs.Count >= limit)
            {
                limit = MaxJobs;
                jobs = await fetch(after, limit);
                if (jobs.Count >= limit)
                    log?.Invoke($"inbound catch-up: more than {MaxJobs} faxes since {after?.ToString("o") ?? "the start"}; only the newest {MaxJobs} were checked");
            }
            var news = announcer.Absorb(jobs, baseline);
            if (news.Count > 0) onNews(news);
        }
        catch (Exception e) { onError?.Invoke(e); }
    }
}
