using LANFax.App.Logic;

namespace LANFax.App;

internal static class Program
{
    private const string MutexName = @"Local\LANFax.Tray";
    internal const string ShowEventName = @"Local\LANFax.Tray.ShowRunning";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Ask the running instance to show its "already running" balloon, then leave.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch (Exception) { }
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
}
