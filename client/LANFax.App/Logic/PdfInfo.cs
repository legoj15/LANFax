using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LANFax.App.Logic;

/// <summary>Cheap page count for the send dialog (no PDF library): a hint for the user, never used for billing.</summary>
public static partial class PdfInfo
{
    [GeneratedRegex(@"/Count\s+(\d{1,6})")]
    private static partial Regex CountRx();

    // "/Type /Page" but not "/Type /Pages".
    [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")]
    private static partial Regex PageRx();

    /// <summary>
    /// The largest <c>/Count</c> (the root Pages node counts every page), else the number of Page objects,
    /// else null. Page objects hidden in compressed object streams are only visible through /Count.
    /// </summary>
    public static int? CountPages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        int max = 0;
        foreach (Match m in CountRx().Matches(text))
            if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max) max = n;
        if (max > 0) return max;
        var pages = PageRx().Count(text);
        return pages > 0 ? pages : null;
    }

    public static int? CountPages(string path)
    {
        try { return CountPages(File.ReadAllBytes(path)); }
        catch (Exception) { return null; }
    }

    public static string Describe(int? pages, long bytes)
    {
        var p = pages is int n ? $"{n} page{(n == 1 ? "" : "s")}" : "unknown page count";
        return $"{p}, {FormatSize(bytes)}";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_048_576 => (bytes / 1_048_576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes + " bytes",
    };
}
