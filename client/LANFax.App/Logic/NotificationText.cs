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

    /// <summary>Uses the number the server queued; says so when it differs from what the user entered.</summary>
    public static string Queued(Job job, string entered) =>
        string.IsNullOrEmpty(job.ToNumber) || job.ToNumber == entered
            ? $"Fax to {entered} queued"
            : $"Server queued this fax to {job.ToNumber} (you entered {entered})";

    public static string ReceiveArmed(DateTimeOffset until) =>
        $"Fax line will answer until {until.ToLocalTime():t}";

    public static string Orphans(int n) => n == 1
        ? "1 unsent document found. Click here to send or discard it."
        : $"{n} unsent documents found. Click here to send or discard them.";

    public const string Unauthorized = "The server rejected the LANFax token. Click here to open Settings.";

    public static string ConfigProblem(string loadError) =>
        $"LANFax settings could not be read ({loadError}). Click here to open Settings.";
}
