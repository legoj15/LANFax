using System.Text.Json;
using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>
/// The monitor window's job-list loads, no UI: a full load the first time (and after <see cref="ServerChanged"/>),
/// else <c>GET /jobs?updated_since=</c> from <see cref="MonitorModel.ReloadSince"/>, merged into the model by id.
/// One load at a time: requests meanwhile coalesce into one more pass after it, from the earliest resync cursor.
/// A load that straddles <see cref="ServerChanged"/> is dropped (the old server's rows). A failed load drops its
/// queued pass too; the next trigger (reconnect, resync) loads again. Owner (UI) thread only.
/// </summary>
/// <param name="loaded">After every pass that reached the model: the rows may have changed.</param>
/// <param name="failed">A load failed (<see cref="FaxApiException"/> or <see cref="JsonException"/>) for the current server.</param>
public sealed class MonitorLoader(MonitorModel model, Func<IMonitorApi> api, Action loaded, Action<Exception> failed)
{
    private bool _loading;
    private bool _queued;
    private DateTimeOffset? _queuedSince;
    private int _generation;
    private bool _stopped;

    /// <summary>The settings may name another server: drop the rows, and any load in flight when it lands.</summary>
    public void ServerChanged()
    {
        _generation++;
        model.Clear();
    }

    /// <summary>The window is gone: a load in flight touches nothing when it lands.</summary>
    public void Stop() => _stopped = true;

    /// <param name="resyncSince">The server's own cursor from a resync; null for a reconnect or the first load.</param>
    public async Task ReloadAsync(DateTimeOffset? resyncSince)
    {
        if (_stopped) return;
        if (_loading)
        {
            _queued = true;
            if (resyncSince is { } r && (_queuedSince is not { } q || r < q)) _queuedSince = r;
            return;
        }
        _loading = true;
        var generation = _generation;
        try
        {
            while (true)
            {
                generation = _generation;
                var since = model.ReloadSince(resyncSince);
                var started = DateTimeOffset.UtcNow;
                IEnumerable<Job> got;
                if (since == null)
                {
                    var jobs = await api().GetJobsAsync(direction: "out", limit: MonitorModel.FullLoadLimit);
                    var inbox = await api().GetInboxAsync(MonitorModel.FullLoadLimit);
                    got = jobs.Concat(inbox);
                }
                else got = await api().GetJobsAsync(limit: MonitorModel.SinceLoadLimit, updatedSince: since);
                if (_stopped) return;
                if (generation == _generation)
                {
                    model.ApplyLoad(got, started);
                    loaded();
                }
                if (!_queued) break;
                _queued = false;
                resyncSince = _queuedSince;
                _queuedSince = null;
            }
        }
        catch (Exception e) when (e is FaxApiException or JsonException)
        {
            if (!_stopped && generation == _generation) failed(e);
        }
        finally
        {
            _loading = false;
            _queued = false;
            _queuedSince = null;
        }
    }
}
