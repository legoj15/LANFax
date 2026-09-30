using System.Text.Json;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

public sealed class JobTrackerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-jt-" + Guid.NewGuid().ToString("N"));
    string Jobs => Path.Combine(_dir, "jobs");
    string JsonOf(Guid id) => Path.Combine(Jobs, id + ".json");

    public JobTrackerTests() => Directory.CreateDirectory(Jobs);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static Job J(string id, string state, string to = "15552345678", string? err = null) =>
        new(id, "out", state, null, to, null, null, null, null, err, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    (Guid id, string pdf) Spooled()
    {
        var id = Guid.NewGuid();
        var pdf = Path.Combine(Jobs, id + ".pdf");
        File.WriteAllText(pdf, "%PDF-1.4");
        return (id, pdf);
    }

    static SendPlan Plan(Guid id, string pdf, bool keep, bool testPage = false, string? key = null) =>
        SendPlan.Create(id, pdf, "5552345678", "subj", testPage, keep, key ?? Guid.NewGuid().ToString());

    JsonElement Record(Guid id) => JsonDocument.Parse(File.ReadAllText(JsonOf(id))).RootElement;

    // ---- persisted send states ----

    [Fact]
    public void BeginSend_persists_sending_with_key_and_full_plan_before_anything_goes_out()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.BeginSend(Plan(id, pdf, keep: true, key: "K1"));
        var r = Record(id);
        Assert.Equal("sending", r.GetProperty("state").GetString());
        Assert.Equal("K1", r.GetProperty("idempotencyKey").GetString());
        Assert.Equal("15552345678", r.GetProperty("number").GetString());
        Assert.Equal("subj", r.GetProperty("subject").GetString());
        Assert.False(r.GetProperty("testPage").GetBoolean());
        Assert.True(r.GetProperty("keepLocalCopies").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("serverId").ValueKind);
        Assert.Empty(Directory.GetFiles(Jobs, "*.tmp"));
    }

    [Fact]
    public void BeginSend_failure_throws_so_the_caller_does_not_send()
    {
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "a file where the spool dir should be");
        var t = new JobTracker(blocked);
        Assert.ThrowsAny<IOException>(() => t.BeginSend(Plan(Guid.NewGuid(), "x.pdf", false)));
    }

    // Fresh session attempts, then the tracker transition that follows, then a restart.
    [Theory]
    [InlineData(413, "draft")]    // definite rejection: plan editable, key discarded
    [InlineData(0, "uncertain")]  // may have gone out: same key, same plan
    [InlineData(503, "uncertain")]
    [InlineData(302, "uncertain")]
    public void RecordFailure_persists_draft_or_uncertain(int status, string expected)
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var s = new SendSession(id, pdf);
        var p = s.Build("555 234 5678", "subj", false, false);
        t.BeginSend(p);
        s.Started(p);
        s.Failed(SendOutcome.Classify(new FaxApiException(status, "x")));
        t.RecordFailure(s, p);
        var r = Record(id);
        Assert.Equal(expected, r.GetProperty("state").GetString());
        if (expected == "draft") Assert.Equal(JsonValueKind.Null, r.GetProperty("idempotencyKey").ValueKind);
        else Assert.Equal(p.IdempotencyKey, r.GetProperty("idempotencyKey").GetString());
        Assert.Equal("15552345678", r.GetProperty("number").GetString());
    }

    [Fact]
    public void Locked_session_stays_uncertain_even_after_a_later_rejection()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var s = new SendSession(id, pdf);
        var p = s.Build("5552345678", null, false, false);
        t.BeginSend(p); s.Started(p); s.Failed(AttemptOutcome.Uncertain); t.RecordFailure(s, p);
        t.BeginSend(p); s.Started(p); s.Failed(AttemptOutcome.Rejected); t.RecordFailure(s, p);
        Assert.Equal("uncertain", Record(id).GetProperty("state").GetString());
        Assert.Equal(p.IdempotencyKey, Record(id).GetProperty("idempotencyKey").GetString());
    }

    [Theory]
    [InlineData("sending")]
    [InlineData("uncertain")]
    public void Restart_in_sending_or_uncertain_reopens_locked_with_the_same_key_and_plan(string state)
    {
        var (id, pdf) = Spooled();
        var plan = Plan(id, pdf, keep: true, key: "K-stored");
        var t1 = new JobTracker(_dir);
        t1.BeginSend(plan);
        if (state == "uncertain")
        {
            var s = SendSession.Resume(plan);
            s.Started(plan); s.Failed(AttemptOutcome.Uncertain);
            t1.RecordFailure(s, plan);
        }

        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.Resume, req.Kind);
        Assert.Equal(id, req.SpoolId);
        Assert.Equal(pdf, req.PdfPath);
        Assert.Equal(plan, req.LockedPlan); // record equality: same key, number, subject, route, file action
        var s2 = SendSession.Resume(req.LockedPlan!);
        Assert.True(s2.FieldsLocked);
        Assert.Equal("K-stored", s2.IdempotencyKey);
        Assert.True(File.Exists(pdf) && File.Exists(JsonOf(id))); // nothing is deleted by the scan
    }

    [Fact]
    public void Restart_keeps_the_test_page_route()
    {
        var (id, pdf) = Spooled();
        var plan = Plan(id, pdf, keep: true, testPage: true, key: "K");
        new JobTracker(_dir).BeginSend(plan);
        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendRoute.TestPage, req.LockedPlan!.Route);
        Assert.Equal(FileAction.Delete, req.LockedPlan.AfterAccept);
        Assert.Equal(plan, req.LockedPlan);
    }

    [Fact]
    public void Restart_in_draft_reopens_unlocked_with_the_number_prefilled()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var s = new SendSession(id, pdf);
        var p = s.Build("5552345678", "subj", false, false);
        t.BeginSend(p); s.Started(p); s.Failed(AttemptOutcome.Rejected); t.RecordFailure(s, p);

        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.Draft, req.Kind);
        Assert.Null(req.LockedPlan);
        Assert.Equal("15552345678", req.Number);
        Assert.Equal("subj", req.Subject);
    }

    [Fact]
    public void Sending_record_whose_pdf_is_gone_is_removed()
    {
        var (id, pdf) = Spooled();
        new JobTracker(_dir).BeginSend(Plan(id, pdf, false));
        File.Delete(pdf);
        Assert.Empty(new JobTracker(_dir).Scan());
        Assert.False(File.Exists(JsonOf(id)));
    }

    [Fact]
    public void Corrupt_json_is_renamed_bad_and_its_pdf_offered_with_the_warning_until_handled()
    {
        var (id, pdf) = Spooled();
        File.WriteAllText(JsonOf(id), "{not json");

        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.Suspect, req.Kind);
        Assert.True(req.NeedsConfirmation);
        Assert.Null(req.LockedPlan); // a fresh key, only after the user confirms
        Assert.False(File.Exists(JsonOf(id)));
        Assert.True(File.Exists(JsonOf(id) + ".bad"));

        // Not handled before the next start (app exit): still offered with the warning.
        Assert.Equal(SendKind.Suspect, Assert.Single(new JobTracker(_dir).Scan()).Kind);

        var t = new JobTracker(_dir);
        t.Discard(id);
        Assert.False(File.Exists(pdf) || File.Exists(JsonOf(id) + ".bad"));
    }

    [Fact]
    public void Record_without_key_in_sending_state_is_offered_with_the_warning_not_stranded()
    {
        var (id, _) = Spooled();
        File.WriteAllText(JsonOf(id),
            $$"""{"spoolId":"{{id}}","serverId":null,"number":"15552345678","subject":null,"keepLocalCopies":false,"state":"sending"}""");
        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.Suspect, req.Kind);
        Assert.Equal("15552345678", req.Number);
    }

    [Fact]
    public void Legacy_accepted_record_without_server_id_is_offered_with_the_warning()
    {
        var (id, _) = Spooled();
        File.WriteAllText(JsonOf(id),
            $$"""{"spoolId":"{{id}}","serverId":null,"number":"15552345678","subject":null,"keepLocalCopies":false,"state":"queued"}""");
        var t = new JobTracker(_dir);
        Assert.Equal(SendKind.Suspect, Assert.Single(t.Scan()).Kind);
        Assert.Empty(t.Pending);
    }

    [Fact]
    public void Legacy_queued_record_with_server_id_is_retracked()
    {
        var (id, _) = Spooled();
        File.WriteAllText(JsonOf(id),
            $$"""{"spoolId":"{{id}}","serverId":"lfq","number":"15552345678","subject":null,"keepLocalCopies":false,"state":"queued"}""");
        var t = new JobTracker(_dir);
        Assert.Empty(t.Scan());
        Assert.Equal("lfq", Assert.Single(t.Pending).ServerId);
    }

    // ---- accepted jobs ----

    [Fact]
    public void Track_persists_accepted_with_server_id_and_keeps_pdf_until_terminal()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var p = Plan(id, pdf, false);
        t.BeginSend(p);
        t.Track(p, J("lf1", "queued"));
        var r = Record(id);
        Assert.Equal("accepted", r.GetProperty("state").GetString());
        Assert.Equal("lf1", r.GetProperty("serverId").GetString());
        Assert.Null(t.OnJobUpdate(J("lf1", "sending")));
        Assert.True(File.Exists(pdf));
        Assert.Single(t.Pending);
    }

    [Fact]
    public void Sent_deletes_pdf_and_json()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.Track(Plan(id, pdf, false), J("lf1", "queued"));
        var r = t.OnJobUpdate(J("lf1", "sent"));
        Assert.Equal(TerminalOutcome.Removed, r!.Outcome);
        Assert.False(File.Exists(pdf));
        Assert.False(File.Exists(JsonOf(id)));
        Assert.Empty(t.Pending);
        Assert.Null(t.OnJobUpdate(J("lf1", "sent")));
    }

    [Fact]
    public void Sent_with_keep_moves_to_sent_dir()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.Track(Plan(id, pdf, true), J("lf1", "queued"));
        Assert.Equal(TerminalOutcome.MovedToSent, t.OnJobUpdate(J("lf1", "sent"))!.Outcome);
        Assert.True(File.Exists(Path.Combine(_dir, "sent", id + ".pdf")));
        Assert.False(File.Exists(pdf));
    }

    [Fact]
    public void Failed_keeps_pdf_for_retry_and_retry_retracks()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.Track(Plan(id, pdf, false), J("lf1", "queued"));
        var r = t.OnJobUpdate(J("lf1", "failed", err: "busy"))!;
        Assert.Equal(TerminalOutcome.KeptForRetry, r.Outcome);
        Assert.Equal(id, r.Tracked.SpoolId);
        Assert.Equal("15552345678", r.Tracked.Number);
        Assert.Equal("subj", r.Tracked.Subject);
        Assert.True(File.Exists(pdf));
        Assert.Empty(t.Pending);
        Assert.Null(t.OnJobUpdate(J("lf1", "failed")));
        t.Track(Plan(id, pdf, false), J("lf2", "queued"));
        Assert.Single(t.Pending);
        Assert.Equal(TerminalOutcome.Removed, t.OnJobUpdate(J("lf2", "sent"))!.Outcome);
        Assert.False(File.Exists(pdf));
    }

    [Fact]
    public void Unknown_to_the_server_stops_polling_and_offers_the_pdf_with_the_warning()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.Track(Plan(id, pdf, false), J("lf1", "queued"));
        var u = t.MarkUnknown("lf1");
        Assert.NotNull(u);
        Assert.Equal("unknown", u!.State);
        Assert.Empty(t.Pending);
        Assert.Null(t.MarkUnknown("lf1")); // already handled
        Assert.Null(t.MarkUnknown("never-tracked"));
        Assert.Equal("unknown", Record(id).GetProperty("state").GetString());
        var req = t.RequestFor(u);
        Assert.Equal(SendKind.Suspect, req.Kind);
        Assert.Equal(pdf, req.PdfPath);
        Assert.Equal("15552345678", req.Number);

        var after = Assert.Single(new JobTracker(_dir).Scan()); // and after a restart
        Assert.Equal(SendKind.Suspect, after.Kind);
        Assert.True(File.Exists(pdf));
    }

    [Fact]
    public void Untracked_job_is_ignored() => Assert.Null(new JobTracker(_dir).OnJobUpdate(J("zzz", "sent")));

    [Fact]
    public void Discard_removes_pdf_and_json()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.Track(Plan(id, pdf, false), J("lf1", "queued"));
        t.OnJobUpdate(J("lf1", "failed"));
        t.Discard(id);
        Assert.False(File.Exists(pdf));
        Assert.False(File.Exists(JsonOf(id)));
        Assert.Empty(t.Pending);
    }

    [Fact]
    public void Scan_after_restart_retracks_offers_orphans_and_finishes_terminal_leftovers()
    {
        var t1 = new JobTracker(_dir);
        var (queued, queuedPdf) = Spooled();
        t1.Track(Plan(queued, queuedPdf, false), J("lfq", "queued"));
        var (failed, failedPdf) = Spooled();
        t1.Track(Plan(failed, failedPdf, false), J("lff", "queued"));
        t1.OnJobUpdate(J("lff", "failed"));
        var (orphan, orphanPdf) = Spooled(); // never sent: no json
        var (done, donePdf) = Spooled();
        File.WriteAllText(JsonOf(done),
            $$"""{"spoolId":"{{done}}","serverId":"lfd","number":"15552345678","subject":null,"keepLocalCopies":false,"state":"sent"}""");
        File.WriteAllText(Path.Combine(Jobs, "notaguid.pdf"), "x");

        var t2 = new JobTracker(_dir);
        var offers = t2.Scan();

        Assert.Equal("lfq", Assert.Single(t2.Pending).ServerId);
        Assert.Equal(2, offers.Count);
        var o = offers.Single(x => x.SpoolId == failed);
        Assert.Equal(SendKind.Retry, o.Kind);
        Assert.Equal("15552345678", o.Number);
        Assert.Equal("subj", o.Subject);
        var n = offers.Single(x => x.SpoolId == orphan);
        Assert.Equal(SendKind.New, n.Kind);
        Assert.Null(n.Number);
        Assert.False(File.Exists(donePdf));
        Assert.False(File.Exists(JsonOf(done)));
        Assert.True(File.Exists(Path.Combine(Jobs, "notaguid.pdf")));
        Assert.True(File.Exists(orphanPdf) && File.Exists(queuedPdf));
        Assert.Equal(TerminalOutcome.Removed, t2.OnJobUpdate(J("lfq", "sent"))!.Outcome);
    }

    [Fact]
    public void Scan_of_missing_dir_is_empty() =>
        Assert.Empty(new JobTracker(Path.Combine(_dir, "nope")).Scan());
}
