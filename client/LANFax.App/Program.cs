using LANFax.App.Logic;

namespace LANFax.App;

internal static class Program
{
    internal const string ShowEventName = InstanceGuard.ShowEventName;

    [STAThread]
    private static void Main()
    {
        using var mutex = TryOwnMachineMutex(out bool first);
        if (!first)
        {
            // Same session: ask the running tray to show its "already running" balloon. Otherwise another session
            // owns the machine-wide spool: say so here and leave.
            bool signalled = false;
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); signalled = ev.Set(); } catch (Exception) { }
            if (InstanceGuard.Decide(false, signalled) == InstanceRole.RunningInAnotherSession)
            {
                FileLog.Info("LANFax is already running in another session; exiting");
                ApplicationConfiguration.Initialize();
                ShowOtherSessionNotice();
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => FileLog.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            FileLog.Error("Unhandled exception", e.ExceptionObject as Exception);

        FileLog.Info("LANFax starting");
        using var ctx = new TrayContext();
        Application.Run(ctx);
        FileLog.Info("LANFax exiting");
    }

    /// <summary>
    /// The Global\ mutex. Another user's instance created it with a DACL this user may not open: that is "not first"
    /// too, never a crash.
    /// </summary>
    private static Mutex? TryOwnMachineMutex(out bool first)
    {
        try { return new Mutex(true, InstanceGuard.MutexName, out first); }
        catch (UnauthorizedAccessException) { first = false; return null; }
    }

    /// <summary>A short-lived tray icon whose balloon says why nothing else happens, then the process exits.</summary>
    private static void ShowOtherSessionNotice()
    {
        using var icon = IconFactory.Create(false);
        using var tray = new NotifyIcon { Icon = icon, Text = "LANFax", Visible = true };
        using var timer = new System.Windows.Forms.Timer { Interval = 8000 };
        timer.Tick += (_, _) => Application.ExitThread();
        tray.BalloonTipClosed += (_, _) => Application.ExitThread();
        tray.BalloonTipClicked += (_, _) => Application.ExitThread();
        tray.ShowBalloonTip(8000, "LANFax", InstanceGuard.OtherSessionText, ToolTipIcon.Info);
        timer.Start();
        Application.Run();
        tray.Visible = false;
    }
}
