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

    public static string ForReceived(Job j) =>
        $"Fax received from {(string.IsNullOrWhiteSpace(j.FromNumber) ? "unknown sender" : j.FromNumber)}";

    public static string Queued(string number) => $"Fax to {number} queued";

    public static string ReceiveArmed(DateTimeOffset until) =>
        $"Fax line will answer until {until.ToLocalTime():t}";
}
