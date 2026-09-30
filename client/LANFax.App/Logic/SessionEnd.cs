namespace LANFax.App.Logic;

/// <summary>
/// What the tray does on WM_ENDSESSION. Windows may end the process as soon as that message returns, so when the
/// session really ends (wParam != 0) while a print job is arriving, the pipe listener is stopped synchronously first:
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
        if (!ending || !receiving()) return false;
        log("session ends while a print job is arriving: stopping the listener so the job can finish first");
        try { stopListener(); }
        catch (Exception e) { log("stopping the listener failed: " + e.Message); return true; }
        log(receiving()
            ? "the print job is still arriving after the grace; the next start sets its partial file aside"
            : "the print job finished before the session ended");
        return true;
    }
}
