using LANFax.App.Logic;
using System.Runtime.InteropServices;

namespace LANFax.App;

/// <summary>
/// A hidden top-level window (WM_QUERYENDSESSION goes to top-level windows only; the tray's other windows are not)
/// that asks Windows to wait for sign-out or shutdown while <c>busy()</c> is true, showing the reason set with
/// <see cref="Block"/>. The user can still end the session from the Windows screen; this only keeps a job in flight
/// from being cut off unannounced. When the session ends anyway, WM_ENDSESSION runs <see cref="SessionEnd"/> before
/// returning, which stops the listener synchronously so the arriving job can finish.
/// </summary>
internal sealed class ShutdownBlocker : NativeWindow, IDisposable
{
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;

    private readonly Func<bool> _busy;
    private readonly Action _stopListener;
    private readonly Action<string> _log;
    private bool _blocking;

    public ShutdownBlocker(Func<bool> busy, Action stopListener, Action<string> log)
    {
        _busy = busy;
        _stopListener = stopListener;
        _log = log;
        CreateHandle(new CreateParams { Caption = "LANFax shutdown guard" }); // no WS_VISIBLE: never shown
    }

    /// <summary>Shows <paramref name="reason"/> on the Windows sign-out/shutdown screen until <see cref="Release"/>.</summary>
    public void Block(string reason)
    {
        if (Handle == IntPtr.Zero) return;
        if (_blocking) ShutdownBlockReasonDestroy(Handle);
        _blocking = ShutdownBlockReasonCreate(Handle, reason);
    }

    public void Release()
    {
        if (!_blocking || Handle == IntPtr.Zero) return;
        _blocking = false;
        ShutdownBlockReasonDestroy(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_QUERYENDSESSION && _busy())
        {
            _log("session ending while a print job is arriving: asked Windows to wait");
            m.Result = IntPtr.Zero; // FALSE: not yet
            return;
        }
        if (m.Msg == WM_ENDSESSION) SessionEnd.OnEndSession(m.WParam != IntPtr.Zero, _busy, _stopListener, _log);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Release();
        DestroyHandle();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
