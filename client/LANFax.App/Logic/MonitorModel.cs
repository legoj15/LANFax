using System.Globalization;
using System.Text.RegularExpressions;
using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>State badge colours, as on the web page; the form maps them to brushes.</summary>
public enum BadgeColor { Gray, Amber, Green, Red, Blue }

public enum PillTone { Neutral, Ok, Bad }

/// <summary>The header pill: short text and whether it reads as good, bad or neither.</summary>
public sealed record Pill(string Text, PillTone Tone);

/// <summary>One grid row, already formatted. <c>Party</c> is the "to" number for a sent job, the "from" for a received one;
/// <c>State</c> is the raw server (or record) state, for gating actions like cancel.</summary>
public sealed record MonitorRow(string Id, string Time, string Party, string Badge, BadgeColor Color, string Pages, string Error,
    string State = "");

/// <summary>
/// The monitor window's view-model, no UI: the jobs and inbox rows (merged by id, newest first, capped), the badge,
/// pill and countdown texts, the reload cursor and the PDF cache path. Display times are local (the zone given),
/// formatted culture-invariant. Owner (UI) thread only.
/// </summary>
public sealed partial class MonitorModel(TimeZoneInfo zone)
{
    /// <summary>Rows kept per grid; the oldest go first.</summary>
    public const int Capacity = 200;
    /// <summary>The first (full) load asks for this many of each, as the web page does.</summary>
    public const int FullLoadLimit = 50;
    /// <summary>A since-reload asks for at most this many (newest first); more than the grids keep anyway.</summary>
    public const int SinceLoadLimit = 500;
    /// <summary>The PBX sends no "receive finished" event: a receiving flag older than this is not shown (web page: 120 s).</summary>
    public static readonly TimeSpan ReceivingTtl = TimeSpan.FromMinutes(2);
    /// <summary>
    /// <c>updated_since</c> compares the server's clock with ours: a reload asks from this much before the last load,
    /// so a client clock ahead of the server's loses nothing. Rows merge by id, so the overlap costs only bytes.
    /// </summary>
    public static readonly TimeSpan SkewMargin = TimeSpan.FromMinutes(5);

    public static string DefaultCacheDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LANFax", "cache");

    private readonly List<Job> _jobs = [];
    private readonly List<Job> _inbox = [];
    private DateTimeOffset? _receivingSince;

    public IReadOnlyList<MonitorRow> Jobs => _jobs.Select(Row).ToList();
    public IReadOnlyList<MonitorRow> Inbox => _inbox.Select(Row).ToList();
    /// <summary>When the last successful load started (our clock), or null before the first.</summary>
    public DateTimeOffset? LastLoad { get; private set; }

    // ---- rows ----

    public MonitorRow Row(Job j)
    {
        var party = j.Direction == "in" ? j.FromNumber : j.ToNumber;
        var (badge, color) = Badge(j.State);
        return new MonitorRow(j.Id, FormatTime(j.CreatedAt), string.IsNullOrWhiteSpace(party) ? "-" : party,
            badge, color, j.Pages?.ToString(CultureInfo.InvariantCulture) ?? "", j.Error ?? "", j.State);
    }

