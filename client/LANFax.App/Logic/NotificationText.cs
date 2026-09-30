using LANFax.Core;

namespace LANFax.App.Logic;

public static class NotificationText
{
    /// <summary>Text for a finished outbound job, or null when it is not a terminal outbound state worth a balloon.</summary>
    public static string? ForTerminalJob(Job j)
    {
        var to = string.IsNullOrEmpty(j.ToNumber) ? "unknown number" : j.ToNumber;
        switch (j.State)
        {
            case "sent":
                var pages = j.Pages is int p and > 0 ? $" ({p} page{(p == 1 ? "" : "s")})" : "";
                return $"Fax to {to} sent{pages}";
            case "failed":
                return $"Fax to {to} failed: {(string.IsNullOrWhiteSpace(j.Error) ? "unknown error" : j.Error)}";
            default:
                return null;
        }
    }

    public static string ForFailedWithRetry(Job j) => ForTerminalJob(j with { State = "failed" }) + ". Click here to retry.";

    public static string ForReceived(Job j) =>
        $"Fax received from {(string.IsNullOrWhiteSpace(j.FromNumber) ? "unknown sender" : j.FromNumber)}";

    /// <summary>
    /// Balloons for inbound faxes found by a catch-up (oldest first): one each for up to three, else one summary so a
    /// long disconnection does not queue a minute of balloons.
    /// </summary>
    public static IReadOnlyList<string> ForCaughtUp(IReadOnlyList<Job> received) =>
        received.Count <= 3
            ? received.Select(ForReceived).ToList()
            : [$"{received.Count} faxes were received while LANFax was not connected"];

    public static string PrinterMisconfigured(string printerName, string actualPort) =>
        $"The \"{printerName}\" printer points at {actualPort}, not the LANFax spool. Click here to fix it.";

    /// <summary>Uses the number the server queued; says so when it differs from what the user entered.</summary>
    public static string Queued(Job job, string entered) =>
        string.IsNullOrEmpty(job.ToNumber) || job.ToNumber == entered
            ? $"Fax to {entered} queued"
            : $"Server queued this fax to {job.ToNumber} (you entered {entered})";

    public static string ReceiveArmed(DateTimeOffset until) =>
        $"Fax line will answer until {until.ToLocalTime():t}";

    public const string Unauthorized = "The server rejected the LANFax token. Click here to open Settings.";

    public static string ConfigProblem(string loadError) =>
        $"LANFax settings could not be read ({loadError}). Click here to open Settings.";

    /// <summary>The spooler finished a job whose file never reached the watcher: the next job overwrote it at the port.</summary>
    public static string PrintReplaced(string printerName) =>
        $"A document printed to {printerName} may have been replaced; print it again.";

    /// <summary>
    /// Kept jobs whose file may have been replaced and that cannot be printed again (another user's documents): names
    /// up to three, else counts them. <paramref name="whileRunning"/>: found by the running app (the loss check did
    /// not follow them), not at start. Balloon text is cut at 255 characters.
    /// </summary>
    public static string ForUnrecovered(string printerName, IReadOnlyList<string> documents, bool whileRunning = false)
    {
        var what = documents.Count switch
        {
            1 => $"\"{Short(documents[0])}\" was",
            <= 3 => string.Join(", ", documents.Select(d => $"\"{Short(d)}\"")) + " were",
            _ => $"{documents.Count} documents were",
        };
        var when = whileRunning ? "may have been replaced" : "while LANFax was not running and replaced";
        return whileRunning
            ? $"{what} printed to {printerName} and {when} before it could be sent; print {(documents.Count == 1 ? "it" : "them")} again."
            : $"{what} printed to {printerName} {when} before it could be sent; print {(documents.Count == 1 ? "it" : "them")} again.";
    }

    /// <summary>The printer stopped keeping printed jobs and the app cannot set that back.</summary>
    public static string KeepPrintedJobsOff(string printerName) =>
        $"The \"{printerName}\" printer no longer keeps printed documents, so a replaced document cannot be printed again. Click here to repair it (an administrator must approve).";

    /// <summary>The start-up hold lasts: a print.pdf from the last run cannot be claimed (open in another program).</summary>
    public static string HeldAtStart(string printerName, string printPath) =>
        $"Documents printed to {printerName} are waiting: {printPath} is open in another program. Close it and LANFax will pick them up.";

    private static string Short(string s) => s.Length <= 40 ? s : s[..37] + "...";

    /// <summary>
    /// The balloon for a finished job with no claimed file, or null when "print it again" cannot help: a printer that
    /// prints elsewhere or is gone, or a watcher that is not running, is the real cause and has its own balloon.
    /// </summary>
    public static string? ForLostJob(string printerName, PrinterState printer, bool watching) =>
        printer == PrinterState.Installed && watching ? PrintReplaced(printerName) : null;
}
