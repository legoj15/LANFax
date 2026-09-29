using System.Text.Json;
using LANFax.Core;

namespace LANFax.Tests;

public class NumbersTests
{
    /// <summary>
    /// Rows of numbers-vectors.json. The same file is server/tests/numbers-vectors.json, run against
    /// faxd.enqueue.normalize_number/check_number by server/tests/test_numbers_vectors.py.
    /// </summary>
    public static IEnumerable<object[]> Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "numbers-vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
            yield return new object[]
            {
                v.GetProperty("input").GetString()!, v.GetProperty("normalized").GetString()!,
                v.GetProperty("allowed").GetBoolean(), v.GetProperty("reason").GetString()!,
            };
    }

    [Fact]
    public void VectorFileIsNotEmpty() => Assert.True(Vectors().Count() >= 20);

    [Theory]
    [MemberData(nameof(Vectors))]
    public void SharedVector(string input, string normalized, bool allowed, string reason)
    {
        var n = Numbers.Normalize(input);
        Assert.Equal(normalized, n);
        Assert.Equal(allowed, Numbers.IsAllowed(n, out var why));
        if (allowed) Assert.Equal("", why);
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(why));
            Assert.Contains(reason, why);
        }
    }
}
