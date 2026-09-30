using System.Text;
using System.Text.Json;
using LANFax.Core;

namespace LANFax.Tests;

/// <summary>Server/port validation (review finding 1) and the per-user token file (finding 5e).</summary>
public class ConfigHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-cfg-" + Guid.NewGuid().ToString("N"));
    private string Cfg => Path.Combine(_dir, "shared", "config.json");
    private string Tok => Path.Combine(_dir, "user", "token.bin");

    public ConfigHardeningTests() => Directory.CreateDirectory(Path.Combine(_dir, "shared"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string json) { File.WriteAllText(Cfg, json); return Cfg; }

    // ---- validation ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(70000)]
    public void An_unusable_port_gives_the_default_and_a_load_error(int port)
    {
        var c = ClientConfig.Load(Write($"{{\"server\":\"10.0.0.5\",\"port\":{port}}}"), Tok);
        Assert.Equal(8080, c.Port);
        Assert.Equal("10.0.0.5", c.Server);
        Assert.Contains("port", c.LoadError, StringComparison.OrdinalIgnoreCase);
        Assert.Null(c.Validate());
    }

    [Theory]
    [InlineData("127.0.0.1:1")]
    [InlineData("192.0.2.10:8080")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("fax box")]
    [InlineData("host/path")]
    public void An_unusable_server_gives_the_default_and_a_load_error(string server)
    {
        var c = ClientConfig.Load(Write(JsonSerializer.Serialize(new { server, port = 9090 })), Tok);
        Assert.Equal("192.0.2.10", c.Server);
        Assert.Equal(9090, c.Port);
        Assert.Contains("server", c.LoadError, StringComparison.OrdinalIgnoreCase);
        Assert.Null(c.Validate());
    }

    [Theory]
    [InlineData("127.0.0.1:1", 8080)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    [InlineData("a b", 8080)]
    [InlineData("", 8080)]
    public void Validate_names_what_is_wrong(string server, int port)
    {
        var c = new ClientConfig { Server = server, Port = port };
        Assert.False(string.IsNullOrWhiteSpace(c.Validate()));
        var e = Assert.Throws<FaxApiConfigException>(() => SendEndpoint.From(c));
        Assert.Equal(c.Validate(), e.Message);
    }

    [Theory]
    [InlineData("192.0.2.10", 8080, "http://192.0.2.10:8080/")]
    [InlineData("faxserver.lan", 1, "http://faxserver.lan:1/")]
    [InlineData("faxserver", 65535, "http://faxserver:65535/")]
    public void A_good_server_validates_and_gives_an_endpoint(string server, int port, string uri)
    {
        var c = new ClientConfig { Server = server, Port = port, Token = "t" };
        Assert.Null(c.Validate());
        var ep = SendEndpoint.From(c);
        Assert.Equal(new Uri(uri), ep.BaseUri);
        Assert.Equal("t", ep.Token);
    }

    // ---- per-user token file ----

    [Fact]
    public void The_token_lives_in_the_user_file_not_the_shared_config()
    {
        new ClientConfig { Server = "h", Token = "s3cret-token-value" }.Save(Cfg, Tok);
        Assert.True(File.Exists(Tok));
        Assert.DoesNotContain("s3cret-token-value", Encoding.Latin1.GetString(File.ReadAllBytes(Tok)));
        using (var doc = JsonDocument.Parse(File.ReadAllText(Cfg)))
            Assert.True(!doc.RootElement.TryGetProperty("token", out var t) || t.ValueKind == JsonValueKind.Null);
        var c = ClientConfig.Load(Cfg, Tok);
        Assert.Equal("s3cret-token-value", c.Token);
        Assert.Null(c.LoadError);
    }

    [Fact]
    public void Clearing_the_token_deletes_the_user_file()
    {
        new ClientConfig { Server = "h", Token = "t" }.Save(Cfg, Tok);
        new ClientConfig { Server = "h", Token = null }.Save(Cfg, Tok);
        Assert.False(File.Exists(Tok));
        Assert.Null(ClientConfig.Load(Cfg, Tok).Token);
    }

    [Fact]
    public void The_user_token_loads_without_a_shared_config()
    {
        new ClientConfig { Token = "only-mine" }.Save(Cfg, Tok);
        File.Delete(Cfg);
        Assert.Equal("only-mine", ClientConfig.Load(Cfg, Tok).Token);
    }

    [Fact]
    public void A_machine_scope_token_is_migrated_to_the_user_file_and_removed_from_the_shared_file()
    {
        var blob = Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes("old-machine-token"), DpapiScope.LocalMachine));
        Write($"{{\"server\":\"10.0.0.5\",\"port\":9090,\"token\":\"{blob}\"}}");

        var c = ClientConfig.Load(Cfg, Tok);
        Assert.Equal("old-machine-token", c.Token);
        Assert.Null(c.LoadError);
        Assert.True(File.Exists(Tok));
        var shared = File.ReadAllText(Cfg);
        Assert.DoesNotContain(blob, shared);
        using (var doc = JsonDocument.Parse(shared))
        {
            Assert.Equal("10.0.0.5", doc.RootElement.GetProperty("server").GetString());
            Assert.Equal(9090, doc.RootElement.GetProperty("port").GetInt32());
        }
        // The next start reads it from the user file.
        var again = ClientConfig.Load(Cfg, Tok);
        Assert.Equal("old-machine-token", again.Token);
        Assert.Equal(9090, again.Port);
    }

    [Fact]
    public void An_existing_user_token_wins_over_a_shared_one_which_is_removed()
    {
        new ClientConfig { Server = "h", Token = "mine" }.Save(Cfg, Tok);
        var blob = Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes("someone-elses"), DpapiScope.LocalMachine));
        Write($"{{\"server\":\"h\",\"token\":\"{blob}\"}}");
        Assert.Equal("mine", ClientConfig.Load(Cfg, Tok).Token);
        Assert.DoesNotContain(blob, File.ReadAllText(Cfg));
    }

    [Fact]
    public void An_unreadable_user_token_is_dropped_with_a_load_error()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Tok)!);
        File.WriteAllBytes(Tok, [1, 2, 3, 4]);
        var c = ClientConfig.Load(Cfg, Tok);
        Assert.Null(c.Token);
        Assert.Contains("token", c.LoadError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void User_scope_round_trips_through_dpapi()
    {
        var plain = Encoding.UTF8.GetBytes("round-trip");
        var blob = Dpapi.Protect(plain, DpapiScope.CurrentUser);
        Assert.Equal(plain, Dpapi.Unprotect(blob));
    }
}
