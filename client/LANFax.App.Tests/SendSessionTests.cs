using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

public class SendSessionTests
{
    static readonly Guid Spool = Guid.Parse("11111111-2222-3333-4444-555555555555");

    static Job J(string to) => new("lf1", "out", "queued", null, to, null, null, null, null, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    static string K(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}").ToString();

    static SendSession New(string? pdf = @"C:\spool\jobs\x.pdf")
    {
        int n = 0;
        return new SendSession(Spool, pdf, () => Guid.Parse($"00000000-0000-0000-0000-{++n:D12}"));
    }

    /// <summary>
    /// One char per step. R = attempt rejected (4xx), U = attempt uncertain (status 0), N = nothing sent (no attempt),
    /// 3 = attempt answered 302, 2 = attempt answered an unreadable 200, S = attempt accepted.
    /// </summary>
    static void Run(SendSession s, string steps, List<SendPlan> attempts)
    {
        foreach (var c in steps)
        {
            if (c == 'N') { s.Failed(AttemptOutcome.NotSent); continue; }
            var p = s.Build("555 234 5678", "hi", false, false);
            attempts.Add(p);
            s.Started(p);
            switch (c)
            {
                case 'R': s.Failed(SendOutcome.Classify(new FaxApiException(413, "too big"))); break;
                case 'U': s.Failed(SendOutcome.Classify(new FaxApiException(0, "cannot reach faxd"))); break;
                case '3': s.Failed(SendOutcome.Classify(new FaxApiException(302, "Found"))); break;
                case '2': s.Failed(SendOutcome.Classify(new FaxApiException(200, "faxd returned a job without id or state"))); break;
                case 'S': s.Succeeded(J("15552345678")); break;
                default: throw new ArgumentException(c.ToString());
            }
        }
    }

    // steps, locked afterwards, which generated key is current (K1 = first)
    [Theory]
    [InlineData("R", false, 2)]   // Fresh(K1)+Rejected -> Fresh(K2)
    [InlineData("RU", true, 2)]   // Fresh+Rejected+Uncertain -> Locked(K2)
    [InlineData("UN", true, 1)]   // Locked+NotSent -> Locked, same key
    [InlineData("RR", false, 3)]  // two rejections: two distinct new keys
    [InlineData("2", true, 1)]    // unreadable 200 -> Locked
    [InlineData("3", true, 1)]    // 3xx -> Uncertain -> Locked
    [InlineData("N", false, 1)]   // nothing sent: nothing changes
    [InlineData("UR", true, 1)]   // once locked, a later 4xx does not unlock or rotate
    [InlineData("US", true, 1)]   // Locked+Success
    public void Transition_table(string steps, bool locked, int key)
    {
        var s = New();
        var attempts = new List<SendPlan>();
        Run(s, steps, attempts);
        Assert.Equal(locked, s.FieldsLocked);
        Assert.Equal(K(key), s.IdempotencyKey);
        Assert.Equal(attempts.Count, s.Attempts);
        if (steps == "RR") Assert.Equal(new[] { K(1), K(2) }, attempts.Select(a => a.IdempotencyKey));
    }

    [Fact]
    public void Locked_then_success_accepts_the_locked_plan()
    {
        var s = New();
        var attempts = new List<SendPlan>();
        Run(s, "US", attempts);
        Assert.Same(s.LockedPlan, s.AcceptedPlan);
        Assert.Same(attempts[0], s.AcceptedPlan);
        Assert.Same(attempts[0], attempts[1]);
    }

    [Fact]
    public void Uncertain_without_started_throws()
    {
        var s = New();
        Assert.Throws<InvalidOperationException>(() => s.Failed(AttemptOutcome.Uncertain));
        Assert.Throws<InvalidOperationException>(() => s.Failed(AttemptOutcome.Rejected));
        Assert.Throws<InvalidOperationException>(() => s.Succeeded(J("1")));
        Assert.Equal(AttemptOutcome.NotSent, s.Failed(AttemptOutcome.NotSent)); // validation failure before Started is fine
    }

    [Fact]
    public void An_attempt_is_consumed_by_its_result()
    {
        var s = New();
        s.Started(s.Build("5552345678", null, false, false));
        s.Failed(AttemptOutcome.Rejected);
        Assert.Throws<InvalidOperationException>(() => s.Failed(AttemptOutcome.Uncertain));
    }

    [Fact]
    public void Uncertain_failure_locks_fields_and_keeps_key_and_plan()
    {
        var s = New();
        var p1 = s.Build("555 234 5678", "hi", false, false);
        s.Started(p1);
        Assert.Equal(AttemptOutcome.Uncertain, s.Failed(AttemptOutcome.Uncertain));
        Assert.True(s.FieldsLocked);
        var p2 = s.Build("555 999 0000", "other", true, true);
        Assert.Same(p1, p2);
        Assert.Equal(p1.IdempotencyKey, s.IdempotencyKey);
    }

    [Fact]
    public void Rejection_leaves_fields_editable()
    {
        var s = New();
        s.Started(s.Build("5552345678", null, false, false));
        s.Failed(AttemptOutcome.Rejected);
        Assert.Equal("15559990000", s.Build("5559990000", null, false, false).Number);
    }

    [Fact]
    public void Resume_is_locked_with_the_stored_key_and_plan()
    {
        var stored = SendPlan.Create(Spool, @"C:\spool\jobs\x.pdf", "5552345678", "subj", false, true, "stored-key");
        var s = SendSession.Resume(stored, () => throw new InvalidOperationException("no new key when resuming"));
        Assert.True(s.FieldsLocked);
        Assert.True(s.Resumed);
        Assert.Equal("stored-key", s.IdempotencyKey);
        Assert.Same(stored, s.Build("", null, true, false));
        s.Started(stored);
        Assert.Equal(AttemptOutcome.Rejected, s.Failed(AttemptOutcome.Rejected));
        Assert.Equal("stored-key", s.IdempotencyKey);
    }

    [Fact]
    public void No_pdf_forces_test_page()
    {
        var s = New(null);
        Assert.Equal(SendRoute.TestPage, s.Build("5552345678", null, false, false).Route);
    }

    [Fact]
    public void Note_texts()
    {
        Assert.Equal("The server may already have this job; fields are locked. Cancel stops retrying; the server may still send it.",
            SendSession.LockNote);
        Assert.Equal("This document may already have been sent; Send will not create a duplicate. Cancel keeps the document.",
            SendSession.ResumeNote);
        Assert.Equal("This document may already have been sent. Sending it again may fax it a second time.",
            SendSession.SuspectNote);
    }
}

public class SendOutcomeTests
{
    [Theory]
    [InlineData(null, AttemptOutcome.NotSent)]
    [InlineData(0, AttemptOutcome.Uncertain)]
    [InlineData(200, AttemptOutcome.Uncertain)]
    [InlineData(201, AttemptOutcome.Uncertain)]
    [InlineData(301, AttemptOutcome.Uncertain)]
    [InlineData(302, AttemptOutcome.Uncertain)]
    [InlineData(500, AttemptOutcome.Uncertain)]
    [InlineData(502, AttemptOutcome.Uncertain)]
    [InlineData(503, AttemptOutcome.Uncertain)]
    [InlineData(400, AttemptOutcome.Rejected)]
    [InlineData(401, AttemptOutcome.Rejected)]
    [InlineData(404, AttemptOutcome.Rejected)]
    [InlineData(413, AttemptOutcome.Rejected)]
    public void Classifies_status_codes(int? status, AttemptOutcome expected) =>
        Assert.Equal(expected, SendOutcome.Classify(status));

