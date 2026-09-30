using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LANFax.Core;

/// <summary>Pause and resume one print queue. All throw <see cref="Win32Exception"/> on failure (e.g. 5: access denied).</summary>
public interface IPrintQueueControl
{
    void Pause();
    void Resume();
    /// <summary>Whether the queue is paused now.</summary>
    bool IsPaused { get; }
}

/// <summary>
/// The printer's "Keep printed documents" setting (PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS), which kept-job recovery needs.
/// Both throw <see cref="Win32Exception"/> on failure (setting it needs PRINTER_ACCESS_ADMINISTER).
/// </summary>
public interface IKeepPrintedJobs
{
    bool KeepsPrintedJobs { get; }
    void KeepPrintedJobs();
}

/// <summary>The spooler's finished jobs on one queue.</summary>
public interface IPrintJobSource : IDisposable
{
    /// <summary>A job finished printing (its output was written to the port). Raised on a background thread.</summary>
    event Action<int>? JobFinished;
    /// <summary>Notifications were lost (the spooler discarded some, or the watch restarted): counts are unreliable.</summary>
    event Action<string>? Unreliable;
    /// <summary>False before Start, after Stop, and once the watch has died on its own (spooler restart, printer gone).</summary>
    bool IsRunning { get; }
    void Start();
    void Stop();
}

