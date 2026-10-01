using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

/// <summary>The monitor window's view-model: rows, badges, pill, countdown, reload cursor, PDF cache path.</summary>
public sealed class MonitorModelTests
{
    /// <summary>UTC-5 with no DST, so the local-time column is predictable on any machine.</summary>
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test-5", TimeSpan.FromHours(-5), "test-5", "test-5");
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 5, 0, TimeSpan.Zero);

    private static Job J(string id, string dir = "out", string state = "queued", DateTimeOffset? created = null,
        DateTimeOffset? updated = null, int? pages = null, string? error = null, string? to = "5551234", string? from = null) =>
        new(id, dir, state, null, dir == "out" ? to : null, from, null, null, pages, error,
            created ?? T0, updated ?? created ?? T0, null);

    private static ServerStatus Status(bool ami, params (string, string)[] regs) =>
        ServerStatus.Empty with { AmiConnected = ami, Registrations = regs };

    // ---- rows ----

    [Fact]
    public void Rows_are_formatted_culture_invariant_in_local_time()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var m = new MonitorModel(Zone);
            var r = m.Row(J("lf1", pages: 3, error: null));
            Assert.Equal("Oct 1 07:05", r.Time); // 12:05 UTC is 07:05 at UTC-5, English month whatever the culture
            Assert.Equal("5551234", r.Party);
            Assert.Equal("3", r.Pages);
            Assert.Equal("", r.Error);
            Assert.Equal("lf1", r.Id);

            var f = m.Row(J("lf2", state: "failed", pages: null, error: "busy"));
            Assert.Equal("", f.Pages);
            Assert.Equal("busy", f.Error);

            var i = m.Row(J("lf3", dir: "in", state: "received", from: "5559876", pages: 1));
            Assert.Equal("5559876", i.Party);
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void Unknown_party_shows_a_dash()
    {
        var m = new MonitorModel(Zone);
        Assert.Equal("-", m.Row(J("x", to: null)).Party);
        Assert.Equal("-", m.Row(J("y", dir: "in", state: "received", from: "")).Party);
    }

    [Theory]
    [InlineData("queued", "Queued", BadgeColor.Amber)]
    [InlineData("dialing", "Dialing", BadgeColor.Amber)]
    [InlineData("sending", "Sending", BadgeColor.Amber)]
    [InlineData("sent", "Sent", BadgeColor.Green)]
    [InlineData("failed", "Failed", BadgeColor.Red)]
    [InlineData("received", "Received", BadgeColor.Blue)]
    [InlineData("cancelled", "Cancelled", BadgeColor.Gray)]
    // the client's own record states (SendState), should one ever reach the grid
    [InlineData(SendState.Accepted, "Accepted", BadgeColor.Amber)]
    [InlineData(SendState.Uncertain, "Uncertain", BadgeColor.Amber)]
    [InlineData(SendState.Draft, "Draft", BadgeColor.Gray)]
    [InlineData(SendState.Unknown, "Unknown", BadgeColor.Gray)]
    [InlineData("weird", "weird", BadgeColor.Gray)]
    [InlineData("", "?", BadgeColor.Gray)]
    public void State_badges_mirror_the_web_page(string state, string text, BadgeColor color)
    {
        var (t, c) = MonitorModel.Badge(state);
        Assert.Equal(text, t);
        Assert.Equal(color, c);
    }

    [Fact]
    public void Every_SendState_constant_has_a_badge_of_its_own()
    {
        foreach (var f in typeof(SendState).GetFields())
        {
            var s = (string)f.GetValue(null)!;
            var (text, _) = MonitorModel.Badge(s);
            Assert.NotEqual(s, text); // mapped, not the raw fallback
        }
    }

    // ---- status pill ----

    [Fact]
    public void Pill_before_and_between_connections()
    {
        Assert.Equal(new Pill("connecting", PillTone.Neutral), MonitorModel.PillFor(false, false, ServerStatus.Empty, false));
        Assert.Equal(new Pill("reconnecting", PillTone.Bad), MonitorModel.PillFor(false, true, Status(true, ("fax", "Registered")), false));
        Assert.Equal(new Pill("connected", PillTone.Neutral), MonitorModel.PillFor(true, true, ServerStatus.Empty, false));
    }

    [Fact]
    public void Pill_for_each_server_status()
    {
        Assert.Equal(new Pill("PBX link down", PillTone.Bad), MonitorModel.PillFor(true, true, Status(false), false));
        Assert.Equal(new Pill("1 line down", PillTone.Bad),
            MonitorModel.PillFor(true, true, Status(true, ("a", "Registered"), ("b", "Unregistered")), false));
        Assert.Equal(new Pill("2 lines down", PillTone.Bad),
            MonitorModel.PillFor(true, true, Status(true, ("a", "Rejected"), ("b", "UNREGISTERED")), false));
        Assert.Equal(new Pill("degraded", PillTone.Bad), MonitorModel.PillFor(true, true, Status(false, ("a", "Registered")), false));
        Assert.Equal(new Pill("1 line registered", PillTone.Ok), MonitorModel.PillFor(true, true, Status(true, ("a", "Registered")), false));
        Assert.Equal(new Pill("2 lines registered", PillTone.Ok),
            MonitorModel.PillFor(true, true, Status(true, ("a", "Registered"), ("b", "registered")), false));
        Assert.Equal(new Pill("daemon ok", PillTone.Ok), MonitorModel.PillFor(true, true, Status(true), false));
        Assert.Equal(new Pill("daemon ok, receiving a fax", PillTone.Ok), MonitorModel.PillFor(true, true, Status(true), true));
    }

    [Fact]
    public void Receiving_expires_after_the_ttl_and_a_new_status_event_restarts_it()
    {
        var m = new MonitorModel(Zone);
        var rx = Status(true) with { Receiving = true };
        m.NoteStatus(rx, T0);
        Assert.True(m.IsReceiving(rx, T0 + TimeSpan.FromSeconds(119)));
        Assert.False(m.IsReceiving(rx, T0 + MonitorModel.ReceivingTtl)); // the PBX never says "finished"
        m.NoteStatus(rx, T0 + TimeSpan.FromMinutes(5)); // another call
        Assert.True(m.IsReceiving(rx, T0 + TimeSpan.FromMinutes(6)));
        var idle = Status(true);
        m.NoteStatus(idle, T0 + TimeSpan.FromMinutes(6));
        Assert.False(m.IsReceiving(idle, T0 + TimeSpan.FromMinutes(6)));
        Assert.False(m.IsReceiving(rx, T0 + TimeSpan.FromMinutes(6))); // the full status cleared the clock
    }

    // ---- arm card ----

    [Fact]
    public void Countdown_text_at_several_remaining_times_and_when_disarmed()
    {
        var m = new MonitorModel(Zone);
        var until = T0 + TimeSpan.FromMinutes(20);
        var a = new ArmState(true, until, 20, "300");
        Assert.Equal("ARMED", MonitorModel.ArmTitle(a));
        Assert.Equal("Receiving until 07:25 (20:00 left)", m.ArmDetail(a, T0));
        Assert.Equal("Receiving until 07:25 (04:59 left)", m.ArmDetail(a, until - TimeSpan.FromSeconds(299.4)));
        Assert.Equal("Receiving until 07:25 (00:01 left)", m.ArmDetail(a, until - TimeSpan.FromSeconds(1)));
        Assert.Equal("Receiving until 07:25 (ending)", m.ArmDetail(a, until + TimeSpan.FromSeconds(3)));

        var eight = new ArmState(true, T0 + TimeSpan.FromHours(8), 480, "300");
        Assert.Equal("Receiving until 15:05 (8:00:00 left)", m.ArmDetail(eight, T0));
        Assert.Equal("Receiving until 15:05 (1:00:01 left)", m.ArmDetail(eight, T0 + TimeSpan.FromHours(7) - TimeSpan.FromSeconds(1)));

        Assert.Equal("Receiving on ext 300", m.ArmDetail(new ArmState(true, null, 0, "300"), T0));

        var off = new ArmState(false, null, 0, "300");
        Assert.Equal("Disarmed", MonitorModel.ArmTitle(off));
        Assert.Equal("Send-only. Inbound calls go to the phones.", m.ArmDetail(off, T0));
    }

    [Fact]
    public void Expiring_balloon_fires_once_per_arm_window()
    {
        var w = new ArmExpiryWatch();
        var until = T0 + TimeSpan.FromMinutes(20);
        var a = new ArmState(true, until, 20, "300");
        Assert.Null(w.Check(a, T0)); // 20 min left
        Assert.Null(w.Check(a, until - TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1)));
        var text = w.Check(a, until - TimeSpan.FromMinutes(5));
        Assert.NotNull(text);
        Assert.Contains("5 min", text);
        Assert.Null(w.Check(a, until - TimeSpan.FromMinutes(4))); // once
        Assert.Null(w.Check(a, until - TimeSpan.FromMinutes(1)));
        Assert.Null(w.Check(new ArmState(false, null, 0, "300"), until)); // disarmed: nothing, and no re-arm
        Assert.Null(w.Check(a, until - TimeSpan.FromMinutes(1))); // same window seen again: still once

        // A new arm window (extended or armed again) may warn again.
        var next = new ArmState(true, until + TimeSpan.FromHours(1), 64, "300");
        Assert.Null(w.Check(next, until));
        Assert.NotNull(w.Check(next, next.Until!.Value - TimeSpan.FromMinutes(2)));
        Assert.Null(w.Check(next, next.Until!.Value - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Expiring_balloon_says_what_its_click_does()
    {
        // The tray wires the click to ShowMonitor: it opens the window, where an arm button extends the window.
        var text = NotificationText.ArmExpiring(T0 + TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(4));
        Assert.EndsWith("Click here to open LANFax and extend it.", text);
        Assert.DoesNotContain("keep it on", text);
    }

    [Fact]
    public void Expiring_balloon_ignores_windows_without_an_end_or_already_over()
    {
        var w = new ArmExpiryWatch();
        Assert.Null(w.Check(new ArmState(true, null, 0, "300"), T0));
        Assert.Null(w.Check(new ArmState(true, T0, 0, "300"), T0 + TimeSpan.FromSeconds(1)));
        Assert.Null(w.Check(new ArmState(false, T0 + TimeSpan.FromMinutes(2), 0, "300"), T0));
    }

    // ---- reload cursor ----

    [Fact]
    public void Reload_since_is_the_last_successful_load_less_the_skew_margin_or_the_resync_since()
    {
        var m = new MonitorModel(Zone);
        Assert.Null(m.ReloadSince(null)); // never loaded: a full load
        var resync = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.FromHours(2));
        Assert.Null(m.ReloadSince(resync)); // a resync before any load still needs the full load

        m.ApplyLoad([], T0);
        Assert.Equal(T0, m.LastLoad);
        Assert.Equal(T0 - MonitorModel.SkewMargin, m.ReloadSince(null));
        Assert.Equal(resync, m.ReloadSince(resync)); // the server's own cursor wins

        Assert.Equal("2026-09-30T08:00:00Z", MonitorModel.FormatSince(resync));
        var old = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
        try { Assert.Equal("2026-10-01T12:05:00Z", MonitorModel.FormatSince(T0)); }
        finally { Thread.CurrentThread.CurrentCulture = old; }

        m.Clear(); // another server: start over
        Assert.Null(m.LastLoad);
        Assert.Empty(m.Jobs);
        Assert.Null(m.ReloadSince(resync)); // and a resync then is a full load too
    }

    // ---- live rows ----

    [Fact]
    public void A_received_event_inserts_then_updates_one_inbox_row()
    {
        var m = new MonitorModel(Zone);
        Assert.True(m.Apply(J("in1", dir: "in", state: "received", from: "555", pages: null)));
        Assert.True(m.Apply(J("in1", dir: "in", state: "received", from: "555", pages: 2, updated: T0.AddSeconds(5))));
        var row = Assert.Single(m.Inbox);
        Assert.Equal("2", row.Pages);
        Assert.Empty(m.Jobs);
    }

    [Fact]
    public void Jobs_and_inbox_are_routed_by_direction_and_sorted_newest_first()
    {
        var m = new MonitorModel(Zone);
        m.Apply(J("a", created: T0));
        m.Apply(J("b", created: T0.AddMinutes(1)));
        m.Apply(J("c", dir: "in", state: "received", created: T0.AddMinutes(2)));
        Assert.Equal(["b", "a"], m.Jobs.Select(r => r.Id));
        Assert.Equal(["c"], m.Inbox.Select(r => r.Id));
    }

    [Fact]
    public void An_older_update_never_overwrites_a_newer_row()
    {
        var m = new MonitorModel(Zone);
        m.Apply(J("a", state: "sent", updated: T0.AddMinutes(2)));
        Assert.False(m.Apply(J("a", state: "dialing", updated: T0.AddMinutes(1)))); // a slow reload arriving late
        Assert.Equal("Sent", Assert.Single(m.Jobs).Badge);
    }

    [Fact]
    public void At_equal_updated_at_a_less_advanced_state_never_overwrites_a_more_advanced_one()
    {
        // faxd stamps updated_at to the second, so a same-second queued->failed is one timestamp; the tray re-applies the
        // POST's queued row after the dialog closes, which may land after the stream's failed.
        var m = new MonitorModel(Zone);
        m.Apply(J("a", state: "failed", updated: T0, error: "busy"));
        Assert.False(m.Apply(J("a", state: "queued", updated: T0)));
        Assert.Equal("Failed", Assert.Single(m.Jobs).Badge);

        // The forward direction at the same second (stream in order: queued, then dialing) still applies.
        m.Apply(J("b", state: "queued", created: T0.AddMinutes(1)));
        Assert.True(m.Apply(J("b", state: "dialing", created: T0.AddMinutes(1), updated: T0.AddMinutes(1))));
        Assert.Equal("Dialing", m.Jobs.Single(r => r.Id == "b").Badge);

        // Same state, same second (a reload replaying the row): a no-op, nothing to redraw.
        Assert.False(m.Apply(J("b", state: "dialing", created: T0.AddMinutes(1), updated: T0.AddMinutes(1))));
    }

    [Fact]
    public void At_equal_updated_at_the_same_state_with_new_fields_still_applies()
    {
        // faxd's ami.py _annotate publishes a job event with pages and detail but no state change, and spool.py
        // publishes received with finished_at; both can share the second of the row already shown.
        var m = new MonitorModel(Zone);
        m.Apply(J("a", state: "sending", updated: T0));
        Assert.True(m.Apply(J("a", state: "sending", updated: T0, pages: 3)));
        Assert.Equal("3", Assert.Single(m.Jobs).Pages);

        m.Apply(J("in1", dir: "in", state: "received", from: "555", updated: T0));
        var done = J("in1", dir: "in", state: "received", from: "555", updated: T0) with { FinishedAt = T0 };
        Assert.True(m.Apply(done));
    }

    [Fact]
    public void Reconnect_reload_merges_rows_by_id()
    {
        var m = new MonitorModel(Zone);
        m.ApplyLoad([J("a", state: "queued"), J("b", state: "sending", created: T0.AddMinutes(1)),
            J("i1", dir: "in", state: "received")], T0);
        Assert.Equal(2, m.Jobs.Count);

        // Since-reload after a reconnect: b finished, c is new, a did not change (not in the answer, row kept).
        m.ApplyLoad([J("b", state: "sent", created: T0.AddMinutes(1), updated: T0.AddMinutes(3)),
            J("c", state: "queued", created: T0.AddMinutes(4)), J("i1", dir: "in", state: "received")], T0.AddMinutes(5));
        Assert.Equal(["c", "b", "a"], m.Jobs.Select(r => r.Id));
        Assert.Equal("Sent", m.Jobs[1].Badge);
        Assert.Single(m.Inbox);
        Assert.Equal(T0.AddMinutes(5), m.LastLoad);
    }

    [Fact]
    public void Rows_are_capped_dropping_the_oldest()
    {
        var m = new MonitorModel(Zone);
        for (int i = 0; i < MonitorModel.Capacity + 5; i++) m.Apply(J("j" + i, created: T0.AddMinutes(i)));
        Assert.Equal(MonitorModel.Capacity, m.Jobs.Count);
        Assert.Equal("j" + (MonitorModel.Capacity + 4), m.Jobs[0].Id);
        Assert.DoesNotContain(m.Jobs, r => r.Id == "j0");
    }

    // ---- PDF cache ----

    [Fact]
    public void Pdf_cache_path_is_the_id_under_the_cache_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lanfax-cache");
        Assert.Equal(Path.Combine(dir, "lf_9a8b7c6d5e4f.pdf"), MonitorModel.PdfCachePath(dir, "lf_9a8b7c6d5e4f"));
        Assert.EndsWith(Path.Combine("LANFax", "cache"), MonitorModel.DefaultCacheDir);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\..\\evil")]
    [InlineData("../x")]
    [InlineData("C:\\x")]
    [InlineData("a/b")]
    [InlineData("a.pdf")]
    [InlineData("")]
    [InlineData("con:")]
    [InlineData("a b")]
    public void A_hostile_id_never_leaves_the_cache_dir(string id)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lanfax-cache");
        Assert.Throws<ArgumentException>(() => MonitorModel.PdfCachePath(dir, id));
    }

    [Fact]
    public void A_too_long_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => MonitorModel.PdfCachePath(Path.GetTempPath(), new string('a', 65)));
    }
}
