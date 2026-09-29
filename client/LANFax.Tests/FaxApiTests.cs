using System.Net;
using System.Text;
using LANFax.Core;

namespace LANFax.Tests;

public class FaxApiTests : IDisposable
{
    private readonly FakeServer _srv = new();
    private readonly HttpClient _http = new();
    private readonly List<TimeSpan> _delays = new();

    private FaxApi Api()
    {
        var cfg = new ClientConfig { Server = "127.0.0.1", Port = _srv.Port, Token = "tok123" };
        return new FaxApi(cfg, _http) { Delay = (d, _) => { _delays.Add(d); return Task.CompletedTask; } };
    }

    public void Dispose() { _srv.Dispose(); _http.Dispose(); }

    private const string JobJson = """
        {"id":"lf0123456789ab","direction":"out","state":"queued","filename":"19543216309_fax-lf0123456789ab.pdf",
         "to_number":"19543216309","from_number":null,"subject":"hi","origin":"api","client":"1.2.3.4","ast_jobid":null,
         "pages":null,"error":null,"pdf_path":"/x.pdf","created_at":"2026-09-29T15:00:00+00:00",
         "updated_at":"2026-09-29T15:00:01+00:00","finished_at":null,"future_field":1}
        """;

    [Fact]
    public async Task StatusParses()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, """
            {"daemon":{"version":"0.1.1","started_at":"x","ami_connected":true},
             "queue":{"queued":1,"converting":0,"dialing":2,"sending":0},
             "registrations":[{"name":"reg-in","status":"Registered"},{"name":"reg-out","status":"Rejected"}],
             "last_sent":null,"last_received":null,
             "arm":{"armed":true,"until":"2026-09-29T16:35:00+00:00","remaining_min":60,"ext":"300","changed":null,"raw":""}}
            """);
        var s = await Api().GetStatusAsync();
        Assert.True(s.AmiConnected);
        Assert.Equal(2, s.Registrations.Count);
        Assert.Equal(("reg-out", "Rejected"), s.Registrations[1]);
        Assert.Equal(2, s.Queue["dialing"]);
        Assert.True(s.Arm.Armed);
        Assert.Equal(60, s.Arm.RemainingMin);
        Assert.Equal("300", s.Arm.Ext);
        Assert.NotNull(s.Arm.Until);
        Assert.False(s.Receiving);
        var r = Assert.Single(_srv.Requests);
        Assert.Equal("/status", r.PathAndQuery);
        Assert.Equal("Bearer tok123", r.Headers["Authorization"]);
    }

    [Fact]
    public async Task JobsParseAndQueryIsBuilt()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, "{\"jobs\":[" + JobJson + "]}");
        var since = new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);
        var jobs = await Api().GetJobsAsync("out", new[] { "sent", "failed" }, 50, since);
        var j = Assert.Single(jobs);
        Assert.Equal("lf0123456789ab", j.Id);
        Assert.Equal("out", j.Direction);
        Assert.Equal("19543216309", j.ToNumber);
        Assert.Null(j.Pages);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero), j.CreatedAt);
        Assert.Null(j.FinishedAt);
        var q = _srv.Requests[0].PathAndQuery;
        Assert.StartsWith("/jobs?", q);
        Assert.Contains("direction=out", q);
        Assert.Contains("state=sent,failed", q.Replace("%2C", ","));
        Assert.Contains("limit=50", q);
        Assert.Contains("updated_since=", q);
    }

    [Fact]
    public async Task InboxAndSingleJob()
    {
        _srv.Handler = (ctx, r) => r.PathAndQuery.StartsWith("/inbox")
            ? FakeServer.Json(ctx, "{\"jobs\":[" + JobJson + "]}")
            : FakeServer.Json(ctx, JobJson.TrimEnd().TrimEnd('}') + ",\"events\":[]}");
        var api = Api();
        Assert.Single(await api.GetInboxAsync(7));
        Assert.Equal("/inbox?limit=7", _srv.Requests[0].PathAndQuery);
        var j = await api.GetJobAsync("lf0123456789ab");
        Assert.Equal("queued", j.State);
        Assert.Equal("/jobs/lf0123456789ab", _srv.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task PostJobSendsHeadersAndBody201()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, JobJson, 201);
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 fake");
        var j = await Api().PostJobAsync(new MemoryStream(pdf), "954-321-6309", "Hello there", "key-1", CancellationToken.None);
        Assert.Equal("lf0123456789ab", j.Id);
        var r = Assert.Single(_srv.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("/jobs", r.PathAndQuery);
        Assert.Equal("application/pdf", r.Headers["Content-Type"]);
        Assert.Equal("954-321-6309", r.Headers["X-Fax-To"]);
        Assert.Equal("Hello there", r.Headers["X-Fax-Subject"]);
        Assert.Equal("key-1", r.Headers["Idempotency-Key"]);
        Assert.Equal("Bearer tok123", r.Headers["Authorization"]);
        Assert.Equal(pdf, r.Body);
    }

    [Fact]
    public async Task PostJob200IsSuccessToo()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, JobJson, 200);
        var j = await Api().PostJobAsync(new MemoryStream(new byte[] { 1 }), "1", null, "k", CancellationToken.None);
        Assert.Equal("lf0123456789ab", j.Id);
        Assert.Null(_srv.Requests[0].Headers["X-Fax-Subject"]);
    }

    [Fact]
    public async Task PostJobRetriesOn503WithSameKey()
    {
        int n = 0;
        _srv.Handler = (ctx, _) => Interlocked.Increment(ref n) < 3
            ? FakeServer.Json(ctx, "{\"error\":\"busy\"}", 503)
            : FakeServer.Json(ctx, JobJson, 201);
        var j = await Api().PostJobAsync(new MemoryStream(Encoding.ASCII.GetBytes("%PDF")), "19543216309", "s", "same-key", CancellationToken.None);
        Assert.Equal("lf0123456789ab", j.Id);
        var reqs = _srv.Requests;
        Assert.Equal(3, reqs.Length);
        Assert.All(reqs, r => Assert.Equal("same-key", r.Headers["Idempotency-Key"]));
        Assert.All(reqs, r => Assert.Equal("%PDF", Encoding.ASCII.GetString(r.Body)));
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, _delays);
    }

    [Fact]
    public async Task PostJobGivesUpAfterThreeAttempts()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, "{\"error\":\"bad gateway\"}", 502);
        var ex = await Assert.ThrowsAsync<FaxApiException>(() =>
            Api().PostJobAsync(new MemoryStream(new byte[] { 1 }), "1", null, "k", CancellationToken.None));
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(3, _srv.Requests.Length);
    }

    [Theory]
    [InlineData(400, "number 12345 not allowed")]
    [InlineData(413, "body too large")]
    public async Task PostJobDoesNotRetryClientErrors(int status, string msg)
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, "{\"error\":\"" + msg + "\"}", status);
        var ex = await Assert.ThrowsAsync<FaxApiException>(() =>
            Api().PostJobAsync(new MemoryStream(new byte[] { 1 }), "12345", null, "k", CancellationToken.None));
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(msg, ex.Message);
        Assert.Single(_srv.Requests);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task PostJobRetriesOnConnectionFailure()
    {
        int n = 0;
        _srv.Handler = (ctx, _) =>
        {
            if (Interlocked.Increment(ref n) == 1) { ctx.Response.Abort(); return Task.CompletedTask; }
            return FakeServer.Json(ctx, JobJson, 201);
        };
        var j = await Api().PostJobAsync(new MemoryStream(new byte[] { 1 }), "1", null, "k", CancellationToken.None);
        Assert.Equal("lf0123456789ab", j.Id);
        Assert.Equal(2, _srv.Requests.Length);
    }

    [Fact]
    public async Task Unauthorized()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, "{\"error\":\"unauthorized\"}", 401);
        await Assert.ThrowsAsync<FaxApiUnauthorizedException>(() => Api().GetStatusAsync());
        await Assert.ThrowsAsync<FaxApiUnauthorizedException>(() =>
            Api().PostJobAsync(new MemoryStream(new byte[] { 1 }), "1", null, "k", CancellationToken.None));
    }

    [Fact]
    public async Task DownloadPdfWritesBytes()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\n...%%EOF");
        _srv.Handler = async (ctx, _) =>
        {
            ctx.Response.ContentType = "application/pdf";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        };
        var dest = Path.Combine(Path.GetTempPath(), "lanfax-" + Guid.NewGuid().ToString("N"), "sub", "x.pdf");
        await Api().DownloadPdfAsync("lfabc", dest);
        Assert.Equal(bytes, File.ReadAllBytes(dest));
        Assert.Equal("/jobs/lfabc/pdf", _srv.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task TestSendArmDisarmReceiveTest()
    {
        _srv.Handler = (ctx, r) => r.PathAndQuery.StartsWith("/test/send")
            ? FakeServer.Json(ctx, JobJson, 201)
            : FakeServer.Json(ctx, """{"armed":true,"until":null,"remaining_min":20,"ext":"300"}""");
        var api = Api();
        var j = await api.SendTestPageAsync("19543216309", "note", "tk");
        Assert.Equal("lf0123456789ab", j.Id);
        var r0 = _srv.Requests[0];
        Assert.Equal("/test/send", r0.PathAndQuery);
        Assert.Equal("tk", r0.Headers["Idempotency-Key"]);
        Assert.Equal("""{"to":"19543216309","note":"note"}""", Encoding.UTF8.GetString(r0.Body));

        var a = await api.ArmAsync(60);
        Assert.Equal("/arm", _srv.Requests[1].PathAndQuery);
        Assert.Equal("""{"minutes":60}""", Encoding.UTF8.GetString(_srv.Requests[1].Body));
        Assert.True(a.Armed);

        await api.DisarmAsync();
        Assert.Equal("/disarm", _srv.Requests[2].PathAndQuery);
        Assert.Equal("POST", _srv.Requests[2].Method);

        await api.ReceiveTestAsync();
        Assert.Equal("/test/receive", _srv.Requests[3].PathAndQuery);
        Assert.Equal("""{"minutes":20}""", Encoding.UTF8.GetString(_srv.Requests[3].Body));
    }

    [Fact]
    public void ServerStatusPartialMergeSemantics()
    {
        var full = ServerStatus.FromJson(System.Text.Json.JsonDocument.Parse(
            """{"daemon":{"ami_connected":true},"queue":{"queued":3},"registrations":[{"name":"a","status":"ok"}],"arm":{"armed":false,"remaining_min":0,"ext":"300"}}""").RootElement);
        var merged = full.Merge(System.Text.Json.JsonDocument.Parse("""{"receiving":true,"from":"555"}""").RootElement);
        Assert.True(merged.Receiving);
        Assert.True(merged.AmiConnected);
        Assert.Equal(3, merged.Queue["queued"]);
        Assert.Single(merged.Registrations);
    }
}
