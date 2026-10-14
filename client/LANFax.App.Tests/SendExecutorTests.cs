using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

public sealed class SendExecutorTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-se-" + Guid.NewGuid().ToString("N"));
    string Jobs => Path.Combine(_dir, "jobs");
    readonly ClientConfig Cfg = new() { Server = "127.0.0.1", Port = 8080, Token = "tok-a" };

    public SendExecutorTests() => Directory.CreateDirectory(Jobs);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static Job J(string id) => new(id, "out", "queued", null, "15552345678", null, null, null, null, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    /// <summary>Records what was on disk at the moment each request arrived.</summary>
    sealed class FakeApi(Func<Job>? answer = null) : ISendApi
    {
        public readonly List<string> Calls = new();
        public Func<string?>? OnCall;
        public readonly List<string?> DiskAtCall = new();
        public byte[]? Body;
        public readonly List<SendEndpoint> Endpoints = new();
        public List<IReadOnlyList<(byte[] Bytes, string Name)>>? Parts;
        public FaxCheck? AnalyzeAnswer { get; set; }
        public readonly List<IReadOnlyList<(byte[] Bytes, string Name)>> Analyzed = new();
        public Func<Exception?>? OnAnalyze { get; set; }

        public Task<Job> PostJobAsync(SendEndpoint endpoint, byte[] pdf, string to, string? subject, string idempotencyKey, CancellationToken ct = default)
        {
            Calls.Add($"post {to} {subject} {idempotencyKey}");
            Endpoints.Add(endpoint);
            DiskAtCall.Add(OnCall?.Invoke());
            Body = pdf;
            return Task.FromResult((answer ?? (() => J("lf1")))());
        }

        public Task<Job> PostJobAsync(SendEndpoint endpoint, IReadOnlyList<(byte[] Bytes, string Name)> parts, string to, string? subject, string idempotencyKey, CancellationToken ct = default)
        {
            Calls.Add($"post {parts.Count} doc(s) {to} {subject} {idempotencyKey}");
            Endpoints.Add(endpoint);
            DiskAtCall.Add(OnCall?.Invoke());
            (Parts ??= new()).Add(parts);
            Body = parts.Count == 1 ? parts[0].Bytes : parts.SelectMany(p => p.Bytes).ToArray();
            return Task.FromResult((answer ?? (() => J("lf1")))());
        }

        public Task<FaxCheck?> AnalyzeJobAsync(SendEndpoint endpoint, IReadOnlyList<(byte[] Bytes, string Name)> parts, CancellationToken ct = default)
        {
            if (OnAnalyze is { } raise && raise() is { } e) return Task.FromException<FaxCheck?>(e);
            Analyzed.Add(parts);
            return Task.FromResult(AnalyzeAnswer);
        }

        public Task<Job> SendTestPageAsync(SendEndpoint endpoint, string to, string? note, string idempotencyKey, CancellationToken ct = default)
        {
            Calls.Add($"test {to} {note} {idempotencyKey}");
            Endpoints.Add(endpoint);
            DiskAtCall.Add(OnCall?.Invoke());
            return Task.FromResult((answer ?? (() => J("lf2")))());
        }
    }

    (Guid id, string pdf) Spooled()
    {
        var id = Guid.NewGuid();
        var pdf = Path.Combine(Jobs, id + ".pdf");
        File.WriteAllText(pdf, "%PDF-1.4 body\n%%EOF\n");
        return (id, pdf);
    }

    string? RecordAt(Guid id)
    {
        var path = Path.Combine(Jobs, id + ".json");
        if (!File.Exists(path)) return null;
        var r = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return r.GetProperty("state").GetString() + " " + r.GetProperty("idempotencyKey").GetString();
    }

    [Fact]
    public async Task The_sending_record_is_on_disk_when_the_request_goes_out()
    {
        var (id, pdf) = Spooled();
        var api = new FakeApi();
        api.OnCall = () => RecordAt(id);
        var session = new SendSession(id, pdf);
        var plan = session.Build("555 234 5678", "hello", false, false);
        var r = await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, plan);

        Assert.Equal(SendAttemptStatus.Accepted, r.Status);
        Assert.Equal("lf1", r.Job!.Id);
        Assert.Equal(new[] { $"post 15552345678 hello {plan.IdempotencyKey}" }, api.Calls);
        Assert.Equal(new[] { "sending " + plan.IdempotencyKey }, api.DiskAtCall);
        Assert.Equal(File.ReadAllBytes(pdf), api.Body);
        Assert.Same(plan, session.AcceptedPlan);
        Assert.Equal(1, session.Attempts);
    }

    [Fact]
    public async Task Test_page_instead_of_the_document_is_also_recorded_first()
    {
        var (id, pdf) = Spooled();
        var api = new FakeApi();
        api.OnCall = () => RecordAt(id);
        var session = new SendSession(id, pdf);
        var plan = session.Build("5552345678", null, useTestPage: true, false);
        var r = await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, plan);
        Assert.Equal(SendAttemptStatus.Accepted, r.Status);
        Assert.StartsWith("test 15552345678", api.Calls.Single());
        Assert.Equal(new[] { "sending " + plan.IdempotencyKey }, api.DiskAtCall);
    }

    [Fact]
    public async Task A_failing_record_write_means_the_api_is_never_called()
    {
        var (id, pdf) = Spooled();
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "a file where the spool dir should be");
        var api = new FakeApi();
        var session = new SendSession(id, pdf);
        var plan = session.Build("5552345678", null, false, false);
        var r = await new SendExecutor(api, Cfg, new JobTracker(blocked)).RunAsync(session, plan);

        Assert.Equal(SendAttemptStatus.NotSent, r.Status);
        Assert.StartsWith("Nothing was sent: the send could not be recorded on disk", r.Message);
        Assert.Empty(api.Calls);
        Assert.Equal(0, session.Attempts);
        Assert.False(session.InFlight);
        Assert.False(session.FieldsLocked);
    }

    [Fact]
    public async Task An_unreadable_document_is_not_sent_and_not_recorded()
    {
        var (id, pdf) = Spooled();
        File.Delete(pdf);
        var api = new FakeApi();
        var session = new SendSession(id, pdf);
        var r = await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal(SendAttemptStatus.NotSent, r.Status);
        Assert.StartsWith("Cannot read the document", r.Message);
        Assert.Empty(api.Calls);
        Assert.Null(RecordAt(id));
    }

    [Theory]
    [InlineData(0, AttemptOutcome.Uncertain, "uncertain", true)]
    [InlineData(413, AttemptOutcome.Rejected, "draft ", false)]
    public async Task A_failed_request_is_classified_and_recorded(int status, AttemptOutcome outcome, string record, bool locked)
    {
        var (id, pdf) = Spooled();
        var api = new FakeApi(() => throw new FaxApiException(status, "boom"));
        var session = new SendSession(id, pdf);
        var plan = session.Build("5552345678", null, false, false);
        var r = await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, plan);
        Assert.Equal(SendAttemptStatus.Failed, r.Status);
        Assert.Equal(outcome, r.Outcome);
        Assert.Equal("boom", r.Message);
        Assert.IsType<FaxApiException>(r.Failure);
        Assert.StartsWith(record, RecordAt(id));
        Assert.Equal(locked, session.FieldsLocked);
        Assert.False(session.InFlight);
    }

    [Fact]
    public async Task The_trays_test_page_has_no_journal()
    {
        var api = new FakeApi();
        var session = new SendSession(Guid.NewGuid(), null);
        var r = await new SendExecutor(api, Cfg, null).RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal(SendAttemptStatus.Accepted, r.Status);
        Assert.Single(api.Calls);
        Assert.Empty(Directory.GetFiles(Jobs));
    }

    // ---- review finding 1: a server/port that cannot form a URI never reaches Started ----

    [Theory]
    [InlineData("127.0.0.1:1", 8080)]
    [InlineData("127.0.0.1", 0)]
    public async Task An_unusable_server_or_port_is_not_sent_and_not_recorded(string server, int port)
    {
        var (id, pdf) = Spooled();
        var handler = new StubHandler(_ => throw new InvalidOperationException("no request may be made"));
        using var http = new HttpClient(handler);
        var cfg = new ClientConfig { Server = server, Port = port, Token = "t" };
        var session = new SendSession(id, pdf);
        var r = await new SendExecutor(new FaxApi(cfg, http), cfg, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, false, false));

        Assert.Equal(SendAttemptStatus.NotSent, r.Status);
        Assert.Contains("Settings", r.Message);
        Assert.Empty(handler.Requests);
        Assert.Null(RecordAt(id));
        Assert.Equal(0, session.Attempts);
        Assert.False(session.FieldsLocked);
    }

    [Theory]
    [InlineData("tok en")]
    [InlineData("t\u00f6ken")]
    [InlineData("tok\"en")]
    public async Task A_token_that_cannot_be_a_bearer_parameter_is_not_sent_never_uncertain(string token)
    {
        // Real FaxApi: the endpoint snapshot refuses the token before Started, so nothing goes out and nothing locks.
        var (id, pdf) = Spooled();
        var cfg = new ClientConfig { Server = "127.0.0.1", Port = 8081, Token = token };
        var handler = new StubHandler(_ => Json201());
        using var http = new HttpClient(handler);
        var session = new SendSession(id, pdf);
        var key = session.IdempotencyKey;
        var r = await new SendExecutor(new FaxApi(cfg, http), cfg, new JobTracker(_dir))
            .RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal(SendAttemptStatus.NotSent, r.Status);
        Assert.Contains(ClientConfig.TokenRule, r.Message);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, session.Attempts);
        Assert.False(session.FieldsLocked);
        Assert.Equal(key, session.IdempotencyKey);
        Assert.Null(RecordAt(id));
    }

    // ---- review finding 2: the endpoint is frozen when the send starts ----

    [Fact]
    public async Task Settings_saved_between_attempts_do_not_move_the_retry()
    {
        var (id, pdf) = Spooled();
        var cfg = new ClientConfig { Server = "127.0.0.1", Port = 8081, Token = "tok-a" };
        int n = 0;
        var handler = new StubHandler(_ => ++n == 1
            ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            : Json201());
        using var http = new HttpClient(handler);
        var api = new FaxApi(cfg, http)
        {
            // The user saves Settings during the backoff: another server, another token.
            Delay = (_, _) => { cfg.Server = "10.9.9.9"; cfg.Port = 9999; cfg.Token = "tok-b"; return Task.CompletedTask; },
        };
        var session = new SendSession(id, pdf);
        var r = await new SendExecutor(api, cfg, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, false, false));

        Assert.Equal(SendAttemptStatus.Accepted, r.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, q =>
        {
            Assert.Equal("http://127.0.0.1:8081/jobs", q.Uri);
            Assert.Equal("Bearer tok-a", q.Auth);
        });
    }

    [Fact]
    public async Task A_locked_send_is_not_resent_to_a_different_server()
    {
        var (id, pdf) = Spooled();
        var cfg = new ClientConfig { Server = "127.0.0.1", Port = 8081, Token = "tok-a" };
        var fail = true;
        var api = new FakeApi(() => fail ? throw new FaxApiException(0, "timeout") : J("lf9"));
        var session = new SendSession(id, pdf);
        var exec = new SendExecutor(api, cfg, new JobTracker(_dir));
        var first = await exec.RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal(AttemptOutcome.Uncertain, first.Outcome);

        // Settings now point at another fax server: the same key there would dial a second time.
        cfg.Server = "10.9.9.9";
        fail = false;
        var second = await exec.RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal(SendAttemptStatus.NotSent, second.Status);
        Assert.Contains("127.0.0.1", second.Message);
        Assert.Single(api.Calls);
        Assert.StartsWith("uncertain", RecordAt(id));

        // Back on the original server (a new token is fine: same server, same key) it goes out.
        cfg.Server = "127.0.0.1";
        cfg.Token = "tok-new";
        var third = await exec.RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal(SendAttemptStatus.Accepted, third.Status);
        Assert.Equal("tok-new", api.Endpoints[^1].Token);
    }

    // ---- the server pin survives a restart ----

    [Fact]
    public async Task A_resumed_send_is_not_sent_to_a_server_other_than_the_recorded_one()
    {
        var (id, pdf) = Spooled();
        var cfg = new ClientConfig { Server = "127.0.0.1", Port = 8081, Token = "tok-a" };
        var first = new FakeApi(() => throw new FaxApiException(0, "timeout"));
        var s1 = new SendSession(id, pdf);
        Assert.Equal(AttemptOutcome.Uncertain,
            (await new SendExecutor(first, cfg, new JobTracker(_dir)).RunAsync(s1, s1.Build("5552345678", null, false, false))).Outcome);

        // Restart with Settings now pointing at another fax server.
        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.Resume, req.Kind);
        Assert.Equal("127.0.0.1:8081", req.Server);
        var s2 = SendSession.For(req);
        var moved = new ClientConfig { Server = "10.9.9.9", Port = 8081, Token = "tok-a" };
        var api = new FakeApi(() => J("lf9"));
        var exec = new SendExecutor(api, moved, new JobTracker(_dir));
        var r = await exec.RunAsync(s2, s2.Build("", null, false, false));
        Assert.Equal(SendAttemptStatus.NotSent, r.Status);
        Assert.Contains("127.0.0.1:8081", r.Message);
        Assert.Contains("10.9.9.9:8081", r.Message);
        Assert.Empty(api.Calls);
        Assert.StartsWith("uncertain", RecordAt(id));

        moved.Server = "127.0.0.1"; // back on the recorded server it goes out, same key
        Assert.Equal(SendAttemptStatus.Accepted, (await exec.RunAsync(s2, s2.Build("", null, false, false))).Status);
        Assert.Equal($"post 15552345678  {req.LockedPlan!.IdempotencyKey}", Assert.Single(api.Calls));
    }

    [Fact]
    public async Task A_record_from_an_older_version_without_a_server_resumes_unpinned()
    {
        var (id, pdf) = Spooled();
        File.WriteAllText(Path.Combine(Jobs, id + ".json"),
            $$"""{"spoolId":"{{id}}","serverId":null,"number":"15552345678","subject":null,"keepLocalCopies":false,"state":"uncertain","idempotencyKey":"old-key-0123456789"}""");
        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Null(req.Server);
        var s = SendSession.For(req);
        Assert.Null(s.LockedServer);
        Assert.True(s.ServerNotRecorded);
        var api = new FakeApi();
        var r = await new SendExecutor(api, new ClientConfig { Server = "10.9.9.9", Port = 8081, Token = "t" }, new JobTracker(_dir))
            .RunAsync(s, s.Build("", null, false, false));
        Assert.Equal(SendAttemptStatus.Accepted, r.Status);
        Assert.Equal("post 15552345678  old-key-0123456789", Assert.Single(api.Calls));
    }

    [Fact]
    public async Task The_sending_record_names_the_server_it_goes_to()
    {
        var (id, pdf) = Spooled();
        var api = new FakeApi();
        string? server = null;
        api.OnCall = () => server = JsonDocument.Parse(File.ReadAllText(Path.Combine(Jobs, id + ".json"))).RootElement
            .GetProperty("server").GetString();
        var session = new SendSession(id, pdf);
        await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal("127.0.0.1:8080", server);
    }

    [Fact]
    public async Task The_endpoint_is_taken_from_the_config_when_the_attempt_starts()
    {
        var (id, pdf) = Spooled();
        var api = new FakeApi();
        var session = new SendSession(id, pdf);
        await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, false, false));
        var ep = Assert.Single(api.Endpoints);
        Assert.Equal(new Uri("http://127.0.0.1:8080/"), ep.BaseUri);
        Assert.Equal("tok-a", ep.Token);
    }

    // ---- review finding 6 (low): documents over faxd's body limit are refused before anything is recorded ----

    [Fact]
    public async Task A_document_over_the_server_limit_is_not_sent()
    {
        var (id, pdf) = Spooled();
        using (var fs = new FileStream(pdf, FileMode.Open)) fs.SetLength(FaxApi.MaxBodyBytes + 1L);
        var api = new FakeApi();
        var session = new SendSession(id, pdf);
        var r = await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal(SendAttemptStatus.NotSent, r.Status);
        Assert.Contains("25 MB", r.Message);
        Assert.Empty(api.Calls);
        Assert.Null(RecordAt(id));
    }

    [Fact]
    public async Task A_test_page_instead_of_an_oversized_document_is_allowed()
    {
        var (id, pdf) = Spooled();
        using (var fs = new FileStream(pdf, FileMode.Open)) fs.SetLength(FaxApi.MaxBodyBytes + 1L);
        var api = new FakeApi();
        var session = new SendSession(id, pdf);
        var r = await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, true, false));
        Assert.Equal(SendAttemptStatus.Accepted, r.Status);
    }

    static System.Net.Http.HttpResponseMessage Json201() => new(System.Net.HttpStatusCode.Created)
    {
        Content = new StringContent("""
            {"id":"lf0123456789ab","direction":"out","state":"queued","to_number":"15552345678",
             "created_at":"2026-09-29T15:00:00+00:00","updated_at":"2026-09-29T15:00:01+00:00"}
            """, System.Text.Encoding.UTF8, "application/json"),
    };

    sealed record Seen(string Uri, string? Auth);

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<Seen> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new Seen(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(answer(request));
        }
    }
    (Guid id, string pdf) SpooledA(string body)
    {
        var id = Guid.NewGuid();
        var pdf = Path.Combine(Jobs, id + ".pdf");
        File.WriteAllText(pdf, body);
        return (id, pdf);
    }