    public static TheoryData<Exception, AttemptOutcome> Exceptions => new()
    {
        { new FaxApiException(422, "bad number"), AttemptOutcome.Rejected },
        { new FaxApiUnauthorizedException(), AttemptOutcome.Rejected },
        { new FaxApiException(0, "cannot reach faxd"), AttemptOutcome.Uncertain },
        { new FaxApiException(200, "unreadable"), AttemptOutcome.Uncertain },
        { new FaxApiException(307, "Temporary Redirect"), AttemptOutcome.Uncertain },
        // Anything else thrown after Started: the request may have gone out.
        { new ArgumentException("the fax number has no digits"), AttemptOutcome.Uncertain },
        { new TaskCanceledException(), AttemptOutcome.Uncertain },
        { new HttpRequestException("reset"), AttemptOutcome.Uncertain },
        { new IOException("disk"), AttemptOutcome.Uncertain },
    };

    [Theory]
    [MemberData(nameof(Exceptions))]
    public void Classifies_exceptions(Exception e, AttemptOutcome expected) => Assert.Equal(expected, SendOutcome.Classify(e));

    [Fact]
    public void Describes_exceptions()
    {
        Assert.Equal("The server rejected the token. Check Settings and try again.",
            SendOutcome.Describe(new FaxApiUnauthorizedException()));
        Assert.Equal("number blocked", SendOutcome.Describe(new FaxApiException(422, "number blocked")));
        Assert.Equal("Sending failed: boom", SendOutcome.Describe(new InvalidOperationException("boom")));
    }
}

public class SendGateTests
{
    [Theory]
    [InlineData(false, false, true, false, false, true)]   // normal, valid number
    [InlineData(false, false, false, false, false, false)] // invalid number
    [InlineData(true, false, true, false, false, false)]   // busy
    [InlineData(false, true, false, false, false, true)]   // locked: the stored plan is sent whatever the field says
    [InlineData(false, false, true, true, false, false)]   // may already have been sent: needs the confirmation
    [InlineData(false, false, true, true, true, true)]
    public void Can_send(bool busy, bool locked, bool numberOk, bool needsConfirm, bool confirmed, bool expected) =>
        Assert.Equal(expected, SendGate.CanSend(busy, locked, numberOk, needsConfirm, confirmed));
}

public class DialogQueueTests
{
    [Fact]
    public void Shows_one_at_a_time_in_order_and_titles_count_waiting()
    {
        var q = new DialogQueue<string>();
        Assert.Equal("a", q.Enqueue("a"));
        Assert.Equal("Send fax", q.Title("Send fax"));
        Assert.Null(q.Enqueue("b"));
        Assert.Equal("Send fax (1 more waiting)", q.Title("Send fax"));
        Assert.Null(q.Enqueue("c"));
        Assert.Equal("Send fax (2 more waiting)", q.Title("Send fax"));
        Assert.True(q.Contains(x => x == "a") && q.Contains(x => x == "c") && !q.Contains(x => x == "z"));
        Assert.Equal("b", q.Complete());
        Assert.Equal("c", q.Complete());
        Assert.Equal("Send fax", q.Title("Send fax"));
        Assert.Null(q.Complete());
        Assert.Null(q.Current);
        Assert.Equal("d", q.Enqueue("d"));
    }

