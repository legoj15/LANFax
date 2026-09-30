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
    /// One char per step. R = attempt rejected (413), A = attempt answered 401, U = attempt uncertain (status 0),
    /// N = nothing sent (no attempt), 3 = attempt answered 302, 2 = attempt answered an unreadable 200, S = attempt accepted.
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
                case 'R': s.Failed(new FaxApiException(413, "too big")); break;
                case 'A': s.Failed(new FaxApiUnauthorizedException()); break;
                case 'U': s.Failed(new FaxApiException(0, "cannot reach faxd")); break;
                case '3': s.Failed(new FaxApiException(302, "Found")); break;
                case '2': s.Failed(new FaxApiException(200, "faxd returned a job without id or state")); break;
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
    [InlineData("UR", false, 2)]  // Locked+4xx: the server checks the key before the body, so no job exists: unlock, rotate
    [InlineData("URU", true, 2)]  // ...and a later uncertain failure locks again under the new key
    [InlineData("UA", true, 1)]   // Locked+401: auth fails before the key lookup, so the job may exist: stay locked
    [InlineData("A", false, 2)]   // Fresh+401: nothing was ever uncertain: rotate as any rejection
    [InlineData("UAR", false, 2)] // a 401 then a real 4xx under the same key: unlock
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
        var s = SendSession.Resume(stored, null, () => throw new InvalidOperationException("no new key when resuming"));
        Assert.True(s.FieldsLocked);
        Assert.True(s.Resumed);
        Assert.Equal("stored-key", s.IdempotencyKey);
        Assert.Same(stored, s.Build("", null, true, false));
        s.Started(stored);
        Assert.Equal(AttemptOutcome.Rejected, s.Failed(new FaxApiUnauthorizedException()));
        Assert.Equal("stored-key", s.IdempotencyKey);
        Assert.True(s.FieldsLocked);
    }

    [Fact]
    public void Resumed_session_unlocks_and_rotates_after_a_definite_rejection()
    {
        var stored = SendPlan.Create(Spool, @"C:\spool\jobs\x.pdf", "5552345678", "subj", false, true, "stored-key");
        var s = SendSession.Resume(stored, null, () => Guid.Parse("00000000-0000-0000-0000-000000000009"));
        s.Started(stored);
        Assert.Equal(AttemptOutcome.Rejected, s.Failed(new FaxApiException(422, "bad number")));
        Assert.False(s.FieldsLocked);
        Assert.Null(s.LockedPlan);
        Assert.Equal(K(9), s.IdempotencyKey);
        var next = s.Build("555 999 0000", null, false, false); // fields are editable again
        Assert.Equal("15559990000", next.Number);
        Assert.Equal(K(9), next.IdempotencyKey);
    }

    [Fact]
    public void A_rejection_without_a_status_never_unlocks()
    {
        // Failed(outcome) without the status cannot tell a 401 from another 4xx: stay on the safe side.
        var s = New();
        var p = s.Build("5552345678", null, false, false);
        s.Started(p); s.Failed(AttemptOutcome.Uncertain);
        s.Started(p); s.Failed(AttemptOutcome.Rejected);
        Assert.True(s.FieldsLocked);
        Assert.Equal(K(1), s.IdempotencyKey);
    }

    [Theory]
    [InlineData(false, "", true, false)]    // fresh dialog: Cancel discards
    [InlineData(false, "R", true, false)]   // after a rejection: nothing at the server, Cancel discards
    [InlineData(false, "U", true, true)]    // after an uncertain failure in this run: Cancel keeps it, like a resumed dialog
    [InlineData(false, "UR", true, false)]  // ...unless a later 4xx proved the server has nothing
    [InlineData(true, "", true, true)]      // resumed locked: Cancel keeps it
    [InlineData(true, "R", true, false)]    // resumed, then a definite 4xx: unlocked, Cancel discards
    [InlineData(false, "U", false, false)]  // the tray's test page has no document to keep
    public void Cancel_keeps_the_document_while_the_server_may_have_it(bool resumed, string steps, bool hasPdf, bool keeps)
    {
        var pdf = hasPdf ? @"C:\spool\jobs\x.pdf" : null;
        var s = resumed
            ? SendSession.Resume(SendPlan.Create(Spool, pdf!, "5552345678", null, false, false, "stored-key"), null, () => Guid.NewGuid())
            : New(pdf);
        Run(s, steps, new List<SendPlan>());
        Assert.Equal(keeps, s.CancelKeepsDocument);
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
        Assert.Equal("The server may already have this job; fields are locked. Send will not create a duplicate. " +
            "Cancel keeps the document (see \"Unsent documents\" in the LANFax menu).", SendSession.LockNote);
        Assert.Equal("This document may already have been sent; Send will not create a duplicate. Cancel keeps the document.",
            SendSession.ResumeNote);
        Assert.Equal("This document may already have been sent. Sending it again may fax it a second time.",
            SendSession.SuspectNote);
    }

    [Theory]
    [InlineData(SendKind.Suspect, true)]  // "may already have been sent": Cancel must not destroy it
    [InlineData(SendKind.New, false)]
    [InlineData(SendKind.Draft, false)]
    [InlineData(SendKind.Retry, false)]
    public void Cancel_keeps_a_suspect_document(SendKind kind, bool keeps)
    {
        Assert.Equal(keeps, New().CancelKeepsDocumentFor(kind));
        Assert.False(New(null).CancelKeepsDocumentFor(kind)); // the tray's test page has no document
    }

    [Fact]
    public void Cancel_keeps_a_locked_document_of_any_kind()
    {
        var s = New();
        var list = new List<SendPlan>();
        Run(s, "U", list);
        Assert.True(s.CancelKeepsDocumentFor(SendKind.New));
    }

    [Fact]
    public void A_config_error_after_Started_is_not_sent_and_changes_nothing()
    {
        var s = New();
        var key = s.IdempotencyKey;
        s.Started(s.Build("5552345678", null, false, false));
        Assert.Equal(AttemptOutcome.NotSent, s.Failed(new FaxApiConfigException("bad server")));
        Assert.False(s.InFlight);
        Assert.False(s.FieldsLocked);
        Assert.Equal(key, s.IdempotencyKey);
    }

    [Fact]
    public void A_409_locks_a_fresh_session_and_keeps_a_locked_one_locked_with_the_same_key()
    {
        var s = New();
        var p = s.Build("5552345678", null, false, false);
        s.Started(p);
        Assert.Equal(AttemptOutcome.Uncertain, s.Failed(new FaxApiException(409, "idempotency key already used for a different number")));
        Assert.True(s.FieldsLocked);
        Assert.Equal(K(1), s.IdempotencyKey);
        s.Started(p);
        Assert.Equal(AttemptOutcome.Uncertain, s.Failed(new FaxApiException(409, "idempotency key already used for a different number")));
        Assert.True(s.FieldsLocked);
        Assert.Equal(K(1), s.IdempotencyKey);
    }

    [Fact]
    public void A_resumed_session_is_pinned_to_the_recorded_server()
    {
        var stored = SendPlan.Create(Spool, @"C:\spool\jobs\x.pdf", "5552345678", null, false, false, "stored-key");
        var s = SendSession.Resume(stored, "127.0.0.1:8081");
        Assert.Equal("127.0.0.1:8081", s.LockedServer!.Authority);
        Assert.False(s.ServerNotRecorded);
        Assert.Equal(SendSession.ResumeNote, s.ResumeText);
    }

    [Fact]
    public void A_resumed_session_from_an_older_record_is_unpinned_and_says_so()
    {
        var stored = SendPlan.Create(Spool, @"C:\spool\jobs\x.pdf", "5552345678", null, false, false, "stored-key");
        var s = SendSession.Resume(stored, null);
        Assert.Null(s.LockedServer);
        Assert.True(s.ServerNotRecorded);
        Assert.Contains("sent from an earlier version; server not recorded", s.ResumeText);
        Assert.False(new SendSession(Spool, @"C:\spool\jobs\x.pdf").ServerNotRecorded); // only resumed sessions
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
    // faxd answers 409 when the key already holds a job for another number: a job exists under the key.
    [InlineData(409, AttemptOutcome.Uncertain)]
    public void Classifies_status_codes(int? status, AttemptOutcome expected) =>
        Assert.Equal(expected, SendOutcome.Classify(status));

    public static TheoryData<Exception, AttemptOutcome> Exceptions => new()
    {
        { new FaxApiException(422, "bad number"), AttemptOutcome.Rejected },
        { new FaxApiUnauthorizedException(), AttemptOutcome.Rejected },
        { new FaxApiException(0, "cannot reach faxd"), AttemptOutcome.Uncertain },
        { new FaxApiException(200, "unreadable"), AttemptOutcome.Uncertain },
        { new FaxApiException(307, "Temporary Redirect"), AttemptOutcome.Uncertain },
        // The request URI could not be built from the settings: provably nothing went out.
        { new FaxApiConfigException("bad server"), AttemptOutcome.NotSent },
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
    public void Click_stops_the_timer_before_the_action_and_advances_once_after_it_returns()
    {
        // The click action may open a modal dialog (Settings) that pumps messages: the 8 s timer or the balloon's
        // Closed must not advance the queue underneath it, and the next notice shows only after the action returns.
        var clock = new Clock();
        var shown = new List<string>();
        bool timerRunning = false;
        NotificationQueue q = null!;
        var log = new List<string>();
        q = new NotificationQueue(n => { shown.Add(n.Text); timerRunning = true; }, () => clock.Now,
            stopTimer: () => timerRunning = false);
        q.Enqueue("Open settings?", () =>
        {
            log.Add($"action, timer running={timerRunning}");
            clock.Now += TimeSpan.FromSeconds(20); // the user takes a while in the dialog
            q.OnTimeout(q.Current!.Id);            // a tick that was already queued
            q.OnClosed();                          // the balloon closing itself
            log.Add($"current during action={q.Current?.Text}");
        });
        q.Enqueue("b");
        q.Enqueue("c");
        q.OnClicked();
        Assert.Equal(new[] { "action, timer running=False", "current during action=Open settings?" }, log);
        Assert.Equal(new[] { "Open settings?", "b" }, shown);
        Assert.Equal("b", q.Current!.Text);
        Assert.Equal(1, q.Waiting);
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