// ---- multi-document sends and the blank-page pre-check ----

    (Guid id, string pdf) Spooled(string body = "%PDF-body\n%%EOF\n")
    {
        var id = Guid.NewGuid();
        var pdf = Path.Combine(Jobs, id + ".pdf");
        File.WriteAllText(pdf, body);
        return (id, pdf);
    }

    [Fact]
    public async Task Two_documents_are_uploaded_as_parts_and_journalled_as_one_fax()
    {
        var a = SpooledA("%PDF-a");
        var b = SpooledA("%PDF-b");
        var api = new FakeApi();
        var session = new SendSession(a.id, new[] { a.pdf, b.pdf }, testPageWhenEmpty: true);
        var plan = session.Build("5552345678", null, false, false);
        var tracker = new JobTracker(_dir);
        var r = await new SendExecutor(api, Cfg, tracker).RunAsync(session, plan);

        Assert.Equal(SendAttemptStatus.Accepted, r.Status);
        var parts = Assert.Single(api.Parts!);
        Assert.Equal(2, parts.Count);
        Assert.Equal(Path.GetFileName(a.pdf), parts[0].Name);
        Assert.Equal(File.ReadAllBytes(a.pdf), parts[0].Bytes);
        Assert.Equal(Path.GetFileName(b.pdf), parts[1].Name);
        Assert.Equal(File.ReadAllBytes(b.pdf), parts[1].Bytes);

        tracker.Track(plan, r.Job!);
        var tracked = Assert.Single(tracker.Pending);
        Assert.Equal(new[] { b.id }, tracked.Parts);
    }

    [Fact]
    public async Task One_missing_document_means_nothing_is_sent()
    {
        var a = Spooled();
        var b = Spooled();
        File.Delete(b.pdf);
        var api = new FakeApi();
        var session = new SendSession(a.id, new[] { a.pdf, b.pdf }, testPageWhenEmpty: true);
        var r = await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal(SendAttemptStatus.NotSent, r.Status);
        Assert.Contains(Path.GetFileName(b.pdf), r.Message);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task PreCheck_reports_blank_pages_and_surfaces_a_merge_error()
    {
        var a = Spooled();
        var session = new SendSession(a.id, a.pdf);
        var plan = session.Build("5552345678", null, false, false);
        var exec = new SendExecutor(new FakeApi(), Cfg, null);

        var (blanks, error) = await exec.PreCheckAsync(plan);
        Assert.Null(blanks);        // a fake server that cannot analyze: no check, no block
        Assert.Null(error);

        var api = new FakeApi { AnalyzeAnswer = new FaxCheck(3, [1, 2]) };
        (blanks, error) = await new SendExecutor(api, Cfg, null).PreCheckAsync(plan);
        Assert.Equal(new[] { 1, 2 }, blanks);
        Assert.Null(error);
        Assert.Single(api.Analyzed);

        api = new FakeApi { OnAnalyze = () => new FaxApiException(400, "the documents could not be combined") };
        (blanks, error) = await new SendExecutor(api, Cfg, null).PreCheckAsync(plan);
        Assert.Null(blanks);
        Assert.Contains("could not be combined", error);

        api = new FakeApi { OnAnalyze = () => new FaxApiUnauthorizedException() };
        (blanks, error) = await new SendExecutor(api, Cfg, null).PreCheckAsync(plan);
        Assert.Null(blanks);        // an auth problem now does not block: the send itself reports it
        Assert.Null(error);
    }

    [Fact]
    public async Task PreCheck_reads_the_documents_the_send_will_send()
    {
        var a = SpooledA("%PDF-a");
        var b = SpooledA("%PDF-b");
        var api = new FakeApi { AnalyzeAnswer = new FaxCheck(null, []) };
        var session = new SendSession(a.id, new[] { a.pdf, b.pdf }, testPageWhenEmpty: true);
        await new SendExecutor(api, Cfg, null).PreCheckAsync(session.Build("5552345678", null, false, false));
        var parts = Assert.Single(api.Analyzed);
        Assert.Equal(2, parts.Count);
    }

    // ---- the compose fax journals like a printed one (review fix) ----

    [Fact]
    public async Task A_compose_fax_is_journalled_under_its_first_document_before_the_request_goes_out()
    {
        var a = SpooledA("%PDF-a");
        var b = SpooledA("%PDF-b");
        var api = new FakeApi();
        string? recordState = null;
        api.OnCall = () => recordState = RecordAt(a.id);   // what is on disk while the request is out
        var throwaway = Guid.NewGuid();                    // the compose request's own id: never a file
        var session = new SendSession(throwaway, new[] { a.pdf, b.pdf }, testPageWhenEmpty: false);
        var tracker = new JobTracker(_dir);
        var plan = session.Build("5552345678", null, false, false);
        Assert.Equal(a.id, plan.JobId);                    // the first document's id, not the throwaway
        var r = await new SendExecutor(api, Cfg, tracker).RunAsync(session, plan);

        Assert.Equal(SendAttemptStatus.Accepted, r.Status);
        Assert.Equal("sending " + plan.IdempotencyKey, recordState);   // the record existed mid-flight
        tracker.Track(plan, r.Job!);
        var tracked = Assert.Single(tracker.Pending);
        Assert.Equal(new[] { b.id }, tracked.Parts);
    }

    [Fact]
    public async Task An_uncertain_compose_send_resumes_with_all_documents_and_the_same_key()
    {
        var a = SpooledA("%PDF-a");
        var b = SpooledA("%PDF-b");
        var api = new FakeApi(() => throw new FaxApiException(0, "timeout"));
        var session = new SendSession(Guid.NewGuid(), new[] { a.pdf, b.pdf }, testPageWhenEmpty: false);
        var key = session.IdempotencyKey;
        await new SendExecutor(api, Cfg, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, false, false));

        var offer = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.Resume, offer.Kind);
        Assert.Equal(a.id, offer.SpoolId);                 // keyed by the first document: a real file
        Assert.NotNull(offer.LockedPlan);
        Assert.Equal(new[] { a.pdf, b.pdf }, offer.LockedPlan!.PdfPaths);
        Assert.Equal(key, offer.LockedPlan.IdempotencyKey);
        Assert.True(File.Exists(a.pdf) && File.Exists(b.pdf));   // nothing eaten by a phantom primary
    }

    [Fact]
    public async Task PreCheck_skips_when_the_server_is_busy()
    {
        var a = Spooled();
        var api = new FakeApi { OnAnalyze = () => new FaxApiException(503, "too many uploads") };
        var session = new SendSession(a.id, a.pdf);
        var (blanks, error) = await new SendExecutor(api, Cfg, null)
            .PreCheckAsync(session.Build("5552345678", null, false, false));
        Assert.Null(blanks);
        Assert.Null(error);
    }

}
