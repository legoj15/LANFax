namespace LANFax.Core;

public enum LeftoverOutcome
{
    /// <summary>No print.pdf, or an empty one: nothing to hand over.</summary>
    None,
    /// <summary>One complete PDF: moved to spool\jobs\&lt;guid&gt;.pdf, which the tray offers as unsent.</summary>
    Offered,
    /// <summary>Anything else (two jobs written over each other, a cut-off one): moved to spool\rejected\, never offered.</summary>
    Rejected,
}

public sealed record LeftoverResult(LeftoverOutcome Outcome, string? MovedTo);

/// <summary>
/// A document printed to the old file port <c>spool\print.pdf</c> (installs before 2026-09-30) while the tray was not
/// running. The install script sorts it during the migration; this is the same sorting (the listener's acceptance
/// rule), which the tray runs at every start and after an install, so a leftover the script could not move (locked
/// by a viewer or a scanner, which the script only reports as a warning) is retried rather than orphaned.
/// </summary>
public static class OldPortLeftover
{
    public static string PathIn(string spoolDir) => Path.Combine(spoolDir, "print.pdf");

    /// <summary>
    /// Sorts the leftover into jobs\ or rejected\. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when it cannot be read or moved; it then stays for the next try.
    /// Call only once the printer no longer writes there (its port is not this file).
    /// </summary>
    public static LeftoverResult Rescue(string spoolDir, long maxBytes = PrintPipeListener.DefaultMaxJobBytes)
    {
        var path = PathIn(spoolDir);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) return new LeftoverResult(LeftoverOutcome.None, null);
        if (PrintPipeListener.IsOneCompletePdf(path, maxBytes))
        {
            var jobs = Path.Combine(spoolDir, "jobs");
            Directory.CreateDirectory(jobs);
            var dest = Path.Combine(jobs, Guid.NewGuid() + ".pdf");
            File.Move(path, dest);
            return new LeftoverResult(LeftoverOutcome.Offered, dest);
        }
        var rejected = Path.Combine(spoolDir, "rejected");
        Directory.CreateDirectory(rejected);
        var to = Path.Combine(rejected, $"print-{Guid.NewGuid()}.pdf");
        File.Move(path, to);
        return new LeftoverResult(LeftoverOutcome.Rejected, to);
    }
}
