using System.Text.RegularExpressions;

namespace LANFax.Core;

/// <summary>Mirrors faxd enqueue.py normalize_number / check_number (default rules nanp,000,ext).</summary>
public static class Numbers
{
    private static readonly Regex Nanp10 = new("^[2-9][0-9]{9}$", RegexOptions.Compiled);
    private static readonly Regex Nanp11 = new("^1[2-9][0-9]{2}[2-9][0-9]{6}$", RegexOptions.Compiled);

    public static string Normalize(string raw)
    {
        var digits = new string((raw ?? "").Where(c => c is >= '0' and <= '9').ToArray());
        return Nanp10.IsMatch(digits) ? "1" + digits : digits;
    }

    public static bool IsAllowed(string normalized, out string reason)
    {
        reason = "";
        var n = normalized ?? "";
        if (n.Length < 2) { reason = "number must have at least 2 digits"; return false; }
        if (n == "000") return true;
        if (n.Length <= 4)
        {
            if (n.StartsWith("911", StringComparison.Ordinal) || n is "933" or "988")
            {
                reason = $"number {n} not allowed: emergency code";
                return false;
            }
            return true;
        }
        if (Nanp11.IsMatch(n))
        {
            var area = n.Substring(1, 3);
            if (area is "900" or "976")
            {
                reason = $"number {n} not allowed: premium-rate area code {area}";
                return false;
            }
            return true;
        }
        reason = $"number {n} not allowed: only 10/11-digit NANP numbers, 2-4 digit extensions and 000 are accepted";
        return false;
    }
}
