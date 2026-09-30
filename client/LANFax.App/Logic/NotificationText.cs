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

    public static string PrinterMissing(string printerName) =>
        $"The \"{printerName}\" printer is not installed. Click here to install it.";

    public static string PrinterMisconfigured(string printerName, string actualPort) =>
        $"The \"{printerName}\" printer prints to {actualPort}, not to LANFax ({PrinterSetup.ExpectedPort()}). Click here to fix it.";

    /// <summary>Uses the number the server queued; says so when it differs from what the user entered.</summary>
    public static string Queued(Job job, string entered) =>
        string.IsNullOrEmpty(job.ToNumber) || job.ToNumber == entered
            ? $"Fax to {entered} queued"
            : $"Server queued this fax to {job.ToNumber} (you entered {entered})";

    /// <summary>
    /// Documents printed while LANFax was not running that the spooler gave up on (they sit in "Error"). Restarting a
    /// job in the queue sends it to LANFax again. A restarted job was once delivered twice, and duplicates cannot be
    /// told from an intentional reprint (the same document printed twice within a second is byte-identical, measured
    /// 2026-09-30), so the notice warns instead of LANFax dropping one.
    /// </summary>
    public static string StuckPrintJobs(string printerName, int count) => count == 1
        ? $"1 document printed to \"{printerName}\" before LANFax started is stuck in the printer queue with an error. Click here to open the queue and restart it. A restart can deliver it twice: send only one copy."
        : $"{count} documents printed to \"{printerName}\" before LANFax started are stuck in the printer queue with an error. Click here to open the queue and restart them. A restart can deliver a document twice: send only one copy.";

    public static string ReceiveArmed(DateTimeOffset until) =>
        $"Fax line will answer until {until.ToLocalTime():t}";

    public const string Unauthorized = "The server rejected the LANFax token. Click here to open Settings.";

    public static string ConfigProblem(string loadError) =>
        $"LANFax settings could not be read ({loadError}). Click here to open Settings.";

    /// <summary>
    /// The print pipe's <c>Error</c> (a rejected job, or the pipe held by another LANFax) as a balloon: the reason
    /// after the printer's name, cut to the balloon's 255 characters.
    /// </summary>
    public static string PrintPipeProblem(string printerName, string reason) => Fit($"\"{printerName}\" printer: {reason}");

    /// <summary>The periodic check found the print pipe listener stopped (an unexpected fault) and started it again.</summary>
    public static string ListenerRestarted(string printerName) =>
        $"Receiving from the \"{printerName}\" printer had stopped; LANFax started it again. If this repeats, restart LANFax.";

    /// <summary>The tray's printer install succeeded; a migration step the script only warned about is named too.</summary>
    public static string PrinterInstalled(string printerName, IReadOnlyList<string> warnings) => warnings.Count == 0
        ? $"The \"{printerName}\" printer is installed and prints to {PrinterSetup.ExpectedPort()}."
        : Fit($"The \"{printerName}\" printer is installed, with {(warnings.Count == 1 ? "a warning" : warnings.Count + " warnings")} (see the log): {warnings[0]}");

    /// <summary>A document left in the old file port print.pdf could not be moved (it is retried at every start).</summary>
    public static string LeftoverStuck(string path, string reason) =>
        Fit($"A document printed before LANFax was updated is still in {path} and could not be moved ({reason}). Close any program that has it open; LANFax tries again at its next start.");

    /// <summary>A document left in the old file port print.pdf was not one complete PDF.</summary>
    public static string LeftoverRejected(string movedTo) =>
        Fit($"A document printed before LANFax was updated was not one complete PDF and is not offered for sending; it was moved to {movedTo}.");

    /// <summary>Cut to the balloon's 255 characters.</summary>
    private static string Fit(string text) => text.Length <= 255 ? text : text[..252] + "...";
}