    [Fact]
    public void Show_skips_items_whose_dialog_fails_to_open()
    {
        var q = new DialogQueue<string>();
        q.Enqueue("a"); q.Enqueue("b"); q.Enqueue("c");
        var opened = new List<string>();
        var failed = new List<string>();
        q.Show("a", x => { if (x != "c") throw new InvalidOperationException(x); opened.Add(x); }, (x, _) => failed.Add(x));
        Assert.Equal(new[] { "a", "b" }, failed);
        Assert.Equal(new[] { "c" }, opened);
        Assert.Equal("c", q.Current);
        Assert.Equal(0, q.Waiting);

        q.Complete();
        q.Enqueue("d");
        q.Show("d", _ => throw new InvalidOperationException(), (_, _) => { });
        Assert.Null(q.Current); // nothing left: the queue is free again
        Assert.Equal("e", q.Enqueue("e"));
    }
}

public class NotificationQueueTests
{
    sealed class Clock { public DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc); }

    [Fact]
    public void One_at_a_time_and_a_new_notice_never_replaces_a_pending_click_action()
    {
        var clock = new Clock();
        var shown = new List<string>();
        var q = new NotificationQueue(n => shown.Add(n.Text), () => clock.Now);
        int clicks = 0;
        q.Enqueue("Retry?", () => clicks++);
        q.Enqueue("Fax sent"); // e.g. the first poll finishing while the retry notice is up
        Assert.Equal(new[] { "Retry?" }, shown);
        Assert.Equal(1, q.Waiting);
        q.OnClicked();
        Assert.Equal(1, clicks);
        Assert.Equal(new[] { "Retry?", "Fax sent" }, shown);
        q.OnClicked(); // no action on the second notice
        Assert.Equal(1, clicks);
        Assert.Null(q.Current);
    }

