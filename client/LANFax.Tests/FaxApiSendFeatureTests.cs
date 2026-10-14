using System.Net;
using System.Text;
using LANFax.Core;

namespace LANFax.Tests;

/// <summary>The new send features over real HTTP against the fake server: multipart multi-document posts, the
/// /analyze blank-page check, /jobs/{id}/cancel and the job detail the confirmation shows.</summary>
public class FaxApiSendFeatureTests : IDisposable
{
    private readonly FakeServer _srv = new();
    private readonly HttpClient _http = new();

    private FaxApi Api()
    {
        var cfg = new ClientConfig { Server = "127.0.0.1", Port = _srv.Port, Token = "tok123" };
        return new FaxApi(cfg, _http);
    }

    public void Dispose() { _srv.Dispose(); _http.Dispose(); }

    private const string JobJson = """
        {"id":"lf0123456789ab","direction":"out","state":"queued","to_number":"19543216309",
         "created_at":"2026-10-14T15:00:00+00:00","updated_at":"2026-10-14T15:00:01+00:00"}
        """;

    [Fact]
    public async Task Two_documents_go_as_one_multipart_body()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, JobJson, 201);
        var ep = SendEndpoint.From(new ClientConfig { Server = "127.0.0.1", Port = _srv.Port, Token = "tok123" });
        var job = await Api().PostJobAsync(ep, new[] { (Encoding.ASCII.GetBytes("%PDF-one"), "cover.pdf"),
                                                       (Encoding.ASCII.GetBytes("%PDF-two"), "scan.pdf") },
            "5552345678", "subj", "key-1");
        Assert.Equal("lf0123456789ab", job.Id);
        var r = Assert.Single(_srv.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("/jobs", r.PathAndQuery);
        var ctype = r.Headers["Content-Type"] ?? "";
        Assert.StartsWith("multipart/form-data; boundary=", ctype);
        var body = Encoding.UTF8.GetString(r.Body);
        Assert.Contains("cover.pdf", body);
        Assert.Contains("scan.pdf", body);
        Assert.Contains("%PDF-one", body);
        Assert.Contains("%PDF-two", body);
        Assert.Contains("Content-Disposition: form-data; name=file", body);   // .NET leaves simple tokens unquoted
        Assert.Contains("application/pdf", body);
    }

    [Fact]
    public async Task One_document_stays_exact_bytes_not_multipart()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, JobJson, 201);
        var pdf = Encoding.ASCII.GetBytes("%PDF-exact-bytes");
        await Api().PostJobAsync(pdf, "5552345678", null, "key-2");
        var r = Assert.Single(_srv.Requests);
        Assert.Equal("application/pdf", r.Headers["Content-Type"]);
        Assert.Equal(pdf, r.Body);
    }

    [Fact]
    public async Task Multipart_part_with_no_name_gets_a_safe_one()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, JobJson, 201);
        var ep = SendEndpoint.From(new ClientConfig { Server = "127.0.0.1", Port = _srv.Port, Token = "t" });
        await Api().PostJobAsync(ep, new[] { (Encoding.ASCII.GetBytes("%PDF-a"), ""),
                                             (Encoding.ASCII.GetBytes("%PDF-b"), @"C:\dir\b.pdf") },
            "5552345678", null, "key-3");
        var body = Encoding.UTF8.GetString(Assert.Single(_srv.Requests).Body);
        Assert.Contains("fax.pdf", body);   // empty name falls back
        Assert.Contains("b.pdf", body);     // a path names only its file
        Assert.DoesNotContain(@"C:\dir", body);
    }

    [Fact]
    public async Task Analyze_parses_and_a_server_without_the_route_answers_null()
    {
        _srv.Handler = (ctx, r) => FakeServer.Json(ctx, "{\"pages\":3,\"blank_pages\":[1]}");
        var ep = SendEndpoint.From(new ClientConfig { Server = "127.0.0.1", Port = _srv.Port, Token = "t" });
        var check = await Api().AnalyzeJobAsync(ep, new[] { (Encoding.ASCII.GetBytes("%PDF-a"), "a.pdf") });
        Assert.NotNull(check);
        Assert.Equal(3, check!.Pages);
        Assert.Equal(new[] { 1 }, check.BlankPages);
        Assert.Equal("/analyze", Assert.Single(_srv.Requests).PathAndQuery);

        _srv.Handler = (ctx, r) => FakeServer.Json(ctx, "{\"error\":\"not found\"}", 404);
        Assert.Null(await Api().AnalyzeJobAsync(ep, new[] { (Encoding.ASCII.GetBytes("%PDF-a"), "a.pdf") }));
    }

    [Fact]
    public async Task Cancel_posts_and_parses_the_answer()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx,
            "{\"id\":\"lf0123456789ab\",\"direction\":\"out\",\"state\":\"cancelled\",\"to_number\":\"19543216309\"," +
            "\"created_at\":\"2026-10-14T15:00:00+00:00\",\"updated_at\":\"2026-10-14T15:01:00+00:00\"}");
        var j = await Api().CancelJobAsync("lf0123456789ab");
        Assert.Equal("cancelled", j.State);
        var r = Assert.Single(_srv.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("/jobs/lf0123456789ab/cancel", r.PathAndQuery);
    }

    [Fact]
    public async Task Job_detail_parses_the_event_history()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, """
            {"id":"lf0123456789ab","direction":"out","state":"sent","to_number":"19543216309","pages":3,
             "detail":"sent 3 page(s) at 14400","finished_at":"2026-10-14T15:02:00+00:00",
             "created_at":"2026-10-14T15:00:00+00:00","updated_at":"2026-10-14T15:02:00+00:00",
             "events":[{"id":5,"job_id":"lf0123456789ab","ts":"2026-10-14T15:00:01+00:00","state":"dialing","detail":null},
                        {"id":6,"job_id":"lf0123456789ab","ts":"2026-10-14T15:01:00+00:00","state":"sending","detail":null},
                        {"id":7,"job_id":"lf0123456789ab","ts":"2026-10-14T15:02:00+00:00","state":"sent","detail":null}]}
            """);
        var d = await Api().GetJobDetailAsync("lf0123456789ab");
        Assert.Equal("sent", d.Job.State);
        Assert.Equal(3, d.Job.Pages);
        Assert.Equal("sent 3 page(s) at 14400", d.Job.Detail);
        Assert.Equal(3, d.Events.Count);
        Assert.Equal("dialing", d.Events[0].State);
        Assert.Equal("sent", d.Events[2].State);
        Assert.Equal("/jobs/lf0123456789ab", Assert.Single(_srv.Requests).PathAndQuery);
    }
}
