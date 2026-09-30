using LANFax.Core;

namespace LANFax.Tests;

/// <summary>
/// The tray's own rescue of a document left in the old file port spool\print.pdf (installs before 2026-09-30): the
/// same sorting as the install script's, run at every tray start and after an install, so a rescue the script could
/// not do (file locked) is retried instead of orphaned.
/// </summary>
public class OldPortLeftoverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-leftover-" + Guid.NewGuid().ToString("N"));
    private string Leftover => Path.Combine(_dir, "print.pdf");
    private string JobsDir => Path.Combine(_dir, "jobs");
    private string RejectedDir => Path.Combine(_dir, "rejected");

    public OldPortLeftoverTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Pdf => "%PDF-1.7\nbody\n%%EOF\n"u8.ToArray();

    [Fact]
    public void NothingLeftIsNothingToDo()
    {
        Assert.Equal(new LeftoverResult(LeftoverOutcome.None, null), OldPortLeftover.Rescue(_dir));
        File.WriteAllBytes(Leftover, []); // the empty file the spooler leaves: nothing to offer, left alone
        Assert.Equal(LeftoverOutcome.None, OldPortLeftover.Rescue(_dir).Outcome);
        Assert.True(File.Exists(Leftover));
    }

    [Fact]
    public void OneCompletePdfMovesToJobsUnderANewId()
    {
        File.WriteAllBytes(Leftover, Pdf);
        var r = OldPortLeftover.Rescue(_dir);
        Assert.Equal(LeftoverOutcome.Offered, r.Outcome);
        Assert.False(File.Exists(Leftover));
        Assert.Equal(JobsDir, Path.GetDirectoryName(r.MovedTo));
        Assert.True(Guid.TryParse(Path.GetFileNameWithoutExtension(r.MovedTo), out _));
        Assert.EndsWith(".pdf", r.MovedTo);
        Assert.Equal(Pdf, File.ReadAllBytes(r.MovedTo!));
    }

    [Fact]
    public void AnythingElseMovesToRejected()
    {
        byte[] two = [.. Pdf, .. Pdf];
        File.WriteAllBytes(Leftover, two);
        var r = OldPortLeftover.Rescue(_dir);
        Assert.Equal(LeftoverOutcome.Rejected, r.Outcome);
        Assert.Equal(RejectedDir, Path.GetDirectoryName(r.MovedTo));
        Assert.StartsWith("print-", Path.GetFileName(r.MovedTo));
        Assert.Equal(two, File.ReadAllBytes(r.MovedTo!));
        Assert.False(Directory.Exists(JobsDir) && Directory.GetFiles(JobsDir).Length > 0);
    }

    [Fact]
    public void ALockedLeftoverThrowsAndStaysForTheNextTry()
    {
        File.WriteAllBytes(Leftover, Pdf);
        using (new FileStream(Leftover, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => OldPortLeftover.Rescue(_dir));
        Assert.True(File.Exists(Leftover));
        Assert.Equal(LeftoverOutcome.Offered, OldPortLeftover.Rescue(_dir).Outcome);
    }
}
