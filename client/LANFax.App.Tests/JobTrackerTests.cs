using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

public sealed class JobTrackerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-jt-" + Guid.NewGuid().ToString("N"));
    string Jobs => Path.Combine(_dir, "jobs");

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

    static SendPlan Plan(Guid id, string pdf, bool keep) =>
        SendPlan.Create(id, pdf, "5552345678", "subj", false, keep, Guid.NewGuid().ToString());

    [Fact]
    public void Track_writes_json_beside_pdf_and_keeps_pdf_until_terminal()
    {
        var t = new JobTracker(_dir);
        var (id, pdf) = Spooled();
        t.Track(Plan(id, pdf, false), J("lf1", "queued"));
        Assert.True(File.Exists(Path.Combine(Jobs, id + ".json")));
        Assert.True(File.Exists(pdf));
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
        Assert.NotNull(r);
        Assert.Equal(TerminalOutcome.Removed, r!.Outcome);
        Assert.False(File.Exists(pdf));
        Assert.False(File.Exists(Path.Combine(Jobs, id + ".json")));
        Assert.Empty(t.Pending);
        Assert.Null(t.OnJobUpdate(J("lf1", "sent"))); // replayed event: already handled
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
        Assert.Null(t.OnJobUpdate(J("lf1", "failed"))); // replay
        // Retry with a new server job id replaces the entry.
        t.Track(Plan(id, pdf, false), J("lf2", "queued"));
        Assert.Single(t.Pending);
        Assert.Equal(TerminalOutcome.Removed, t.OnJobUpdate(J("lf2", "sent"))!.Outcome);
        Assert.False(File.Exists(pdf));
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
        Assert.False(File.Exists(Path.Combine(Jobs, id + ".json")));
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
        // A terminal state whose file action did not happen (e.g. the app died mid-way).
        var (done, donePdf) = Spooled();
        File.WriteAllText(Path.Combine(Jobs, done + ".json"),
            $$"""{"spoolId":"{{done}}","serverId":"lfd","number":"15552345678","subject":null,"keepLocalCopies":false,"state":"sent"}""");
        // Unreadable json: its pdf is offered as unsent.
        var (bad, badPdf) = Spooled();
        File.WriteAllText(Path.Combine(Jobs, bad + ".json"), "{not json");
        File.WriteAllText(Path.Combine(Jobs, "notaguid.pdf"), "x"); // not ours: untouched

        var t2 = new JobTracker(_dir);
        var orphans = t2.Scan();

        var pending = Assert.Single(t2.Pending);
        Assert.Equal("lfq", pending.ServerId);
        Assert.Equal(3, orphans.Count);
        var o = orphans.Single(x => x.SpoolId == failed);
        Assert.Equal("15552345678", o.Number); // prefilled for the retry
        Assert.Equal("subj", o.Subject);
        Assert.Contains(orphans, x => x.SpoolId == orphan && x.Number == null);
        Assert.Contains(orphans, x => x.SpoolId == bad && x.PdfPath == badPdf);
        Assert.False(File.Exists(donePdf));
        Assert.False(File.Exists(Path.Combine(Jobs, done + ".json")));
        Assert.True(File.Exists(Path.Combine(Jobs, "notaguid.pdf")));
        Assert.True(File.Exists(orphanPdf) && File.Exists(queuedPdf));
        Assert.Equal(TerminalOutcome.Removed, t2.OnJobUpdate(J("lfq", "sent"))!.Outcome);
    }

    [Fact]
    public void Scan_of_missing_dir_is_empty() =>
        Assert.Empty(new JobTracker(Path.Combine(_dir, "nope")).Scan());
}
