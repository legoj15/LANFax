using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

public class SendSessionTests
{
    static readonly Guid Spool = Guid.Parse("11111111-2222-3333-4444-555555555555");

    static Job J(string to) => new("lf1", "out", "queued", null, to, null, null, null, null, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    static SendSession New(string? pdf = @"C:\spool\jobs\x.pdf")
    {
        int n = 0;
        return new SendSession(Spool, pdf, () => Guid.Parse($"00000000-0000-0000-0000-{++n:D12}"));
    }

    [Theory]
    [InlineData(null, AttemptOutcome.NotSent)]
    [InlineData(0, AttemptOutcome.Uncertain)]
    [InlineData(502, AttemptOutcome.Uncertain)]
    [InlineData(503, AttemptOutcome.Uncertain)]
    [InlineData(500, AttemptOutcome.Uncertain)]
    [InlineData(200, AttemptOutcome.Uncertain)] // unreadable 2xx: the server may well have the job
    [InlineData(400, AttemptOutcome.Rejected)]
    [InlineData(401, AttemptOutcome.Rejected)]
    [InlineData(413, AttemptOutcome.Rejected)]
    public void Classifies_status_codes(int? status, AttemptOutcome expected) =>
        Assert.Equal(expected, SendSession.Classify(status));

    [Fact]
    public void Uncertain_failure_locks_fields_and_keeps_key_and_plan()
    {
        var s = New();
        var p1 = s.Build("555 234 5678", "hi", false, false);
        s.Started(p1);
        Assert.Equal(AttemptOutcome.Uncertain, s.Failed(0));
        Assert.True(s.FieldsLocked);
        // Whatever the (read-only) fields now say, the next attempt is the identical request.
        var p2 = s.Build("555 999 0000", "other", true, true);
        Assert.Same(p1, p2);
        Assert.Equal(p1.IdempotencyKey, s.IdempotencyKey);
        s.Started(p2);
        Assert.Equal(2, s.Attempts);
    }

    [Fact]
    public void Rejection_rotates_key_and_leaves_fields_editable()
    {
        var s = New();
        var p1 = s.Build("5552345678", null, false, false);
        s.Started(p1);
        Assert.Equal(AttemptOutcome.Rejected, s.Failed(413));
        Assert.False(s.FieldsLocked);
        var p2 = s.Build("5559990000", null, false, false);
        Assert.NotEqual(p1.IdempotencyKey, p2.IdempotencyKey);
        Assert.Equal("15559990000", p2.Number);
    }

    [Fact]
    public void Once_locked_stays_locked_even_after_a_rejection()
    {
        var s = New();
        var p = s.Build("5552345678", null, false, false);
        s.Started(p);
        s.Failed(503);
        var key = s.IdempotencyKey;
        s.Started(s.Build("", null, false, false));
        Assert.Equal(AttemptOutcome.Rejected, s.Failed(401));
        Assert.True(s.FieldsLocked);
        Assert.Equal(key, s.IdempotencyKey);
    }

    [Fact]
    public void Nothing_sent_changes_nothing()
    {
        var s = New();
        var p = s.Build("5552345678", null, false, false);
        s.Started(p);
        Assert.Equal(AttemptOutcome.NotSent, s.Failed(null));
        Assert.False(s.FieldsLocked);
        Assert.Equal(p.IdempotencyKey, s.IdempotencyKey);
    }

    [Fact]
    public void Success_records_job_and_plan()
    {
        var s = New();
        var p = s.Build("5552345678", null, false, false);
        s.Started(p);
        s.Succeeded(J("15552345678"));
        Assert.Equal("lf1", s.Accepted!.Id);
        Assert.Same(p, s.AcceptedPlan);
    }

    [Fact]
    public void No_pdf_forces_test_page()
    {
        var s = New(null);
        Assert.Equal(SendRoute.TestPage, s.Build("5552345678", null, false, false).Route);
    }

    [Fact]
    public void Lock_note_text() =>
        Assert.Equal("The server may already have this job; fields are locked. Cancel to discard.", SendSession.LockNote);
}

public class DialogQueueTests
{
    [Fact]
    public void Shows_one_at_a_time_in_order_and_titles_count_waiting()
    {
        var q = new DialogQueue<string>();
        Assert.Equal("a", q.Enqueue("a"));   // nothing open: show now
        Assert.Null(q.Enqueue("b"));          // queued behind a
        Assert.Equal("Send fax (1 of 2 waiting)", q.Title("Send fax"));
        Assert.Null(q.Enqueue("c"));
        Assert.Equal("Send fax (1 of 3 waiting)", q.Title("Send fax"));
        Assert.Equal("a", q.Current);
        Assert.Equal("b", q.Complete());
        Assert.Equal("c", q.Complete());
        Assert.Equal("Send fax", q.Title("Send fax"));
        Assert.Null(q.Complete());
        Assert.Null(q.Current);
        Assert.Equal("d", q.Enqueue("d"));
    }
}

public class UnauthorizedNoticeTests
{
    [Fact]
    public void Shows_once_per_run_until_reset_and_only_for_401()
    {
        var n = new UnauthorizedNotice();
        Assert.False(n.ShouldShow(null));
        Assert.False(n.ShouldShow(new FaxApiException(503, "x")));
        Assert.False(n.ShouldShow(new TimeoutException()));
        Assert.True(n.ShouldShow(new FaxApiException(401, "events: HTTP 401")));
        Assert.False(n.ShouldShow(new FaxApiUnauthorizedException()));
        n.Reset(); // settings saved
        Assert.True(n.ShouldShow(new FaxApiUnauthorizedException()));
    }
}
