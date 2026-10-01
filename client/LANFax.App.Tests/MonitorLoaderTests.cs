using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

/// <summary>
/// The monitor's loads without a window: one at a time with one coalesced follow-up, a load that straddles a server
/// change is dropped, and a failed load still runs the pass queued behind it.
/// </summary>
public sealed class MonitorLoaderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static Job J(string id, string dir = "out") =>
        new(id, dir, dir == "in" ? "received" : "queued", null, dir == "out" ? "5551234" : null, dir == "in" ? "5559876" : null,
            null, null, 1, null, T0, T0, null);

    private readonly ScriptedApi _api = new();
    private readonly MonitorModel _model = new(TimeZoneInfo.Utc);
    private readonly List<Exception> _failures = [];
    private int _loaded;

    private MonitorLoader Make() => new(_model, () => _api, () => _loaded++, _failures.Add);

    [Fact]
    public async Task A_load_in_flight_across_ServerChanged_is_dropped()
    {
        var full = _api.HoldNextFullLoad();
        var loader = Make();
        var first = loader.ReloadAsync(null);
        loader.ServerChanged(); // settings saved: another server
        full.SetResult([J("old1")]);
        await first;
        Assert.Empty(_model.Jobs); // the old server's rows never land
        Assert.Null(_model.LastLoad); // and the next load is still a full one
    }

    [Fact]
    public async Task Requests_during_a_load_coalesce_into_one_follow_up_from_the_earliest_since()
    {
        var full = _api.HoldNextFullLoad();
        var loader = Make();
        var first = loader.ReloadAsync(null);
        await loader.ReloadAsync(T0.AddMinutes(10)); // both return at once: queued
        await loader.ReloadAsync(T0);
        Assert.Equal(1, _api.FullLoads);
        Assert.Empty(_api.Sinces);
        full.SetResult([J("lf1")]);
        await first;
        Assert.Equal([T0], _api.Sinces); // one follow-up, from the earlier cursor
        Assert.Equal(1, _api.FullLoads);
        Assert.Equal(2, _loaded);
        Assert.Single(_model.Jobs);
    }

    [Fact]
    public async Task A_failed_load_still_runs_its_queued_follow_up_once_and_leaves_no_stale_since()
    {
        var full = _api.HoldNextFullLoad();
        var loader = Make();
        var first = loader.ReloadAsync(null);
        await loader.ReloadAsync(T0); // queued behind the failing load (a reconnect or resync meanwhile)
        full.SetException(new FaxApiException(0, "cannot reach faxd"));
        await first;
        Assert.IsType<FaxApiException>(Assert.Single(_failures));
        Assert.Equal(2, _api.FullLoads); // the queued pass ran: nothing loaded yet, so a full one
        Assert.Empty(_api.Sinces);
        Assert.Equal(1, _loaded);
        Assert.Single(_model.Jobs);

        await loader.ReloadAsync(null); // the next trigger: one plain since-load, not the old resync cursor
        Assert.Equal(2, _api.FullLoads);
        Assert.NotEqual(T0, Assert.Single(_api.Sinces));
    }

    [Fact]
    public async Task Two_failed_passes_in_one_reload_report_the_failure_once()
    {
        var full = _api.HoldNextFullLoad();
        var loader = Make();
        var first = loader.ReloadAsync(null);
        await loader.ReloadAsync(T0); // queued behind the failing load
        _api.FullAnswer = () => Task.FromException<IReadOnlyList<Job>>(new FaxApiException(0, "still unreachable"));
        full.SetException(new FaxApiException(0, "cannot reach faxd"));
        await first;
        Assert.Equal(2, _api.FullLoads); // both passes ran and both failed
        Assert.Equal("cannot reach faxd", Assert.Single(_failures).Message); // one message for one reload, the first

        await loader.ReloadAsync(null); // a later reload is a new call: its failure is reported again
        Assert.Equal(2, _failures.Count);
    }

    [Fact]
    public async Task A_stale_load_that_fails_after_ServerChanged_still_runs_the_load_queued_for_the_new_server()
    {
        var full = _api.HoldNextFullLoad();
        var loader = Make();
        var first = loader.ReloadAsync(null); // the old, unreachable server
        loader.ServerChanged();
        await loader.ReloadAsync(null); // the new server's stream connected: queued
        full.SetException(new FaxApiException(0, "cannot reach faxd"));
        await first;
        Assert.Empty(_failures); // the old server's failure is not shown
        Assert.Equal(2, _api.FullLoads);
        Assert.Equal(1, _loaded);
        Assert.Single(_model.Jobs);
    }

    [Fact]
    public async Task A_resync_before_any_load_is_a_full_load()
    {
        var loader = Make();
        await loader.ReloadAsync(T0);
        Assert.Equal(1, _api.FullLoads);
        Assert.Empty(_api.Sinces);
        Assert.Single(_model.Inbox);
    }

    [Fact]
    public async Task An_unreadable_answer_is_reported_and_Stop_ends_the_loop()
    {
        _api.FullAnswer = () => Task.FromException<IReadOnlyList<Job>>(new JsonException("bad"));
        var loader = Make();
        await loader.ReloadAsync(null);
        Assert.IsType<JsonException>(Assert.Single(_failures));

        _api.FullAnswer = null;
        var full = _api.HoldNextFullLoad();
        var load = loader.ReloadAsync(null);
        loader.Stop(); // the window is gone
        full.SetResult([J("lf1")]);
        await load;
        Assert.Empty(_model.Jobs);
        Assert.Equal(0, _loaded);
    }

    private sealed class ScriptedApi : IMonitorApi
    {
        public int FullLoads;
        public readonly List<DateTimeOffset> Sinces = [];
        public Func<Task<IReadOnlyList<Job>>>? FullAnswer;
        private TaskCompletionSource<IReadOnlyList<Job>>? _held;

        public TaskCompletionSource<IReadOnlyList<Job>> HoldNextFullLoad() => _held = new();

        public Task<IReadOnlyList<Job>> GetJobsAsync(string? direction = null, IEnumerable<string>? states = null, int limit = 100,
            DateTimeOffset? updatedSince = null, CancellationToken ct = default)
        {
            if (updatedSince is { } s)
            {
                Sinces.Add(s);
                return Task.FromResult<IReadOnlyList<Job>>([J("lf1")]);
            }
            FullLoads++;
            if (_held is { } h) { _held = null; return h.Task; }
            return FullAnswer?.Invoke() ?? Task.FromResult<IReadOnlyList<Job>>([J("lf1")]);
        }

        public Task<IReadOnlyList<Job>> GetInboxAsync(int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Job>>([J("in1", "in")]);

        public Task DownloadPdfAsync(string id, string destPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ArmState> ArmAsync(int minutes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArmState> DisarmAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArmState> ReceiveTestAsync(int minutes = 20, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
