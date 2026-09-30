using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

public sealed class SendExecutorTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-se-" + Guid.NewGuid().ToString("N"));
    string Jobs => Path.Combine(_dir, "jobs");

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

        public Task<Job> PostJobAsync(Stream pdf, string to, string? subject, string idempotencyKey, CancellationToken ct = default)
        {
            Calls.Add($"post {to} {subject} {idempotencyKey}");
            DiskAtCall.Add(OnCall?.Invoke());
            using var ms = new MemoryStream();
            pdf.CopyTo(ms);
            Body = ms.ToArray();
            return Task.FromResult((answer ?? (() => J("lf1")))());
        }

        public Task<Job> SendTestPageAsync(string to, string? note, string idempotencyKey, CancellationToken ct = default)
        {
            Calls.Add($"test {to} {note} {idempotencyKey}");
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
        var r = await new SendExecutor(api, new JobTracker(_dir)).RunAsync(session, plan);

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
        var r = await new SendExecutor(api, new JobTracker(_dir)).RunAsync(session, plan);
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
        var r = await new SendExecutor(api, new JobTracker(blocked)).RunAsync(session, plan);

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
        var r = await new SendExecutor(api, new JobTracker(_dir)).RunAsync(session, session.Build("5552345678", null, false, false));
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
        var r = await new SendExecutor(api, new JobTracker(_dir)).RunAsync(session, plan);
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
        var r = await new SendExecutor(api, null).RunAsync(session, session.Build("5552345678", null, false, false));
        Assert.Equal(SendAttemptStatus.Accepted, r.Status);
        Assert.Single(api.Calls);
        Assert.Empty(Directory.GetFiles(Jobs));
    }
}
