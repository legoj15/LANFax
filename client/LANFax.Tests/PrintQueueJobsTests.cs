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
    public void AMissingPrinterGivesNull() =>
        Assert.Null(PrintQueueJobs.List("LANFax no such printer " + Guid.NewGuid().ToString("N")));

    [StockPdfPrinterFact]
    public void AnInstalledPrinterCanBeListed()
    {
        // Exercises OpenPrinter/EnumJobs and the JOB_INFO_1 layout on a queue every Windows 10/11 has.
        var jobs = PrintQueueJobs.List(StockPdfPrinterFactAttribute.PrinterName);
        Assert.NotNull(jobs);
        Assert.All(jobs, j => Assert.True(j.Id > 0 && j.SubmittedUtc.Kind == DateTimeKind.Utc));
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
