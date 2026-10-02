using System.ComponentModel;
using System.Diagnostics;

namespace LANFax.App.Logic;

/// <summary>What a check said; <see cref="Message"/> is for the user.</summary>
public sealed record UpdateCheckResult(bool Failed, string Message);

/// <summary>
/// The update state shared by the monitor window's banner, the Settings dialog and the tray: whether a newer release
/// is known, whether work is in progress, and the two actions (check, install). UI thread only; <see cref="Changed"/>
/// is raised on it. Nothing here throws to the caller: failures come back as text.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateChecker _checker;
    private readonly Func<bool> _sendInFlight;
    private readonly Action<string> _launch;
    private readonly Action _exit;
    private readonly string _workDir;

    /// <param name="current">The running version.</param>
    /// <param name="sendInFlight">True while a fax is on the wire: the app must not be replaced then.</param>
    /// <param name="launch">Starts the downloaded installer (<see cref="ShellLaunch"/>); throws Win32Exception 1223 when the admin prompt is declined.</param>
    /// <param name="exit">Closes the app once the installer is running so it can replace the files.</param>
    /// <param name="workDir">Where installers are downloaded; the user's temp folder by default.</param>
    public UpdateService(UpdateChecker checker, Version current, Func<bool> sendInFlight, Action<string> launch, Action exit, string? workDir = null)
    {
        _checker = checker; Current = current; _sendInFlight = sendInFlight; _launch = launch; _exit = exit;
        _workDir = workDir ?? Path.Combine(Path.GetTempPath(), "LANFax-update");
    }

    public Version Current { get; }
    /// <summary>The newer release found by the last successful check, if any.</summary>
    public UpdateInfo? Available { get; private set; }
    /// <summary>A check or download is running, or the installer has started and the app is closing (stays true then).</summary>
    public bool Busy { get; private set; }
    /// <summary>The installer is being downloaded (not merely a check).</summary>
    public bool Downloading { get; private set; }
    /// <summary>The user chose "Later" on the banner for the version in <see cref="Available"/>.</summary>
    public bool Dismissed { get; private set; }
    public event Action? Changed;

    /// <summary>Hides the banner until a different (newer) release is found.</summary>
    public void Dismiss() { Dismissed = true; Changed?.Invoke(); }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (Busy) return new UpdateCheckResult(false, "Already working on an update.");
        SetBusy(true);
        try
        {
            var found = await _checker.CheckAsync(Current, ct).ConfigureAwait(true);
            if (found?.Version != Available?.Version) Dismissed = false;
            Available = found;
            return found == null
                ? new UpdateCheckResult(false, $"LANFax is up to date ({Current}).")
                : new UpdateCheckResult(false, $"LANFax {found.Version} is available (you have {Current}).");
        }
        catch (UpdateException e)
        {
            FileLog.Error("update check failed", e);
            return new UpdateCheckResult(true, "Could not check for updates: " + e.Message + ".");
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(true, "Could not check for updates: cancelled.");
        }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// Downloads the available update, starts its installer and exits the app. Returns null when the installer is
    /// running (the app is closing), else why nothing happened.
    /// </summary>
    public async Task<string?> InstallAsync(CancellationToken ct = default)
    {
        if (Busy) return "Already working on an update.";
        if (Available is not { } info) return "No update is available.";
        if (_sendInFlight()) return "Wait for the current fax to finish before updating.";
        SetBusy(true);
        Downloading = true;
        Changed?.Invoke();
        bool launched = false;
        try
        {
            string path;
            try { path = await _checker.DownloadAsync(info, _workDir, ct).ConfigureAwait(true); }
            catch (UpdateException e)
            {
                FileLog.Error("update download failed", e);
                return "Could not download the update: " + e.Message + ".";
            }
            catch (OperationCanceledException) { return "Could not download the update: cancelled."; }
            if (_sendInFlight()) return "Wait for the current fax to finish before updating."; // started while downloading
            try { _launch(path); }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                return "Update cancelled: the installer needs administrator approval.";
            }
            catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
            {
                FileLog.Error("starting the installer failed", e);
                return "Could not start the installer: " + e.Message + ".";
            }
            FileLog.Info($"update: installer for {info.Version} started, exiting");
            launched = true;
        }
        finally
        {
            Downloading = false;
            // Once the installer runs the app is closing: stay busy so no second click starts a second installer.
            if (!launched) SetBusy(false); else Changed?.Invoke();
        }
        _exit();
        return null;
    }

    private void SetBusy(bool busy) { Busy = busy; Changed?.Invoke(); }

    /// <summary>The real launcher: the installer asks for administrator approval itself and relaunches LANFax when done.</summary>
    public static void ShellLaunch(string installerPath)
    {
        using var p = Process.Start(new ProcessStartInfo(installerPath, "/SILENT /CLOSEAPPLICATIONS /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    }
}