/// <summary>winspool.drv calls for one printer by name.</summary>
internal static class Winspool
{
    public const uint PrinterAccessAdminister = 0x4;
    public const uint PrinterAccessUse = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    public struct PrinterDefaults
    {
        public IntPtr Datatype;
        public IntPtr DevMode;
        public uint DesiredAccess;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenPrinter(string name, out IntPtr handle, ref PrinterDefaults defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPrinter(IntPtr handle, uint level, IntPtr info, uint command);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPrinter(IntPtr handle, uint level, IntPtr buf, uint size, out uint needed);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern IntPtr FindFirstPrinterChangeNotification(IntPtr printer, uint filter, uint options, IntPtr notifyOptions);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextPrinterChangeNotification(IntPtr change, out uint cause, IntPtr notifyOptions, out IntPtr info);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClosePrinterChangeNotification(IntPtr change);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreePrinterNotifyInfo(IntPtr info);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumJobsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumJobs(IntPtr handle, uint firstJob, uint noJobs, uint level, IntPtr buf, uint size, out uint needed, out uint returned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetJobW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetJob(IntPtr handle, uint jobId, uint level, IntPtr job, uint command);

    /// <summary>
    /// The hand-computed PRINTER_INFO_2 and PRINTER_NOTIFY_* offsets are checked on x64 only (the app ships x64):
    /// another width would read the wrong DWORD as job status and leave the loss check blind with no error.
    /// </summary>
    public static void RequireX64()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("LANFax spooler access is built for 64-bit processes only");
    }

    public static IntPtr Open(string printer, uint access)
    {
        var d = new PrinterDefaults { DesiredAccess = access };
        if (!OpenPrinter(printer, out var h, ref d)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return h;
    }
}

/// <summary>Pauses and resumes a queue with SetPrinter (needs PRINTER_ACCESS_ADMINISTER on that printer).</summary>
public sealed class SpoolerPrintQueue(string printerName) : IPrintQueueControl, IKeepPrintedJobs
{
    private const uint PrinterControlPause = 1, PrinterControlResume = 2;
    private const uint PrinterStatusPaused = 0x1;
    private const uint PrinterAttributeKeepPrintedJobs = 0x100;
    // PRINTER_INFO_2: 13 pointers (pServerName .. pSecurityDescriptor), then Attributes, Priority, DefaultPriority,
    // StartTime, UntilTime, Status, cJobs. PRINTER_INFO_5: pPrinterName, pPortName, then Attributes.
    private static int JobCountOffset => 13 * IntPtr.Size + 6 * 4;
    private static int Info5AttributesOffset => 2 * IntPtr.Size;

    public void Pause() => Control(PrinterControlPause);
    public void Resume() => Control(PrinterControlResume);

    private void Control(uint command)
    {
        var h = Winspool.Open(printerName, Winspool.PrinterAccessAdminister);
        try
        {
            if (!Winspool.SetPrinter(h, 0, IntPtr.Zero, command)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Winspool.ClosePrinter(h); }
    }

    public bool KeepsPrintedJobs => WithInfo(5, Winspool.PrinterAccessUse, buf => ((uint)Marshal.ReadInt32(buf, Info5AttributesOffset) & PrinterAttributeKeepPrintedJobs) != 0);

    public void KeepPrintedJobs() => SetKeepPrintedJobs(true);

    /// <summary>
    /// Sets or clears the setting with SetPrinter level 5 (PRINTER_INFO_5: name, port, attributes, timeouts, written
    /// back as read but for the attribute). The handle needs PRINTER_ACCESS_ADMINISTER and PRINTER_ACCESS_USE: with
    /// ADMINISTER alone SetPrinter is refused (access denied, measured 2026-09-30, while pause works). Clearing it is
    /// for live checks only.
    /// </summary>
    public void SetKeepPrintedJobs(bool on) => WithInfo(5, Winspool.PrinterAccessAdminister | Winspool.PrinterAccessUse, buf =>
    {
        uint attributes = (uint)Marshal.ReadInt32(buf, Info5AttributesOffset);
        uint wanted = on ? attributes | PrinterAttributeKeepPrintedJobs : attributes & ~PrinterAttributeKeepPrintedJobs;
        if (wanted == attributes) return true;
        Marshal.WriteInt32(buf, Info5AttributesOffset, (int)wanted);
        return false;
    }, write: true);

    /// <summary>Reads PRINTER_INFO_n; with <paramref name="write"/>, writes it back when <paramref name="use"/> returns false.</summary>
    private T WithInfo<T>(uint level, uint access, Func<IntPtr, T> use, bool write = false)
    {
        Winspool.RequireX64();
        var h = Winspool.Open(printerName, access);
        try
        {
            Winspool.GetPrinter(h, level, IntPtr.Zero, 0, out var needed);
            var buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!Winspool.GetPrinter(h, level, buf, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = use(buf);
                if (write && result is false && !Winspool.SetPrinter(h, level, buf, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Winspool.ClosePrinter(h); }
    }

    /// <summary>Jobs in the queue now (PRINTER_INFO_2 cJobs); needs only PRINTER_ACCESS_USE.</summary>
    public int JobCount => WithInfo(2, Winspool.PrinterAccessUse, buf => Marshal.ReadInt32(buf, JobCountOffset));

    /// <summary>Whether the queue is paused now (PRINTER_INFO_6 status); needs only PRINTER_ACCESS_USE.</summary>
    public bool IsPaused
    {
        get
        {
            var h = Winspool.Open(printerName, Winspool.PrinterAccessUse);
            var buf = Marshal.AllocHGlobal(64);
            try
            {
                if (!Winspool.GetPrinter(h, 6, buf, 64, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return ((uint)Marshal.ReadInt32(buf) & PrinterStatusPaused) != 0;
            }
            finally { Marshal.FreeHGlobal(buf); Winspool.ClosePrinter(h); }
        }
    }
}

/// <summary>
/// Spooler change notifications for one printer's jobs (FindFirstPrinterChangeNotification with the job status
/// field). A job counts as finished the first time its status carries JOB_STATUS_PRINTED; a job cancelled or deleted
/// before that never does. Needs only PRINTER_ACCESS_USE, so it sees every user's jobs on the queue.
/// </summary>
public sealed class SpoolerJobSource(string printerName) : IPrintJobSource
{
    private const uint PrinterChangeJob = 0x0000FF00;
    private const ushort JobNotifyType = 1;
    private const ushort JobNotifyFieldStatus = 0x0A;
    private const uint PrinterNotifyInfoDiscarded = 0x1;
    private const uint PrinterNotifyOptionsRefresh = 0x1;
    public const uint JobStatusPrinted = 0x80;
    public const uint JobStatusDeleted = 0x100;
    /// <summary>Consecutive failed notification reads before the watch gives up (the owner starts a new one).</summary>
    public const int ReadFailureLimit = 10;

    private readonly object _gate = new();
    private Thread? _thread;
    private ManualResetEvent? _stop;
    private volatile bool _running;

    public bool IsRunning => _running;

    public event Action<int>? JobFinished;
    public event Action<string>? Unreliable;
    public Action<string>? Log { get; set; }
    /// <summary>Diagnostics: every (job id, status) the spooler reported.</summary>
    public event Action<int, uint>? StatusSeen;

    public void Start()
    {
        lock (_gate)
        {
            if (_thread != null) return;
            Winspool.RequireX64();
            _stop = new ManualResetEvent(false);
            var stop = _stop;
            // Opened here so a missing printer or a denied open throws to the caller.
            var printer = Winspool.Open(printerName, Winspool.PrinterAccessUse);
            _running = true;
            _thread = new Thread(() => { try { Run(printer, stop); } finally { _running = false; } }) { IsBackground = true, Name = "LANFax spooler jobs" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        ManualResetEvent? stop;
        lock (_gate) { t = _thread; stop = _stop; _thread = null; _stop = null; _running = false; }
        if (t == null) return;
        stop!.Set();
        t.Join(TimeSpan.FromSeconds(5));
        stop.Dispose();
    }

    public void Dispose() => Stop();

    private void Run(IntPtr printer, ManualResetEvent stop)
    {
        // PRINTER_NOTIFY_OPTIONS { Version=2, Flags, Count=1, pTypes } -> PRINTER_NOTIFY_OPTIONS_TYPE { JOB, fields }.
        var fields = Marshal.AllocHGlobal(2);
        var type = Marshal.AllocHGlobal(IntPtr.Size == 8 ? 24 : 20);
        var options = Marshal.AllocHGlobal(IntPtr.Size == 8 ? 24 : 16);
        var change = IntPtr.Zero;
        var finished = new FinishedJobs();
        try
        {
            Marshal.WriteInt16(fields, (short)JobNotifyFieldStatus);
            Marshal.WriteInt16(type, 0, (short)JobNotifyType);
            Marshal.WriteInt16(type, 2, 0);
            Marshal.WriteInt32(type, 4, 0);
            Marshal.WriteInt32(type, 8, 0);
            Marshal.WriteInt32(type, 12, 1);
            Marshal.WriteIntPtr(type, IntPtr.Size == 8 ? 16 : 16, fields);
            Marshal.WriteInt32(options, 0, 2);
            Marshal.WriteInt32(options, 4, 0);
            Marshal.WriteInt32(options, 8, 1);
            Marshal.WriteIntPtr(options, IntPtr.Size == 8 ? 16 : 12, type);

            change = Winspool.FindFirstPrinterChangeNotification(printer, PrinterChangeJob, 0, options);
            if (change == IntPtr.Zero || change == new IntPtr(-1))
            {
                Log?.Invoke($"spooler notifications unavailable: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                Raise(Unreliable, "spooler notifications unavailable");
                return;
            }
            using var changeWait = new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(change, ownsHandle: false) };
            var waits = new WaitHandle[] { stop, changeWait };
            bool refresh = false;
            int failures = 0;
            while (WaitHandle.WaitAny(waits) == 1)
            {
                bool refreshing = refresh; // this read's reply is a full refresh: every job's current status
                Marshal.WriteInt32(options, 4, refreshing ? (int)PrinterNotifyOptionsRefresh : 0);
                if (!Winspool.FindNextPrinterChangeNotification(change, out _, options, out var info))
                {
                    // A handle that stays signalled while every read fails (spooler restarted, printer deleted) must
                    // not spin: back off, then give up and say so; the owner sees IsRunning false and starts anew.
                    var why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    if (++failures >= ReadFailureLimit)
                    {
                        Log?.Invoke($"spooler notification read failed {failures} times in a row ({why}); watch stopped");
                        Raise(Unreliable, "spooler notifications stopped");
                        return;
                    }
                    if (failures == 1) Log?.Invoke($"spooler notification read failed: {why}");
                    if (stop.WaitOne(100 * failures)) return;
                    continue;
                }
                failures = 0;
                if (info == IntPtr.Zero) continue;
                try
                {
                    uint flags = (uint)Marshal.ReadInt32(info, 4);
                    if ((flags & PrinterNotifyInfoDiscarded) != 0)
                    {
                        // Some changes were dropped: ask for a full refresh next time, and say counts are unreliable.
                        refresh = true;
                        Raise(Unreliable, "the spooler discarded job notifications");
                        continue;
                    }
                    refresh = false;
                    int count = Marshal.ReadInt32(info, 8);
                    // PRINTER_NOTIFY_INFO { Version, Flags, Count, aData[] }: aData aligned to 8 on x64. Each
                    // PRINTER_NOTIFY_INFO_DATA { WORD Type, WORD Field, DWORD Reserved, DWORD Id, union NotifyData }.
                    int dataOffset = IntPtr.Size == 8 ? 16 : 12;
                    int dataSize = IntPtr.Size == 8 ? 32 : 20;
                    int unionOffset = IntPtr.Size == 8 ? 16 : 12;
                    for (int i = 0; i < count; i++)
                    {
                        var d = info + dataOffset + i * dataSize;
                        if ((ushort)Marshal.ReadInt16(d, 0) != JobNotifyType || (ushort)Marshal.ReadInt16(d, 2) != JobNotifyFieldStatus) continue;
                        int id = Marshal.ReadInt32(d, 8);
                        uint status = (uint)Marshal.ReadInt32(d, unionOffset);
                        Raise(StatusSeen, id, status);
                        if (finished.OnStatus(id, status, refreshing)) Raise(JobFinished, id);
                    }
                }
                finally { Winspool.FreePrinterNotifyInfo(info); }
            }
        }
        catch (Exception e) { Log?.Invoke($"spooler notifications stopped: {e.Message}"); Raise(Unreliable, "spooler notifications stopped"); }
        finally
        {
            if (change != IntPtr.Zero && change != new IntPtr(-1)) Winspool.FindClosePrinterChangeNotification(change);
            Winspool.ClosePrinter(printer);
            Marshal.FreeHGlobal(options);
            Marshal.FreeHGlobal(type);
            Marshal.FreeHGlobal(fields);
        }
    }

    private static void Raise<T>(Action<T>? h, T arg) { try { h?.Invoke(arg); } catch { /* subscriber bug */ } }
    private static void Raise<T1, T2>(Action<T1, T2>? h, T1 a, T2 b) { try { h?.Invoke(a, b); } catch { /* subscriber bug */ } }
}

/// <summary>
/// Turns a job's stream of status notifications into one "finished" per printing: the first status with
/// JOB_STATUS_PRINTED fires (0x84 then 0x184 is one finish; 0x184 alone is one too). A job leaves the set when its
/// status says DELETED (it will not be heard of again) or no longer says PRINTED (restarted: it will finish again).
/// Kept jobs that are never deleted are capped at <see cref="Capacity"/>, forgetting the lowest (oldest) ids first.
/// A restarted kept job reports 0x800 (RESTART, without PRINTED) first, which re-arms it (measured live, probe -Crash).
/// </summary>
internal sealed class FinishedJobs
{
    public const int Capacity = 4096;
    private const int DeletedMemory = 256;
    private readonly SortedSet<int> _ids = new();
    // Recently deleted ids: a repeated DELETED status must not count as a second finish.
    private readonly HashSet<int> _deleted = new();
    private readonly Queue<int> _deletedOrder = new();

    /// <summary>Jobs finished and not yet deleted or restarted.</summary>
    public int Count => _ids.Count;

    /// <param name="refresh">
    /// The status comes from a full refresh (after discarded notifications): a printed job is recorded but not
    /// counted as a finish, since it may have printed long ago (a kept job, another user's in the ledger). A job
    /// that really finished within the discarded batch is left to the kept-job sweep.
    /// </param>
    public bool OnStatus(int id, uint status, bool refresh = false)
    {
        bool printed = (status & SpoolerJobSource.JobStatusPrinted) != 0;
        bool deleted = (status & SpoolerJobSource.JobStatusDeleted) != 0;
        if (_deleted.Contains(id))
        {
            if (deleted) return false;
            _deleted.Remove(id); // a new job with a reused id (spooler reset)
        }
        bool fire = printed && _ids.Add(id);
        if (!printed || deleted) _ids.Remove(id);
        if (deleted && _deleted.Add(id))
        {
            _deletedOrder.Enqueue(id);
            while (_deletedOrder.Count > DeletedMemory) _deleted.Remove(_deletedOrder.Dequeue());
        }
        while (_ids.Count > Capacity) _ids.Remove(_ids.Min);
        return fire && !refresh;
    }
}
