using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>Live validation state of the number field (same rules as faxd, via <see cref="Numbers"/>).</summary>
public sealed record NumberState(string Normalized, bool Allowed, string Reason);

public static class NumberInput
{
    public static NumberState Evaluate(string? raw)
    {
        var n = Numbers.Normalize(raw ?? "");
        if (n.Length == 0) return new NumberState("", false, ""); // nothing typed yet: disabled, no red text
        var ok = Numbers.IsAllowed(n, out var reason);
        return new NumberState(n, ok, ok ? "" : reason);
    }
}
