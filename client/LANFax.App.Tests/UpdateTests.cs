using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LANFax.App.Logic;

namespace LANFax.App.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-upd-" + Guid.NewGuid().ToString("N"));
    public UpdateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("pretend installer bytes");
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string Release(string tag, bool draft = false, bool pre = false, string? asset = "LANFax-Setup.exe",
        string? digest = "auto", string host = "https://github.com/legoj15/LANFax/releases/download", long? size = null, string body = "Notes here")
    {
        var d = digest == "auto" ? "\"sha256:" + Sha(Payload) + "\"" : digest == null ? "null" : "\"" + digest + "\"";
        var assets = asset == null
            ? "[]"
            : "[{\"name\":\"" + asset + "\",\"size\":" + (size ?? Payload.Length) + ",\"digest\":" + d
              + ",\"browser_download_url\":\"" + host + "/" + tag + "/" + asset + "\"}]";
        return "{\"tag_name\":\"" + tag + "\",\"draft\":" + draft.ToString().ToLowerInvariant() + ",\"prerelease\":" + pre.ToString().ToLowerInvariant()
            + ",\"body\":\"" + body + "\",\"assets\":" + assets + "}";
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Seen.Add(r.RequestUri!);
            return Task.FromResult(f(r));
        }
    }

    private static UpdateChecker Checker(Func<HttpRequestMessage, HttpResponseMessage> f, out Stub stub)
    {
        stub = new Stub(f);
        return new UpdateChecker(new HttpClient(stub));
    }

    private static HttpResponseMessage Json(string s, HttpStatusCode c = HttpStatusCode.OK) =>
        new(c) { Content = new StringContent(s, Encoding.UTF8, "application/json") };

    private static readonly Version V100 = new(1, 0, 0);

    // ---- versions ----

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V1.10.0", "1.10.0")]
    [InlineData("v2.0", "2.0.0")]
    public void ParseTag_reads_plain_versions(string tag, string want) => Assert.Equal(Version.Parse(want), UpdateChecker.ParseTag(tag));

    [Theory]
    [InlineData("v1.0.0-beta1")] [InlineData("nightly")] [InlineData("")] [InlineData(null)] [InlineData("v1.2.3.4")] [InlineData("v-1.0.0")]
    public void ParseTag_refuses_anything_else(string? tag) => Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public void Current_is_the_assembly_version_without_a_commit_suffix() =>
        Assert.Equal(new Version(1, 1, 0), UpdateChecker.CurrentVersion());

    // ---- check ----

    [Fact]
    public async Task Newer_release_is_offered_with_its_asset_digest_and_notes()
    {
        var c = Checker(_ => Json(Release("v1.1.0")), out var stub);
        var u = await c.CheckAsync(V100);
        Assert.NotNull(u);
        Assert.Equal(new Version(1, 1, 0), u!.Version);
        Assert.Equal(Sha(Payload), u.Sha256);
        Assert.Equal(Payload.Length, u.Size);
        Assert.Equal("Notes here", u.Notes);
        Assert.Equal("https://api.github.com/repos/legoj15/LANFax/releases/latest", stub.Seen.Single().ToString());
    }

    [Theory]
    [InlineData("v1.0.0")] [InlineData("v0.9.0")] [InlineData("v1.0")]
    public async Task Same_or_older_release_is_not_an_update(string tag) =>
        Assert.Null(await Checker(_ => Json(Release(tag)), out _).CheckAsync(V100));

    [Fact]
    public async Task Numeric_not_text_comparison() =>
        Assert.NotNull(await Checker(_ => Json(Release("v1.10.0")), out _).CheckAsync(new Version(1, 9, 0)));

    [Fact]
    public async Task Draft_prerelease_and_unparsable_tags_are_ignored()
    {
        Assert.Null(await Checker(_ => Json(Release("v2.0.0", draft: true)), out _).CheckAsync(V100));
        Assert.Null(await Checker(_ => Json(Release("v2.0.0", pre: true)), out _).CheckAsync(V100));
        Assert.Null(await Checker(_ => Json(Release("v2.0.0-rc1")), out _).CheckAsync(V100));
    }

    [Fact]
    public async Task No_releases_yet_is_up_to_date_not_an_error() =>
        Assert.Null(await Checker(_ => Json("{}", HttpStatusCode.NotFound), out _).CheckAsync(V100));

    [Fact]
    public async Task A_release_without_the_installer_asset_is_an_error()
    {
        var e = await Assert.ThrowsAsync<UpdateException>(() => Checker(_ => Json(Release("v1.1.0", asset: "other.zip")), out _).CheckAsync(V100));
        Assert.Contains("LANFax-Setup.exe", e.Message);
    }

    [Fact]
    public async Task A_release_without_a_digest_is_refused_because_the_installer_runs_elevated()
    {
        var e = await Assert.ThrowsAsync<UpdateException>(() => Checker(_ => Json(Release("v1.1.0", digest: null)), out _).CheckAsync(V100));
        Assert.Contains("checksum", e.Message);
    }

    [Fact]
    public async Task A_download_url_off_github_is_refused() =>
        await Assert.ThrowsAsync<UpdateException>(() => Checker(_ => Json(Release("v1.1.0", host: "https://evil.example.com/x")), out _).CheckAsync(V100));

    [Fact]
    public async Task A_plain_http_download_url_is_refused() =>
        await Assert.ThrowsAsync<UpdateException>(() => Checker(_ => Json(Release("v1.1.0", host: "http://github.com/x")), out _).CheckAsync(V100));

    [Fact]
    public async Task Http_errors_and_garbage_become_UpdateException()
    {
        await Assert.ThrowsAsync<UpdateException>(() => Checker(_ => Json("{}", HttpStatusCode.Forbidden), out _).CheckAsync(V100));
        await Assert.ThrowsAsync<UpdateException>(() => Checker(_ => Json("<html>"), out _).CheckAsync(V100));
        await Assert.ThrowsAsync<UpdateException>(() => Checker(_ => throw new HttpRequestException("no route"), out _).CheckAsync(V100));
    }

    // ---- download ----

    private static async Task<UpdateInfo> Offered(string? digest = "auto", long? size = null) =>
        (await Checker(_ => Json(Release("v1.1.0", digest: digest, size: size)), out _).CheckAsync(V100))!;

    private static UpdateChecker Serving(byte[] bytes) =>
        Checker(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }, out _);

    [Fact]
    public async Task Download_saves_a_verified_file()
    {
        var info = await Offered();
        var path = await Serving(Payload).DownloadAsync(info, _dir);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.StartsWith(_dir, path);
        Assert.Contains("1.1.0", Path.GetFileName(path));
    }

    [Fact]
    public async Task Download_with_a_wrong_checksum_deletes_the_file_and_throws()
    {
        var info = await Offered(digest: "sha256:" + new string('0', 64));
        await Assert.ThrowsAsync<UpdateException>(() => Serving(Payload).DownloadAsync(info, _dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Download_with_a_wrong_size_throws()
    {
        var info = await Offered(size: 5);
        await Assert.ThrowsAsync<UpdateException>(() => Serving(Payload).DownloadAsync(info, _dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    // ---- service ----

    private sealed class Rig
    {
        public readonly List<string> Launched = [];
        public int Exits;
        public bool Sending;
        public Func<bool>? SendingProbe;
        public Action? OnExit;
        public Exception? LaunchFails;
        public UpdateService Svc = null!;
        public int Changes;
    }

    private Rig MakeService(Func<HttpRequestMessage, HttpResponseMessage> http)
    {
        var rig = new Rig();
        rig.Svc = new UpdateService(Checker(http, out _), V100, () => rig.SendingProbe?.Invoke() ?? rig.Sending,
            p => { if (rig.LaunchFails != null) throw rig.LaunchFails; rig.Launched.Add(p); },
            () => { rig.Exits++; rig.OnExit?.Invoke(); }, _dir);
        rig.Svc.Changed += () => rig.Changes++;
        return rig;
    }

    private static HttpResponseMessage Route(HttpRequestMessage r) =>
        r.RequestUri!.Host == "api.github.com" ? Json(Release("v1.1.0")) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };

    [Fact]
    public async Task Service_check_publishes_the_available_update()
    {
        var rig = MakeService(Route);
        var r = await rig.Svc.CheckAsync();
        Assert.False(r.Failed);
        Assert.Equal(new Version(1, 1, 0), rig.Svc.Available!.Version);
        Assert.Contains("1.1.0", r.Message);
        Assert.True(rig.Changes > 0);
        Assert.False(rig.Svc.Busy);
    }

    [Fact]
    public async Task Service_check_when_current_says_up_to_date()
    {
        var rig = MakeService(_ => Json(Release("v1.0.0")));
        var r = await rig.Svc.CheckAsync();
        Assert.False(r.Failed);
        Assert.Null(rig.Svc.Available);
        Assert.Contains("up to date", r.Message);
        Assert.Contains("1.0.0", r.Message);
    }

    [Fact]
    public async Task Service_check_failure_is_reported_not_thrown_and_keeps_a_known_update()
    {
        bool fail = false;
        var rig = MakeService(r => fail ? throw new HttpRequestException("offline") : Route(r));
        await rig.Svc.CheckAsync();
        fail = true;
        var r2 = await rig.Svc.CheckAsync();
        Assert.True(r2.Failed);
        Assert.StartsWith("Could not check for updates", r2.Message);
        Assert.NotNull(rig.Svc.Available);
    }

    [Fact]
    public async Task Install_downloads_launches_the_installer_then_exits()
    {
        var rig = MakeService(Route);
        await rig.Svc.CheckAsync();
        var m = await rig.Svc.InstallAsync();
        Assert.Single(rig.Launched);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(rig.Launched[0]));
        Assert.Equal(1, rig.Exits);
        Assert.Null(m);
    }

    [Fact]
    public async Task Install_waits_while_a_fax_is_being_sent()
    {
        var rig = MakeService(Route);
        await rig.Svc.CheckAsync();
        rig.Sending = true;
        var m = await rig.Svc.InstallAsync();
        Assert.Contains("fax", m, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Launched);
        Assert.Equal(0, rig.Exits);
    }

    [Fact]
    public async Task A_fax_started_while_the_installer_downloaded_stops_the_install()
    {
        var rig = MakeService(Route);
        await rig.Svc.CheckAsync();
        var calls = 0;
        rig.SendingProbe = () => ++calls > 1; // idle at the first look, sending by the second (after the download)
        var m = await rig.Svc.InstallAsync();
        Assert.Contains("fax", m, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(rig.Launched);
        Assert.Equal(0, rig.Exits);
    }

    [Fact]
    public async Task Buttons_stay_locked_once_the_installer_is_running()
    {
        var rig = MakeService(Route);
        await rig.Svc.CheckAsync();
        bool busyAtExit = false;
        rig.OnExit = () => busyAtExit = rig.Svc.Busy;
        await rig.Svc.InstallAsync();
        Assert.True(busyAtExit);
        Assert.True(rig.Svc.Busy); // the app is closing: a second click must not start a second installer
    }

    [Fact]
    public async Task Downloading_is_true_only_while_the_installer_downloads_not_during_a_check()
    {
        var rig = MakeService(Route);
        var seen = new List<(bool Busy, bool Downloading)>();
        rig.Svc.Changed += () => seen.Add((rig.Svc.Busy, rig.Svc.Downloading));
        await rig.Svc.CheckAsync();
        Assert.All(seen, s => Assert.False(s.Downloading));
        seen.Clear();
        await rig.Svc.InstallAsync();
        Assert.Contains(seen, s => s.Downloading);
    }

    [Fact]
    public async Task A_cancelled_http_call_that_the_user_did_not_cancel_is_reported_as_a_timeout()
    {
        var info = await Offered();
        var checker = new UpdateChecker(new HttpClient(new Stub(_ => throw new TaskCanceledException("timed out", new TimeoutException()))),
            checkTimeout: TimeSpan.FromSeconds(30), downloadTimeout: TimeSpan.FromSeconds(30));
        var e = await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(info, _dir));
        Assert.Contains("timed out", e.Message);
    }

    [Fact]
    public async Task Download_over_its_own_limit_throws_a_timeout_not_a_cancellation()
    {
        var info = await Offered();
        var checker = new UpdateChecker(new HttpClient(new HangingHandler()), checkTimeout: TimeSpan.FromSeconds(30), downloadTimeout: TimeSpan.FromMilliseconds(200));
        var e = await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(info, _dir));
        Assert.Contains("timed out", e.Message);
    }

    [Fact]
    public async Task Check_over_its_own_limit_throws_a_timeout()
    {
        var checker = new UpdateChecker(new HttpClient(new HangingHandler()), checkTimeout: TimeSpan.FromMilliseconds(200));
        var e = await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(V100));
        Assert.Contains("timed out", e.Message);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task Install_without_a_known_update_does_nothing()
    {
        var rig = MakeService(Route);
        Assert.NotNull(await rig.Svc.InstallAsync());
        Assert.Empty(rig.Launched);
    }

    [Fact]
    public async Task Declining_the_admin_prompt_keeps_the_app_running()
    {
        var rig = MakeService(Route);
        await rig.Svc.CheckAsync();
        rig.LaunchFails = new Win32Exception(1223);
        var m = await rig.Svc.InstallAsync();
        Assert.Contains("cancel", m, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rig.Exits);
        Assert.False(rig.Svc.Busy);
    }

    [Fact]
    public async Task Download_failure_keeps_the_app_running()
    {
        var rig = MakeService(r => r.RequestUri!.Host == "api.github.com" ? Json(Release("v1.1.0")) : new HttpResponseMessage(HttpStatusCode.NotFound));
        await rig.Svc.CheckAsync();
        var m = await rig.Svc.InstallAsync();
        Assert.StartsWith("Could not download", m);
        Assert.Empty(rig.Launched);
        Assert.Equal(0, rig.Exits);
    }

    [Fact]
    public async Task Dismissed_update_stays_available_but_is_marked_dismissed()
    {
        var rig = MakeService(Route);
        await rig.Svc.CheckAsync();
        Assert.False(rig.Svc.Dismissed);
        rig.Svc.Dismiss();
        Assert.True(rig.Svc.Dismissed);
        Assert.NotNull(rig.Svc.Available);
    }

    // ---- release plumbing ----

    [Fact]
    public void Project_and_installer_agree_on_the_version()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
        var props = File.ReadAllText(Path.Combine(d!.FullName, "Directory.Build.props"));
        var v = System.Text.RegularExpressions.Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
        var iss = File.ReadAllText(Path.Combine(d.FullName, "install", "LANFax.iss"));
        Assert.Contains("#define AppVersion \"" + v + "\"", iss);
        Assert.Equal(UpdateChecker.CurrentVersion(), Version.Parse(v));
    }
}
