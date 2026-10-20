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

    [Fact]
    public void NewDocuments_offers_only_unrecorded_pdfs_not_seen_before_and_remembers_them()
    {
        var t = new JobTracker(_dir);
        var (fresh, _) = Spooled();
        var (seenId, _) = Spooled();
        var (recorded, recordedPdf) = Spooled();
        File.WriteAllText(JsonOf(recorded), "{}");
        var seen = new HashSet<Guid> { seenId };
        var offers = t.NewDocuments(seen);
        var o = Assert.Single(offers);
        Assert.Equal(fresh, o.SpoolId);
        Assert.Equal(SendKind.New, o.Kind);
        Assert.Contains(fresh, seen);
        Assert.Empty(t.NewDocuments(seen)); // second call: nothing new
        Assert.True(File.Exists(recordedPdf));
    }

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
    public void BeginSend_records_the_server_authority()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.BeginSend(Plan(id, pdf, keep: false, key: "K1"), new Uri("http://10.0.0.5:9090/"));
        Assert.Equal("10.0.0.5:9090", Record(id).GetProperty("server").GetString());
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
    public void Locked_session_stays_uncertain_after_a_401()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var s = new SendSession(id, pdf);
        var p = s.Build("5552345678", null, false, false);
        t.BeginSend(p); s.Started(p); s.Failed(AttemptOutcome.Uncertain); t.RecordFailure(s, p);
        t.BeginSend(p); s.Started(p); s.Failed(new FaxApiUnauthorizedException()); t.RecordFailure(s, p);
        Assert.Equal("uncertain", Record(id).GetProperty("state").GetString());
        Assert.Equal(p.IdempotencyKey, Record(id).GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public void Locked_session_becomes_draft_after_a_definite_4xx()
    {
        // faxd looks the Idempotency-Key up before it validates or reads the body: a 4xx other than 401 means no job.
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var s = new SendSession(id, pdf);
        var p = s.Build("5552345678", null, false, false);
        t.BeginSend(p); s.Started(p); s.Failed(AttemptOutcome.Uncertain); t.RecordFailure(s, p);
        t.BeginSend(p); s.Started(p); s.Failed(new FaxApiException(413, "too big")); t.RecordFailure(s, p);
        var r = Record(id);
        Assert.Equal("draft", r.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("idempotencyKey").ValueKind);
        Assert.NotEqual(p.IdempotencyKey, s.IdempotencyKey);
        Assert.Equal(SendKind.Draft, Assert.Single(new JobTracker(_dir).Scan()).Kind);
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
            var s = SendSession.Resume(plan, null);
            s.Started(plan); s.Failed(AttemptOutcome.Uncertain);
            t1.RecordFailure(s, plan);
        }

        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.Resume, req.Kind);
        Assert.Equal(id, req.SpoolId);
        Assert.Equal(pdf, req.PdfPath);
        Assert.Equal(plan, req.LockedPlan); // record equality: same key, number, subject, route, file action
        var s2 = SendSession.For(req);
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

    // ---- re-offering a kept dialog (tray menu): the record, not the original request ----

    static readonly Uri ServerA = new("http://10.0.0.5:9090/");
    static readonly Uri ServerB = new("http://10.0.0.6:9090/");

    [Fact]
    public void Uncertain_in_this_run_then_cancel_reopens_locked_with_the_same_key_and_server()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var original = new SendRequest(id, pdf); // a fresh print job: kind New, no lock, no key, no server
        var keys = 0;
        var s = new SendSession(id, pdf, () => { keys++; return Guid.NewGuid(); });
        var p = s.Build("5552345678", "subj", false, false);
        t.BeginSend(p, ServerA); s.Started(p, ServerA); s.Failed(AttemptOutcome.Uncertain); t.RecordFailure(s, p);
        Assert.True(s.CancelKeepsDocumentFor(original.Kind)); // Cancel -> Kept

        var again = t.Reoffer(original, s);
        Assert.Equal(SendKind.Resume, again.Kind);
        Assert.Equal(p, again.LockedPlan);
        Assert.Equal("10.0.0.5:9090", again.Server);
        var reopened = SendSession.For(again);
        Assert.True(reopened.FieldsLocked);
        Assert.Equal(p.IdempotencyKey, reopened.IdempotencyKey);
        Assert.Equal("10.0.0.5:9090", reopened.LockedServer!.Authority);
        Assert.Equal(1, keys); // only the first session's key: none minted for the re-offer
    }

    [Fact]
    public void Old_version_record_pinned_during_this_run_stays_pinned_when_reopened()
    {
        var (id, pdf) = Spooled();
        var plan = Plan(id, pdf, keep: false, key: "K-old");
        new JobTracker(_dir).BeginSend(plan); // written without a server, like an older version's record
        var t = new JobTracker(_dir);
        var req = Assert.Single(t.Scan());
        var s = SendSession.For(req);
        Assert.True(s.ServerNotRecorded);
        t.BeginSend(plan, ServerA); s.Started(plan, ServerA); s.Failed(AttemptOutcome.Uncertain); t.RecordFailure(s, plan);

        var again = t.Reoffer(req, s);
        var reopened = SendSession.For(again);
        Assert.Equal("K-old", reopened.IdempotencyKey);
        Assert.False(reopened.ServerNotRecorded);
        Assert.Equal("10.0.0.5:9090", reopened.LockedServer!.Authority);
    }

    [Fact]
    public void Suspect_sent_anyway_then_uncertain_reopens_locked_with_its_key()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.Track(Plan(id, pdf, false), J("lf1", "queued"));
        var suspect = t.RequestFor(t.MarkUnknown("lf1")!);
        Assert.Equal(SendKind.Suspect, suspect.Kind);
        var s = SendSession.For(suspect);
        var p = s.Build("5552345678", null, false, false);
        t.BeginSend(p, ServerB); s.Started(p, ServerB); s.Failed(AttemptOutcome.Uncertain); t.RecordFailure(s, p);

        var reopened = SendSession.For(t.Reoffer(suspect, s));
        Assert.True(reopened.FieldsLocked);
        Assert.Equal(p.IdempotencyKey, reopened.IdempotencyKey);
        Assert.Equal("10.0.0.6:9090", reopened.LockedServer!.Authority);
    }

    [Fact]
    public void Suspect_rejected_then_cancel_keeps_the_warning()
    {
        // A 4xx for the new key proves nothing about the job the server lost: the document may still have been sent.
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.Track(Plan(id, pdf, false), J("lf1", "queued"));
        var suspect = t.RequestFor(t.MarkUnknown("lf1")!);
        var s = SendSession.For(suspect);
        var p = s.Build("5552345678", null, false, false);
        t.BeginSend(p, ServerA); s.Started(p, ServerA); s.Failed(AttemptOutcome.Rejected); t.RecordFailure(s, p);

        var again = t.Reoffer(suspect, s);
        Assert.Equal(SendKind.Suspect, again.Kind);
        Assert.Null(again.LockedPlan);
        Assert.Equal("15552345678", again.Number);
    }

    [Fact]
    public void Reoffer_without_a_readable_record_falls_back_to_the_locked_session()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var original = new SendRequest(id, pdf);
        var s = new SendSession(id, pdf);
        var p = s.Build("5552345678", null, false, false);
        s.Started(p, ServerA); s.Failed(AttemptOutcome.Uncertain); // the record write failed, say
        Assert.False(File.Exists(JsonOf(id)));

        var again = t.Reoffer(original, s);
        Assert.Equal(SendKind.Resume, again.Kind);
        Assert.Equal(p, again.LockedPlan);
        Assert.Equal("10.0.0.5:9090", again.Server);
    }

    [Fact]
    public void Compose_fax_uncertain_then_cancel_reopens_locked_on_its_documents()
    {
        // The compose window opens with no document; the record is under its first added document's id.
        var t = new JobTracker(_dir);
        var (a, aPdf) = Spooled();
        var (b, bPdf) = Spooled();
        var original = new SendRequest(Guid.NewGuid(), null, Kind: SendKind.Compose);
        var s = SendSession.For(original);
        s.SetDocuments([aPdf, bPdf]);
        var p = s.Build("5552345678", null, false, false);
        t.BeginSend(p, ServerA); s.Started(p, ServerA); s.Failed(AttemptOutcome.Uncertain); t.RecordFailure(s, p);
        Assert.True(s.CancelKeepsDocumentFor(original.Kind));

        var again = t.Reoffer(original, s);
        Assert.Equal(SendKind.Resume, again.Kind);
        Assert.Equal(p, again.LockedPlan);
        Assert.Equal(new[] { aPdf, bPdf }, again.AllPdfPaths);
        Assert.Equal(p.IdempotencyKey, SendSession.For(again).IdempotencyKey);
    }

    [Fact]
    public void Removing_the_printed_document_then_uncertain_reopens_on_what_was_sent()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var (a, aPdf) = Spooled();
        var original = new SendRequest(id, pdf);
        var s = SendSession.For(original);
        s.SetDocuments([aPdf]); // the print was removed, another document added
        var p = s.Build("5552345678", null, false, false);
        t.BeginSend(p, ServerA); s.Started(p, ServerA); s.Failed(AttemptOutcome.Uncertain); t.RecordFailure(s, p);

        var again = t.Reoffer(original, s);
        Assert.Equal(p, again.LockedPlan);
        Assert.Equal(new[] { aPdf }, again.AllPdfPaths);
    }

    [Fact]
    public void Reoffer_of_an_untouched_request_is_that_request()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        var original = new SendRequest(id, pdf, "15552345678", null, SendKind.Retry);
        Assert.Equal(original, t.Reoffer(original));
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
    public void Briefly_locked_json_is_read_after_a_retry_not_treated_as_corrupt()
    {
        var t0 = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t0.BeginSend(Plan(id, pdf, keep: false, key: "K1"));
        var hold = new FileStream(JsonOf(id), FileMode.Open, FileAccess.Read, FileShare.None);
        int reads = 0;
        // The first read hits the real sharing violation; the holder lets go right before the retry.
        var t = new JobTracker(_dir)
        {
            ReadRetryDelay = TimeSpan.Zero,
            ReadAllText = p => { if (++reads == 2) hold.Dispose(); return File.ReadAllText(p); },
        };
        try
        {
            var req = Assert.Single(t.Scan());
            Assert.Equal(2, reads);
            Assert.Equal(SendKind.Resume, req.Kind);
            Assert.Equal("K1", req.LockedPlan!.IdempotencyKey);
            Assert.False(File.Exists(JsonOf(id) + ".bad"));
        }
        finally { hold.Dispose(); }
    }

    [Fact]
    public void A_record_deleted_between_listing_and_reading_is_no_record_and_its_pdf_is_new()
    {
        var (id, pdf) = Spooled();
        new JobTracker(_dir).BeginSend(Plan(id, pdf, keep: false, key: "K1"));
        var logs = new List<string>();
        var t = new JobTracker(_dir, logs.Add) { ReadAllText = p => { File.Delete(p); return File.ReadAllText(p); } };
        var req = Assert.Single(t.Scan());
        Assert.Equal(SendKind.New, req.Kind);
        Assert.Equal(id, req.SpoolId);
        Assert.DoesNotContain(logs, l => l.Contains("skipped"));
    }

    [Fact]
    public void Only_accepted_records_are_pollable()
    {
        foreach (var state in new[] { "accepted" })
            Assert.True(new TrackedJob(Guid.NewGuid(), "lf1", "1", null, false, state).IsPollable);
        foreach (var state in new[] { "sending", "uncertain", "draft", "unknown", "sent", "failed", "cancelled", "bogus", "" })
            Assert.False(new TrackedJob(Guid.NewGuid(), "lf1", "1", null, false, state).IsPollable, state);
        Assert.False(new TrackedJob(Guid.NewGuid(), null, "1", null, false, "accepted").IsPollable);
    }

    [Fact]
    public void A_record_in_a_state_this_version_does_not_know_is_offered_with_the_warning()
    {
        var (id, _) = Spooled();
        File.WriteAllText(JsonOf(id),
            $$"""{"spoolId":"{{id}}","serverId":"lfx","number":"15552345678","subject":null,"keepLocalCopies":false,"state":"parked-by-v9"}""");
        var t = new JobTracker(_dir);
        Assert.Equal(SendKind.Suspect, Assert.Single(t.Scan()).Kind);
        Assert.Empty(t.Pending);
    }

    [Fact]
    public void Persistently_locked_json_is_skipped_this_scan_not_renamed_or_offered()
    {
        var logs = new List<string>();
        var (id, pdf) = Spooled();
        new JobTracker(_dir).BeginSend(Plan(id, pdf, keep: false, key: "K1"));
        using (new FileStream(JsonOf(id), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(new JobTracker(_dir, logs.Add) { ReadRetryDelay = TimeSpan.Zero }.Scan());
            Assert.True(File.Exists(JsonOf(id)));
            Assert.False(File.Exists(JsonOf(id) + ".bad"));
        }
        Assert.Contains(logs, l => l.Contains(id.ToString()) && l.Contains("skipped"));
        // released: the next scan resumes it normally
        Assert.Equal(SendKind.Resume, Assert.Single(new JobTracker(_dir).Scan()).Kind);
    }

    [Fact]
    public void Leftover_tmp_is_adopted_when_the_json_is_missing()
    {
        // A crash between the flushed temp write and the rename: the temp file holds the latest record.
        var (id, pdf) = Spooled();
        new JobTracker(_dir).BeginSend(Plan(id, pdf, keep: false, key: "K7"));
        File.Move(JsonOf(id), JsonOf(id) + ".tmp");
        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.Resume, req.Kind);
        Assert.Equal("K7", req.LockedPlan!.IdempotencyKey);
        Assert.True(File.Exists(JsonOf(id)));
        Assert.False(File.Exists(JsonOf(id) + ".tmp"));
    }

    [Fact]
    public void Unreadable_leftover_tmp_is_not_adopted()
    {
        var (id, _) = Spooled();
        File.WriteAllText(JsonOf(id) + ".tmp", "{\"spoolId\":"); // torn write
        var req = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.New, req.Kind); // offered as an unsent PDF, as without any record
        Assert.False(File.Exists(JsonOf(id)));
    }

    [Fact]
    public void Leftover_tmp_beside_an_existing_json_is_ignored()
    {
        var (id, pdf) = Spooled();
        var t = new JobTracker(_dir);
        t.BeginSend(Plan(id, pdf, keep: false, key: "OLD"));
        var old = File.ReadAllText(JsonOf(id));
        t.BeginSend(Plan(id, pdf, keep: false, key: "NEW"));
        File.WriteAllText(JsonOf(id) + ".tmp", old);
        Assert.Equal("NEW", Assert.Single(new JobTracker(_dir).Scan()).LockedPlan!.IdempotencyKey);
        Assert.Contains("\"NEW\"", File.ReadAllText(JsonOf(id)));
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
    // ---- multi-document faxes: the parts share the record's fate ----

    (Guid a, Guid b, SendPlan plan) TwoDocPlan(bool keep = false)
    {
        var (a, pa) = Spooled();
        var (b, pb) = Spooled();
        return (a, b, SendPlan.Create(a, new[] { pa, pb }, "5552345678", "subj", false, keep, "K-multi"));
    }

    [Fact]
    public void A_multi_document_record_names_its_parts()
    {
        var (a, b, plan) = TwoDocPlan();
        new JobTracker(_dir).BeginSend(plan);
        var rec = Record(a);
        Assert.Equal("sending", rec.GetProperty("state").GetString());
        Assert.Equal(JsonSerializer.Serialize(new[] { b }),
                     JsonSerializer.Serialize(rec.GetProperty("parts").EnumerateArray().Select(g => g.GetGuid()).ToArray()));
    }

    [Fact]
    public void Finishing_removes_or_moves_every_part()
    {
        var (a, b, plan) = TwoDocPlan(keep: true);
        var t = new JobTracker(_dir);
        t.Track(plan, J("lfmulti1", "queued"));
        t.OnJobUpdate(J("lfmulti1", "sent"));

        Assert.False(File.Exists(Path.Combine(Jobs, a + ".pdf")));
        Assert.False(File.Exists(Path.Combine(Jobs, b + ".pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "sent", a + ".pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "sent", b + ".pdf")));
        Assert.False(File.Exists(JsonOf(a)));
    }

    [Fact]
    public void Discard_deletes_every_part_even_after_a_restart()
    {
        var (a, b, plan) = TwoDocPlan();
        new JobTracker(_dir).BeginSend(plan);
        var t = new JobTracker(_dir);   // a new run reads the record for the parts
        t.Discard(a);
        Assert.False(File.Exists(Path.Combine(Jobs, a + ".pdf")));
        Assert.False(File.Exists(Path.Combine(Jobs, b + ".pdf")));
        Assert.False(File.Exists(JsonOf(a)));
    }

    [Fact]
    public void Scan_offers_the_fax_once_and_never_its_parts_alone()
    {
        var (a, b, plan) = TwoDocPlan();
        new JobTracker(_dir).RecordFailure(new SendSession(a, new[] { Path.Combine(Jobs, a + ".pdf"), Path.Combine(Jobs, b + ".pdf") }, true), plan);
        var offers = new JobTracker(_dir).Scan();
        var offer = Assert.Single(offers);
        Assert.Equal(a, offer.SpoolId);
        Assert.Equal(new[] { b }, offer.Parts);
        Assert.Equal(2, offer.AllPdfPaths.Count);
        Assert.Equal(SendKind.Draft, offer.Kind);   // editable again, with both documents
    }

    [Fact]
    public async Task An_uncertain_multi_document_failure_resumes_locked_with_all_documents()
    {
        var (a, b, plan) = TwoDocPlan();
        var session = new SendSession(a, new[] { Path.Combine(Jobs, a + ".pdf"), Path.Combine(Jobs, b + ".pdf") }, true);
        var exec = new SendExecutor(new FailingApi(), new ClientConfig { Server = "127.0.0.1", Port = 8081, Token = "t" }, new JobTracker(_dir));
        Assert.Equal(AttemptOutcome.Uncertain, (await exec.RunAsync(session, plan)).Outcome);

        var offer = Assert.Single(new JobTracker(_dir).Scan());
        Assert.Equal(SendKind.Resume, offer.Kind);
        Assert.NotNull(offer.LockedPlan);
        Assert.Equal(2, offer.LockedPlan!.PdfPaths.Count);
        Assert.Equal("K-multi", offer.LockedPlan.IdempotencyKey);
    }

    [Fact]
    public void NewDocuments_skips_a_part_of_a_fax()
    {
        var (a, b, plan) = TwoDocPlan();
        new JobTracker(_dir).BeginSend(plan);
        var seen = new HashSet<Guid>();
        Assert.Empty(new JobTracker(_dir).NewDocuments(seen));   // both files belong to the fax
    }

    /// <summary>Throws like a timeout: the attempt is uncertain and the session locks.</summary>
    sealed class FailingApi : ISendApi
    {
        public Task<Job> PostJobAsync(SendEndpoint endpoint, byte[] pdf, string to, string? subject, string idempotencyKey, CancellationToken ct = default) => throw new FaxApiException(0, "timeout");
        public Task<Job> PostJobAsync(SendEndpoint endpoint, IReadOnlyList<(byte[] Bytes, string Name)> parts, string to, string? subject, string idempotencyKey, CancellationToken ct = default) => throw new FaxApiException(0, "timeout");
        public Task<Job> SendTestPageAsync(SendEndpoint endpoint, string to, string? note, string idempotencyKey, CancellationToken ct = default) => throw new FaxApiException(0, "timeout");
        public Task<FaxCheck?> AnalyzeJobAsync(SendEndpoint endpoint, IReadOnlyList<(byte[] Bytes, string Name)> parts, CancellationToken ct = default) => Task.FromResult<FaxCheck?>(null);
        public Task<FaxPreview?> PreviewJobAsync(SendEndpoint endpoint, IReadOnlyList<(byte[] Bytes, string Name)> parts, string to, CancellationToken ct = default) => Task.FromResult<FaxPreview?>(null);
    }


}