    [Fact]
    public void Closed_right_after_showing_is_the_previous_balloon_and_does_not_advance()
    {
        var clock = new Clock();
        var shown = new List<string>();
        var q = new NotificationQueue(n => shown.Add(n.Text), () => clock.Now);
        q.Enqueue("a"); q.Enqueue("b"); q.Enqueue("c");
        q.OnClicked();          // a -> b
        q.OnClosed();           // a's own Closed arrives right after: ignored
        Assert.Equal("b", q.Current!.Text);
        clock.Now += TimeSpan.FromSeconds(3);
        q.OnClosed();           // b closed by the user
        Assert.Equal("c", q.Current!.Text);
        Assert.Equal(new[] { "a", "b", "c" }, shown);
    }

    [Fact]
    public void Timer_advances_but_a_stale_timer_does_not()
    {
        var clock = new Clock();
        var q = new NotificationQueue(_ => { }, () => clock.Now);
        q.Enqueue("a"); q.Enqueue("b"); q.Enqueue("c");
        var aId = q.Current!.Id;
        q.OnTimeout(aId);
        Assert.Equal("b", q.Current!.Text);
        q.OnTimeout(aId); // the timer for a fired late
        Assert.Equal("b", q.Current!.Text);
        q.OnTimeout(q.Current.Id);
        Assert.Equal("c", q.Current!.Text);
        Assert.Equal(TimeSpan.FromSeconds(8), NotificationQueue.Duration);
    }

    [Fact]
    public void A_throwing_click_action_still_advances()
    {
        var q = new NotificationQueue(_ => { });
        q.Enqueue("a", () => throw new InvalidOperationException());
        q.Enqueue("b");
        Assert.Throws<InvalidOperationException>(() => q.OnClicked());
        Assert.Equal("b", q.Current!.Text);
    }
}

public class OfferBookTests
{
    [Fact]
    public void Counts_and_takes_by_category_one_entry_per_document()
    {
        var b = new OfferBook();
        var a = new SendRequest(Guid.NewGuid(), "a.pdf", Kind: SendKind.Retry);
        var c = new SendRequest(Guid.NewGuid(), "c.pdf", Kind: SendKind.Resume);
        b.Add(OfferCategory.Retry, a);
        b.Add(OfferCategory.Unsent, c);
        b.Add(OfferCategory.Unsent, a); // the same document moves, it is not counted twice
        Assert.Equal(0, b.Count(OfferCategory.Retry));
        Assert.Equal(2, b.Count(OfferCategory.Unsent));
        b.Remove(c.SpoolId);
        Assert.Equal(new[] { a }, b.TakeAll(OfferCategory.Unsent));
        Assert.Equal(0, b.Count(OfferCategory.Unsent));
        Assert.Equal("Unsent documents (2)...", OfferBook.MenuText(OfferCategory.Unsent, 2));
        Assert.Equal("Retry failed fax (1)...", OfferBook.MenuText(OfferCategory.Retry, 1));
    }

    [Fact]
    public void Take_is_true_once_so_a_stale_balloon_click_cannot_reopen_a_handled_document()
    {
        var b = new OfferBook();
        var a = new SendRequest(Guid.NewGuid(), "a.pdf", Kind: SendKind.Retry);
        b.Add(OfferCategory.Retry, a);
        Assert.True(b.Take(a.SpoolId));
        Assert.False(b.Take(a.SpoolId));
        Assert.Equal(0, b.Count(OfferCategory.Retry));
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
        n.Reset();
        Assert.True(n.ShouldShow(new FaxApiUnauthorizedException()));
    }
}
