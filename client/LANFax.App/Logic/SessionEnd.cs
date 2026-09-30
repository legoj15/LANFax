namespace LANFax.App.Logic;

/// <summary>
/// What the tray does on WM_ENDSESSION. Windows may end the process as soon as that message returns, so when the
/// session really ends (wParam != 0) the pipe listener is stopped synchronously first, arriving job seen or not:
/// its <c>PrintPipeListener.StopGrace</c> lets the job finish into spool\jobs\, where the next start offers it as unsent.
/// </summary>
public static class SessionEnd
{
    /// <summary>
    /// Handles WM_ENDSESSION. <paramref name="ending"/> is its wParam != 0. Returns true when it stopped the listener.
    /// Never throws: a failing stop is logged.
    /// </summary>
    public static bool OnEndSession(bool ending, Func<bool> receiving, Action stopListener, Action<string> log)
    {
        if (!ending) return false;
        // Stopped whether or not a job is seen arriving: IsReceiving lags a job already connected to the spare
        // instance, and Stop on an idle listener returns at once.
        bool inFlight = receiving();
        log(inFlight
            ? "session ends while a print job is arriving: stopping the listener so the job can finish first"
            : "session ends with no print job in flight: stopping the listener (it still reads a job already connected)");
        try { stopListener(); }
        catch (Exception e) { log("stopping the listener failed: " + e.Message); return true; }
        if (inFlight)
            log(receiving()
                ? "the print job is still arriving after the grace; the next start sets its partial file aside"
                : "the print job finished before the session ended");
        return true;
    }
}
