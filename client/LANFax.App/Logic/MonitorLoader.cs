using System.Text.Json;
using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>
/// The monitor's job-list loads, no UI (<see cref="ClientRuntime"/> owns one): a full load the first time (and after
/// <see cref="ServerChanged"/>), else <c>GET /jobs?updated_since=</c> from <see cref="MonitorModel.ReloadSince"/>,
/// merged into the model by id. One load at a time: requests meanwhile coalesce into one more pass after it, from the
/// earliest resync cursor. A load that straddles <see cref="ServerChanged"/> is dropped (the old server's rows). A
/// failed load still runs the pass queued behind it (once; nothing retries on its own). Owner thread only.
/// </summary>
/// <param name="loaded">After every pass that reached the model: the rows may have changed.</param>
/// <param name="failed">
/// A load failed (<see cref="FaxApiException"/> or <see cref="JsonException"/>) for the current server; at most once
/// per <see cref="ReloadAsync"/> call and server, however many of its passes fail.
/// </param>
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

    /// <summary>The runtime stopped: a load in flight touches nothing when it lands.</summary>
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
        // One failure message per call and server: a queued pass that fails the same way says nothing new, but one
        // for another server (ServerChanged since) does.
        int reportedGeneration = -1;
        try
        {
            while (true)
            {
                var generation = _generation;
                try
                {
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
                }
                catch (Exception e) when (e is FaxApiException or JsonException)
                {
                    if (_stopped) return;
                    if (generation == _generation && reportedGeneration != generation)
                    {
                        reportedGeneration = generation;
                        failed(e);
                    }
                }
                // A request that arrived meanwhile (a reconnect, a resync, the new server's first connect) still
                // runs, even after a failure: the trigger that queued it will not come again.
                if (!_queued) break;
                _queued = false;
                resyncSince = _queuedSince;
                _queuedSince = null;
            }
        }
        finally
        {
            _loading = false;
            _queued = false;
            _queuedSince = null;
        }
    }
}
