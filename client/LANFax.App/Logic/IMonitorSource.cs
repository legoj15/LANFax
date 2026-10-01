using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>
/// What the monitor window needs from <see cref="ClientRuntime"/>: the snapshot, the events that move its grids and
/// pill, and the API calls it makes. Same threading as the runtime: read and subscribe on the owner (UI) thread,
/// events arrive there. The smoke test fakes it.
/// </summary>
public interface IMonitorSource
{
    IMonitorApi MonitorApi { get; }
    bool Connected { get; }
    bool EverConnected { get; }
    ServerStatus Status { get; }
    event Action<bool>? ConnectionChanged;
    event Action<ServerStatus>? StatusChanged;
    /// <summary>Every job update (stream, poll, a send just accepted); outbound ones in practice.</summary>
    event Action<Job>? JobChanged;
    /// <summary>Every received-fax event from the stream.</summary>
    event Action<Job>? InboxChanged;
    /// <summary>The server's replay gap was too large: reload from <c>since</c> (null: the server did not say).</summary>
    event Action<DateTimeOffset?>? Resynced;
}
