namespace LANFax.Core;

/// <summary>
/// What the client runtime needs from the print listener (<see cref="PrintPipeListener"/>); a seam so the runtime's
/// start, restart and stop ordering can be tested without a real pipe. Events are raised on thread-pool threads.
/// </summary>
public interface IPrintListener : IDisposable
{
    /// <summary>(spool id, path of the saved PDF in spool\jobs).</summary>
    event Action<Guid, string>? NewPrintJob;
    /// <summary>(reason, cause); see <see cref="PrintPipeListener.Error"/>.</summary>
    event Action<string, Exception>? Error;
    /// <summary>A job started (true) or finished (false) arriving; once per job, in that order.</summary>
    event Action<bool>? ReceivingChanged;
    bool IsRunning { get; }
    bool IsReceiving { get; }
    void Start();
    /// <summary>Blocks up to <see cref="PrintPipeListener.StopWait"/>.</summary>
    void Stop();
}