    /// <summary>"Oct 1 07:05": month, day, 24 h time in the display zone, the same in every culture.</summary>
    public string FormatTime(DateTimeOffset t) =>
        TimeZoneInfo.ConvertTime(t, zone).ToString("MMM d HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Badge text and colour for a server job state (or the client's own record state).</summary>
    public static (string Text, BadgeColor Color) Badge(string state) => state switch
    {
        "queued" => ("Queued", BadgeColor.Amber),
        "dialing" => ("Dialing", BadgeColor.Amber),
        "sending" => ("Sending", BadgeColor.Amber), // also SendState.Sending
        "sent" => ("Sent", BadgeColor.Green),
        "failed" => ("Failed", BadgeColor.Red),
        "received" => ("Received", BadgeColor.Blue),
        "cancelled" => ("Cancelled", BadgeColor.Gray),
        SendState.Accepted => ("Accepted", BadgeColor.Amber),
        SendState.Uncertain => ("Uncertain", BadgeColor.Amber),
        SendState.Draft => ("Draft", BadgeColor.Gray),
        SendState.Unknown => ("Unknown", BadgeColor.Gray),
        "" or null => ("?", BadgeColor.Gray),
        _ => (state, BadgeColor.Gray),
    };

    /// <summary>
    /// A live update (stream or reload): inserts or replaces the row with that id in the grid its direction names.
    /// An update older than the row (a slow reload landing after a newer event) is ignored. faxd stamps
    /// <c>updated_at</c> to the second, so two updates can share one: then a strictly less advanced state (by
    /// <see cref="StateRank"/>) never replaces a more advanced one, while an equal rank still applies, because faxd
    /// sends same-state updates that carry new fields (ami.py's annotate adds pages and detail, spool.py's received
    /// adds finished_at). An update identical to the row is a no-op. True if a row changed.
    /// </summary>
    public bool Apply(Job j)
    {
        var list = j.Direction == "in" ? _inbox : _jobs;
        var at = list.FindIndex(x => x.Id == j.Id);
        if (at >= 0)
        {
            var cur = list[at];
            if (j.UpdatedAt < cur.UpdatedAt) return false;
            if (j.UpdatedAt == cur.UpdatedAt && (j == cur || StateRank(j.State) < StateRank(cur.State))) return false;
            list.RemoveAt(at);
        }
        // Newest first by creation; ties by id so the order is stable.
        int i = 0;
        while (i < list.Count && (list[i].CreatedAt > j.CreatedAt ||
               (list[i].CreatedAt == j.CreatedAt && string.CompareOrdinal(list[i].Id, j.Id) > 0))) i++;
        list.Insert(i, j);
        if (list.Count > Capacity) list.RemoveRange(Capacity, list.Count - Capacity);
        return true;
    }

    /// <summary>How far along faxd's pipeline a state is: queued, dialing, sending, then any terminal state. Unknown states rank with queued.</summary>
    public static int StateRank(string state) => state switch
    {
        "dialing" => 1,
        "sending" => 2,
        "sent" or "failed" or "received" or "cancelled" => 3,
        _ => 0,
    };

    /// <summary>A load's answer, merged by id (rows it does not mention stay); <paramref name="startedAt"/> becomes <see cref="LastLoad"/>.</summary>
    public void ApplyLoad(IEnumerable<Job> jobs, DateTimeOffset startedAt)
    {
        foreach (var j in jobs) Apply(j);
        LastLoad = startedAt;
    }

    /// <summary>Another server (settings saved): no rows, and the next load is a full one.</summary>
    public void Clear()
    {
        _jobs.Clear();
        _inbox.Clear();
        LastLoad = null;
        _receivingSince = null;
    }

    // ---- reload cursor ----

    /// <summary>
    /// Where a reload starts: null (a full load) while nothing is loaded yet, even for a resync; else a resync's own
    /// <c>since</c> (the server's cursor) if it gave one, else the last load less <see cref="SkewMargin"/>.
    /// </summary>
    public DateTimeOffset? ReloadSince(DateTimeOffset? resyncSince) =>
        LastLoad is { } last ? resyncSince ?? last - SkewMargin : null;

    /// <summary>The <c>updated_since</c> form faxd reads: UTC, <c>yyyy-MM-ddTHH:mm:ssZ</c>, any culture.</summary>
    public static string FormatSince(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---- status pill ----

    /// <summary>The header pill, as the web page words it.</summary>
    public static Pill PillFor(bool connected, bool everConnected, ServerStatus s, bool receiving)
    {
        if (!connected) return everConnected ? new Pill("reconnecting", PillTone.Bad) : new Pill("connecting", PillTone.Neutral);
        if (ReferenceEquals(s, ServerStatus.Empty)) return new Pill("connected", PillTone.Neutral); // no status event yet
        Pill p;
        int bad = s.Registrations.Count(r => !Registered().IsMatch(r.Status) || Unregistered().IsMatch(r.Status));
        int n = s.Registrations.Count;
        if (n == 0 && !s.AmiConnected) p = new Pill("PBX link down", PillTone.Bad);
        else if (bad > 0) p = new Pill($"{bad} line{(bad == 1 ? "" : "s")} down", PillTone.Bad);
        else if (!s.AmiConnected) p = new Pill("degraded", PillTone.Bad);
        else if (n > 0) p = new Pill($"{n} line{(n == 1 ? "" : "s")} registered", PillTone.Ok);
        else p = new Pill("daemon ok", PillTone.Ok);
        return receiving ? p with { Text = p.Text + ", receiving a fax" } : p;
    }

    /// <summary>Every status event: a receiving flag (re)starts the <see cref="ReceivingTtl"/> clock; none clears it.</summary>
    public void NoteStatus(ServerStatus s, DateTimeOffset now) => _receivingSince = s.Receiving ? now : null;

    /// <summary>Show "receiving a fax": the status says so and the last status event that did is under the TTL old.</summary>
    public bool IsReceiving(ServerStatus s, DateTimeOffset now) =>
        s.Receiving && _receivingSince is { } since && now - since < ReceivingTtl;

    [GeneratedRegex("registered", RegexOptions.IgnoreCase)]
    private static partial Regex Registered();
    [GeneratedRegex("unregistered", RegexOptions.IgnoreCase)]
    private static partial Regex Unregistered();

    // ---- arm card ----

    public static string ArmTitle(ArmState a) => a.Armed ? "ARMED" : "Disarmed";

    /// <summary>The line under the arm state: the countdown while a window runs ("mm:ss", "h:mm:ss" from an hour).</summary>
    public string ArmDetail(ArmState a, DateTimeOffset now)
    {
        if (!a.Armed) return "Send-only. Inbound calls go to the phones.";
        if (a.Until is not { } until) return $"Receiving on ext {a.Ext}";
        var end = TimeZoneInfo.ConvertTime(until, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var left = until - now;
        if (left <= TimeSpan.Zero) return $"Receiving until {end} (ending)";
        var secs = (long)Math.Floor(left.TotalSeconds);
        var text = secs >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{secs / 3600}:{secs / 60 % 60:00}:{secs % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{secs / 60:00}:{secs % 60:00}");
        return $"Receiving until {end} ({text} left)";
    }

    // ---- PDF cache ----

    /// <summary>
    /// <c>&lt;cacheDir&gt;\&lt;id&gt;.pdf</c>. The id comes from the server: only letters, digits, '_' and '-' (faxd's
    /// are "lf" plus 12 hex), at most 64, so it can never name a path outside <paramref name="cacheDir"/>.
    /// </summary>
    public static string PdfCachePath(string cacheDir, string id)
    {
        if (!SafeId().IsMatch(id)) throw new ArgumentException("not a job id: " + id, nameof(id));
        var dir = Path.GetFullPath(cacheDir);
        var path = Path.GetFullPath(Path.Combine(dir, id + ".pdf"));
        if (!string.Equals(Path.GetDirectoryName(path), dir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("not a job id: " + id, nameof(id));
        return path;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();
}

/// <summary>
/// The "arm window ends in 5 minutes" balloon: once per arm window (a window is its end time, so extending or arming
/// again is a new one). The tray checks it on every arm change and on a timer.
/// </summary>
public sealed class ArmExpiryWatch
{
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(5);
    private DateTimeOffset? _warnedFor;

    /// <summary>The balloon text the first time this window has <see cref="Lead"/> or less left; otherwise null.</summary>
    public string? Check(ArmState a, DateTimeOffset now)
    {
        if (!a.Armed || a.Until is not { } until) return null;
        var left = until - now;
        if (left <= TimeSpan.Zero || left > Lead || _warnedFor == until) return null;
        _warnedFor = until;
        return NotificationText.ArmExpiring(until, left);
    }
}
