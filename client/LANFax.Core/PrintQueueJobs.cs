using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LANFax.Core;

/// <summary>A job in a print queue (JOB_INFO_1): its id, JOB_STATUS_* flags and submit time (UTC).</summary>
public sealed record QueuedJob(int Id, uint Status, DateTime SubmittedUtc, string Document, string User);

/// <summary>
/// Reads a print queue's jobs with EnumJobs (winspool.drv, PRINTER_ACCESS_USE, no elevation). Used at tray start to
/// find documents printed while no LANFax listened that the spooler stopped retrying (they sit in "Error").
/// </summary>
public static class PrintQueueJobs
{
    public const uint JobStatusError = 0x2, JobStatusDeleting = 0x4, JobStatusDeleted = 0x100;
    private const uint PrinterAccessUse = 0x8;

    /// <summary>
    /// Jobs in Error that were submitted before <paramref name="startUtc"/> and are not being deleted: documents the
    /// spooler could not deliver while no listener ran. A job printed after the start is the listener's to report.
    /// </summary>
    public static IReadOnlyList<QueuedJob> StuckSince(IEnumerable<QueuedJob> jobs, DateTime startUtc) =>
        jobs.Where(j => (j.Status & JobStatusError) != 0
                        && (j.Status & (JobStatusDeleting | JobStatusDeleted)) == 0
                        && j.SubmittedUtc < startUtc)
            .ToList();

    /// <summary>
    /// <see cref="StuckSince"/> minus the jobs already in <paramref name="seen"/>; the returned ids are added to it, so
    /// repeated checks announce each stuck job once.
    /// </summary>
    public static IReadOnlyList<QueuedJob> NewStuckSince(IEnumerable<QueuedJob> jobs, DateTime startUtc, ISet<int> seen) =>
        StuckSince(jobs, startUtc).Where(j => seen.Add(j.Id)).ToList();

    /// <summary>The queue's jobs in queue order, or null when the printer cannot be opened (missing, no access).</summary>
    /// <exception cref="Win32Exception">EnumJobs failed on an opened printer.</exception>
    public static IReadOnlyList<QueuedJob>? List(string printerName)
    {
        var defaults = new PrinterDefaults { DesiredAccess = PrinterAccessUse };
        if (!OpenPrinter(printerName, out var h, ref defaults)) return null;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                EnumJobs(h, 0, uint.MaxValue, 1, IntPtr.Zero, 0, out var needed, out _);
                if (needed == 0) return [];
                var buf = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!EnumJobs(h, 0, uint.MaxValue, 1, buf, needed, out _, out var returned))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == 122 && attempt < 3) continue; // ERROR_INSUFFICIENT_BUFFER: a job arrived meanwhile
                        throw new Win32Exception(err);
                    }
                    var list = new List<QueuedJob>((int)returned);
                    int size = Marshal.SizeOf<JobInfo1>();
                    for (int i = 0; i < returned; i++)
                    {
                        var j = Marshal.PtrToStructure<JobInfo1>(buf + i * size);
                        var t = j.Submitted;
                        DateTime submitted;
                        try { submitted = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Milliseconds, DateTimeKind.Utc); }
                        catch (ArgumentOutOfRangeException) { submitted = DateTime.MinValue; } // unreadable: counts as old
                        list.Add(new QueuedJob((int)j.JobId, j.Status, DateTime.SpecifyKind(submitted, DateTimeKind.Utc),
                            Marshal.PtrToStringUni(j.Document) ?? "", Marshal.PtrToStringUni(j.UserName) ?? ""));
                    }
                    return list;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { ClosePrinter(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterDefaults { public IntPtr Datatype, DevMode; public uint DesiredAccess; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobInfo1
    {
        public uint JobId;
        public IntPtr PrinterName, MachineName, UserName, Document, Datatype, StatusText;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public SystemTime Submitted;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, ref PrinterDefaults defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumJobsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumJobs(IntPtr handle, uint firstJob, uint noJobs, uint level, IntPtr buf, uint size, out uint needed, out uint returned);
}
