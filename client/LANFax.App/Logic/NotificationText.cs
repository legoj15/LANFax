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
}
