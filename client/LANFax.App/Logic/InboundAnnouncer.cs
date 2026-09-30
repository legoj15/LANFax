using System.Text.Json;
using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>
/// Decides which received faxes get a "Fax received" balloon, so each is announced exactly once whether it arrives
/// as a live <c>received</c> event or is found by a catch-up (<c>GET /jobs?direction=in&amp;updated_since=</c>) after a
/// reconnect or a <c>resync</c>. Keeps the last <see cref="Capacity"/> announced ids and the newest inbound
/// <c>updated_at</c> seen, persisted in <c>%LocalAppData%\LANFax\announced.json</c> so a restart does not announce
/// them again. The first run with no state takes whatever the server lists as a silent baseline. Never throws on
/// I/O; UI thread only.
/// </summary>
public sealed class InboundAnnouncer
{
    public const int Capacity = 200;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LANFax", "announced.json");

    private sealed record State(List<string>? Ids, DateTimeOffset? LastSeen);

    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
    private readonly string? _path;
    private readonly Action<string>? _log;
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <param name="path">State file; null keeps the state in memory only.</param>
    public InboundAnnouncer(string? path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        Load();
    }

    /// <summary>The newest <c>updated_at</c> of an inbound fax seen so far (null before the first one).</summary>
    public DateTimeOffset? LastSeen { get; private set; }

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
    /// first. With no earlier state at all nothing is returned (baseline): the inbox as it stands is not news.
    /// </summary>
    public IReadOnlyList<Job> Absorb(IEnumerable<Job> fetched)
    {
        bool baseline = LastSeen == null && _ids.Count == 0;
        var news = new List<Job>();
        foreach (var j in fetched.Where(IsReceived).OrderBy(j => j.UpdatedAt).ThenBy(j => j.Id, StringComparer.Ordinal))
            if (Add(j) && !baseline) news.Add(j);
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
            foreach (var id in (st?.Ids ?? []).Where(i => !string.IsNullOrEmpty(i)).TakeLast(Capacity))
                if (_ids.Add(id)) _order.AddLast(id);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Invoke($"announced faxes state unreadable ({e.GetType().Name}); starting empty");
            LastSeen = null;
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(new State(_order.ToList(), LastSeen), Opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"saving announced faxes failed ({e.GetType().Name}: {e.Message})");
            try { File.Delete(tmp); } catch { }
        }
    }
}
