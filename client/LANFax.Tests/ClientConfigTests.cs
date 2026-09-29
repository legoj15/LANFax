using LANFax.Core;

namespace LANFax.Tests;

public class ClientConfigTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "lanfax-" + Guid.NewGuid().ToString("N"), "config.json");

    [Fact]
    public void Defaults()
    {
        var c = new ClientConfig();
        Assert.Equal(8080, c.Port);
        Assert.Equal("LAN Fax", c.PrinterName);
        Assert.False(c.KeepLocalCopies);
        Assert.Null(c.Token);
        Assert.EndsWith(Path.Combine("LANFax", "spool"), c.SpoolDir);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), c.SpoolDir);
    }

    [Fact]
    public void LoadMissingFileGivesDefaults()
    {
        var c = ClientConfig.Load(Tmp());
        Assert.Equal(8080, c.Port);
    }

    [Fact]
    public void RoundTripProtectsToken()
    {
        var path = Tmp();
        var c = new ClientConfig { Server = "192.0.2.10", Port = 9090, Token = "s3cret-token-value", KeepLocalCopies = true, PrinterName = "P", SpoolDir = @"D:\sp" };
        c.Save(path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("s3cret-token-value", text);
        Assert.Contains("\"token\"", text);
        var d = ClientConfig.Load(path);
        Assert.Equal("192.0.2.10", d.Server);
        Assert.Equal(9090, d.Port);
        Assert.Equal("s3cret-token-value", d.Token);
        Assert.True(d.KeepLocalCopies);
        Assert.Equal("P", d.PrinterName);
        Assert.Equal(@"D:\sp", d.SpoolDir);
        Assert.Equal(new Uri("http://192.0.2.10:9090/"), d.BaseUri);
    }

    [Fact]
    public void NullTokenRoundTrips()
    {
        var path = Tmp();
        new ClientConfig { Server = "h" }.Save(path);
        Assert.Null(ClientConfig.Load(path).Token);
    }
}
