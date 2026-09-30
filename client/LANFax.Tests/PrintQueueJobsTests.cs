using System.Runtime.InteropServices;
using LANFax.Core;

namespace LANFax.Tests;

public class PrintQueueJobsTests
{
    private static readonly DateTime Start = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private static QueuedJob Job(int id, uint status, int minutesBeforeStart) =>
        new(id, status, Start.AddMinutes(-minutesBeforeStart), "doc", "user");

    [Fact]
    public void StuckJobsAreTheOnesInErrorSubmittedBeforeTheStart()
    {
        var jobs = new[]
        {
            Job(1, PrintQueueJobs.JobStatusError, 60),                                    // stuck
            Job(2, PrintQueueJobs.JobStatusError | 0x10 /* printing */, 5),                // stuck ("Error, Printing")
            Job(3, 0, 60),                                                                // waiting normally
            Job(4, PrintQueueJobs.JobStatusError, -1),                                     // printed after the start: the listener sees it
            Job(5, PrintQueueJobs.JobStatusError | PrintQueueJobs.JobStatusDeleting, 60),  // going away
            Job(6, PrintQueueJobs.JobStatusError | PrintQueueJobs.JobStatusDeleted, 60),
        };
        Assert.Equal([1, 2], PrintQueueJobs.StuckSince(jobs, Start).Select(j => j.Id));
    }

    [Fact]
    public void AStuckJobIsSelectedOnceAcrossRepeatedChecks()
    {
        var seen = new HashSet<int>();
        var first = new[] { Job(1, 0, 5), Job(2, PrintQueueJobs.JobStatusError, 5) };
        Assert.Equal([2], PrintQueueJobs.NewStuckSince(first, Start, seen).Select(j => j.Id));
        // The next check: job 1 has since gone to Error too; job 2 is not announced again.
        var second = new[] { Job(1, PrintQueueJobs.JobStatusError, 5), Job(2, PrintQueueJobs.JobStatusError, 5) };
        Assert.Equal([1], PrintQueueJobs.NewStuckSince(second, Start, seen).Select(j => j.Id));
        Assert.Empty(PrintQueueJobs.NewStuckSince(second, Start, seen));
    }

    [Fact]
    public void AMissingPrinterGivesNull() =>
        Assert.Null(PrintQueueJobs.List("LANFax no such printer " + Guid.NewGuid().ToString("N")));

    [StockPdfPrinterFact]
    public void AnInstalledPrinterCanBeListed()
    {
        // Exercises OpenPrinter/EnumJobs and the JOB_INFO_1 layout on a queue every Windows 10/11 has. One RAW job is
        // started and left open (never ended), so it stays in the queue as "spooling" without reaching the port or
        // prompting for a file; only that job is touched, and it is aborted at the end.
        var name = StockPdfPrinterFactAttribute.PrinterName;
        var docName = "LANFax-test-" + Guid.NewGuid().ToString("N");
        Assert.True(OpenPrinter(name, out var h, IntPtr.Zero), "OpenPrinter failed: " + Marshal.GetLastWin32Error());
        try
        {
            var before = DateTime.UtcNow;
            var di = new DocInfo1 { DocName = docName, Datatype = "RAW" };
            int id = StartDocPrinter(h, 1, ref di);
            Assert.True(id > 0, "StartDocPrinter failed: " + Marshal.GetLastWin32Error());
            var jobs = PrintQueueJobs.List(name);
            Assert.NotNull(jobs);
            var mine = Assert.Single(jobs, j => j.Id == id);
            Assert.Equal(DateTimeKind.Utc, mine.SubmittedUtc.Kind);
            Assert.InRange((mine.SubmittedUtc - before).TotalSeconds, -60, 60);
            Assert.InRange((DateTime.UtcNow - mine.SubmittedUtc).TotalSeconds, -5, 60);
            Assert.Equal(docName, mine.Document);
            // Independent reference: the same job as Get-PrintJob sees it.
            var reference = RunPs("(Get-PrintJob -PrinterName '" + name + "' | Where-Object { $_.DocumentName -eq '" + docName + "' }).Id");
            Assert.Equal(id.ToString(), reference.Trim());
        }
        finally
        {
            AbortPrinter(h);
            ClosePrinter(h);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        public string DocName;
        public string? OutputFile;
        public string Datatype;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string name, out IntPtr h, IntPtr defaults);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr h, int level, ref DocInfo1 di);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool AbortPrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr h);

    private static string RunPs(string script)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } }
        return o.Result;
    }
}

/// <summary>
/// A fact that is skipped, not failed, where the stock "Microsoft Print to PDF" queue was removed (an optional Windows
/// feature). Checked in the registry, independently of the code under test, so a List bug cannot turn into a skip.
/// </summary>
public sealed class StockPdfPrinterFactAttribute : FactAttribute
{
    public const string PrinterName = "Microsoft Print to PDF";

    public StockPdfPrinterFactAttribute()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers\" + PrinterName);
        if (key == null) Skip = $"the \"{PrinterName}\" printer is not installed on this PC";
    }
}
