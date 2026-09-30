using LANFax.App.Logic;

namespace LANFax.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // The show signal exists as soon as the mutex is owned, so a second start during this one's startup reaches it.
        using var claim = InstanceGuard.Acquire(InstanceGuard.MutexName, InstanceGuard.ShowEventName, InstanceGuard.SignalPatience);
        if (claim.Role != InstanceRole.Run)
        {
            // Same session: the running tray was asked to show its "already running" balloon. Otherwise another
            // session owns the machine-wide spool: say so here and leave.
            if (claim.Role == InstanceRole.RunningInAnotherSession)
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
        using var ctx = new TrayContext(claim.ShowSignal!);
        Application.Run(ctx);
        FileLog.Info("LANFax exiting");
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
