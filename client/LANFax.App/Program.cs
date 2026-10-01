using LANFax.App.Logic;
using LANFax.Core;

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
        var loc = ConfigLocation.Default();
        var cfg = ClientConfig.Load(loc.ReadPath, ClientConfig.DefaultTokenPath); // never throws; problems land in LoadError
        FileLog.Info($"config: {loc.ReadPath} (save to {loc.Path}, fallback={loc.IsFallback}), server {cfg.Server}:{cfg.Port}, token set={!string.IsNullOrEmpty(cfg.Token)}");
        if (cfg.LoadError != null) FileLog.Error("config: " + cfg.LoadError);

        // The runtime's owner thread is this (the UI) thread: its stream, listener and timer callbacks are posted here,
        // and await continuations come back here. Disposed after the tray (declared first), so the tray's shutdown
        // guard is gone before the listener it guards.
        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        using var runtime = new ClientRuntime(cfg, a => ui.Post(_ => a(), null));
        using var ctx = new TrayContext(runtime, loc, claim.ShowSignal!);
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
