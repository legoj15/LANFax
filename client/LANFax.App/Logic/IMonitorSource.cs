using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>
/// What the monitor window needs from <see cref="ClientRuntime"/>: the snapshot (connection, status with the arm state,
/// the rows in <see cref="Monitor"/>), the events that redraw it, the API calls it makes and where an arm call's answer
/// goes. The window owns no state of its own. Same threading as the runtime: read and subscribe on the owner (UI)
/// thread, events arrive there. The smoke test fakes it.
/// </summary>
public interface IMonitorSource
{
    IMonitorApi MonitorApi { get; }
    bool Connected { get; }
    bool EverConnected { get; }
    ServerStatus Status { get; }
    /// <summary>The rows, loaded and kept current by the source; read them on <see cref="MonitorChanged"/>.</summary>
    MonitorModel Monitor { get; }
    /// <summary>The last load's failure until a load succeeds (null: none), for a window opened after it.</summary>
    Exception? MonitorLoadError { get; }
    /// <summary>Changes when the settings may have named another server: an arm call asked before answers for the old one.</summary>
    int ServerGeneration { get; }
    event Action<bool>? ConnectionChanged;
    /// <summary>The status (arm state included) changed, from the stream, <see cref="NoteArm"/> or a restart's reset.</summary>
    event Action<ServerStatus>? StatusChanged;
    /// <summary><see cref="Monitor"/>'s rows may have changed.</summary>
    event Action? MonitorChanged;
    /// <summary>A load failed for the current server, once per load request.</summary>
    event Action<Exception>? MonitorLoadFailed;
    /// <summary>An arm call's answer, asked at the current <see cref="ServerGeneration"/>: into the status at once.</summary>
    void NoteArm(ArmState answer);
}
