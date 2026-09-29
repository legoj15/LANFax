using LANFax.Core;

namespace LANFax.Tests;

public class NumbersTests
{
    [Theory]
    [InlineData("+1 (954) 321-6309", "19543216309")]
    [InlineData("954-321-6309", "19543216309")]
    [InlineData("1 954 321 6309", "19543216309")]
    [InlineData("000", "000")]
    [InlineData("101", "101")]
    [InlineData("1234567890", "1234567890")] // 10 digits starting with 1: no prefix added
    [InlineData("ab", "")]
    public void Normalize(string raw, string expected) => Assert.Equal(expected, Numbers.Normalize(raw));

    [Theory]
    [InlineData("19543216309")]
    [InlineData("12125551234")]
    [InlineData("000")]
    [InlineData("101")]
    [InlineData("1234")]
    [InlineData("99")]
    [InlineData("110")]
    [InlineData("932")]
    [InlineData("989")]
    [InlineData("19015551234")]
    public void Allowed(string n)
    {
        Assert.True(Numbers.IsAllowed(n, out var reason), reason);
    }

    [Theory]
    [InlineData("911")]
    [InlineData("9111")]
    [InlineData("9119")]
    [InlineData("933")]
    [InlineData("988")]
    [InlineData("19005551234")]
    [InlineData("19765551234")]
    [InlineData("11235551234")]   // area code starts with 1
    [InlineData("12120551234")]   // exchange starts with 0
    [InlineData("12121551234")]   // exchange starts with 1
    [InlineData("1")]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("123456789")]
    [InlineData("442071234567")]  // international
    [InlineData("1234567890")]    // 10 digits, leading 1
    public void Rejected(string n)
    {
        Assert.False(Numbers.IsAllowed(n, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void ReasonsAreClear()
    {
        Numbers.IsAllowed("911", out var r);
        Assert.Contains("emergency", r, StringComparison.OrdinalIgnoreCase);
        Numbers.IsAllowed("19005551234", out r);
        Assert.Contains("900", r);
    }
}
