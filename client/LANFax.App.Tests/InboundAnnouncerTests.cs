using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

/// <summary>Review finding 4: inbound faxes missed while disconnected are announced once, never twice.</summary>
public sealed class InboundAnnouncerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-in-" + Guid.NewGuid().ToString("N"));
    string State => Path.Combine(_dir, "announced.json");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static readonly DateTimeOffset T0 = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    static Job In(string id, int minute, string state = "received", string direction = "in") =>
        new(id, direction, state, null, null, "15551230000", null, "inbound", 1, null, T0.AddMinutes(minute), T0.AddMinutes(minute), T0.AddMinutes(minute));

    /// <summary>An announcer whose baseline is already taken (an empty inbox), as after the first run.</summary>
    InboundAnnouncer Established()
    {
        var a = new InboundAnnouncer(State);
        Assert.Empty(a.Absorb([], asBaseline: true));
        return a;
    }

    [Fact]
    public void A_live_fax_is_announced_once()
    {
        var a = Established();
        Assert.True(a.TryAnnounce(In("in1", 1)));
        Assert.False(a.TryAnnounce(In("in1", 1)));
        Assert.Empty(a.Absorb([In("in1", 1)], asBaseline: false));
    }

    [Fact]
    public void Catch_up_announces_only_the_missed_ones_oldest_first()
    {
        var a = Established();
        a.TryAnnounce(In("in1", 1));
        var news = a.Absorb([In("in4", 4), In("in1", 1), In("in3", 3)], asBaseline: false);
        Assert.Equal(new[] { "in3", "in4" }, news.Select(j => j.Id));
        Assert.Equal(T0.AddMinutes(4), a.LastSeen);
    }

    [Fact]
    public void Only_received_inbound_faxes_count()
    {
        var a = Established();
        a.TryAnnounce(In("in1", 1));
        Assert.False(a.TryAnnounce(In("lf1", 2, "sent", "out")));
        Assert.False(a.TryAnnounce(In("in2", 2, "failed")));
        Assert.Empty(a.Absorb([In("lf2", 3, "queued", "out"), In("in3", 3, "failed")], asBaseline: false));
    }

    [Fact]
    public void A_restart_does_not_announce_again()
    {
        var a = Established();
        a.TryAnnounce(In("in1", 1));
        a.Absorb([In("in2", 2)], asBaseline: false);

        var b = new InboundAnnouncer(State);
        Assert.Equal(T0.AddMinutes(2), b.LastSeen);
        Assert.False(b.NeedsBaseline);
        Assert.Empty(b.Absorb([In("in1", 1), In("in2", 2)], asBaseline: false));
        Assert.Equal(new[] { "in9" }, b.Absorb([In("in2", 2), In("in9", 9)], asBaseline: false).Select(j => j.Id));
    }

    [Fact]
    public void The_first_run_takes_the_existing_inbox_as_a_silent_baseline()
    {
        var a = new InboundAnnouncer(State);
        Assert.Null(a.LastSeen);
        Assert.True(a.NeedsBaseline);
        Assert.Empty(a.Absorb([In("old1", 1), In("old2", 2)], asBaseline: true));
        Assert.False(a.NeedsBaseline);
        Assert.Equal(new[] { "new3" }, a.Absorb([In("old2", 2), In("new3", 3)], asBaseline: false).Select(j => j.Id));
    }

    [Fact]
    public void An_empty_inbox_on_the_first_run_still_ends_the_baseline_and_survives_a_restart()
    {
        var clock = T0.AddHours(1);
        var a = new InboundAnnouncer(State, now: () => clock);
        Assert.Empty(a.Absorb([], asBaseline: true));
        Assert.False(a.NeedsBaseline);
        Assert.Equal(clock, a.BaselineAt);

        var b = new InboundAnnouncer(State);
        Assert.False(b.NeedsBaseline);
        Assert.Equal(clock, b.BaselineAt);
        Assert.Equal(new[] { "in1" }, b.Absorb([In("in1", 70)], asBaseline: false).Select(j => j.Id));
    }

    [Fact]
    public void A_live_fax_ends_the_baseline()
    {
        // The balloon showed and LastSeen moved to it: a later catch-up from there is news, even after a restart
        // (a baseline then would silently swallow faxes received while the app was down).
        var a = new InboundAnnouncer(State);
        Assert.True(a.TryAnnounce(In("live", 5)));
        Assert.False(a.NeedsBaseline);
        Assert.NotNull(a.BaselineAt);
        var b = new InboundAnnouncer(State);
        Assert.False(b.NeedsBaseline);
        Assert.Equal(new[] { "later" }, b.Absorb([In("live", 5), In("later", 9)], asBaseline: b.NeedsBaseline).Select(j => j.Id));
    }

    [Fact]
    public void A_live_event_that_is_not_announced_leaves_the_baseline_pending()
    {
        var a = new InboundAnnouncer(State);
        Assert.False(a.TryAnnounce(In("lf1", 5, "sent", "out")));
        Assert.True(a.NeedsBaseline);
    }

    [Fact]
    public void A_state_file_from_an_older_version_counts_as_a_taken_baseline()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(State, """{"ids":["in1"],"lastSeen":"2026-09-29T12:01:00+00:00"}""");
        var a = new InboundAnnouncer(State);
        Assert.False(a.NeedsBaseline);
        Assert.Equal(new[] { "in2" }, a.Absorb([In("in1", 1), In("in2", 2)], asBaseline: false).Select(j => j.Id));
    }

    [Fact]
    public void The_set_is_bounded_and_keeps_the_newest()
    {
        var a = new InboundAnnouncer(State);
        for (int i = 0; i < InboundAnnouncer.Capacity + 50; i++) a.TryAnnounce(In("in" + i, i));
        Assert.Equal(InboundAnnouncer.Capacity, a.Count);

        var b = new InboundAnnouncer(State);
        Assert.Equal(InboundAnnouncer.Capacity, b.Count);
        Assert.False(b.TryAnnounce(In("in" + (InboundAnnouncer.Capacity + 49), 0)));
        Assert.True(b.TryAnnounce(In("in0", 0))); // the oldest fell out
    }

    [Fact]
    public void Since_is_the_requested_time_else_the_last_seen_one()
    {
        var a = new InboundAnnouncer(State);
        Assert.Null(a.SinceFor(null));
        a.TryAnnounce(In("in1", 5));
        Assert.Equal(T0.AddMinutes(5), a.SinceFor(null));
        Assert.Equal(T0, a.SinceFor(T0));
    }

    [Fact]
    public void A_corrupt_state_file_starts_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(State, "{ not json");
        var logs = new List<string>();
        var a = new InboundAnnouncer(State, logs.Add);
        Assert.Equal(0, a.Count);
        Assert.True(a.NeedsBaseline);
        Assert.Single(logs);
        Assert.True(a.TryAnnounce(In("in1", 1)));
    }

    // ---- the catch-up run: baseline decided up front, widened when a page is full, coalesced ----

    /// <summary>A faxd stand-in: <c>GET /jobs</c> returns the newest <c>limit</c> of the inbox, newest first.</summary>
    sealed class FakeInbox
    {
        public readonly List<Job> Jobs = new();
        public readonly List<(DateTimeOffset? Since, int Limit)> Calls = new();
        public Func<Task>? Gate;
        public Action? DuringFetch;

        public async Task<IReadOnlyList<Job>> Fetch(DateTimeOffset? since, int limit)
        {
            Calls.Add((since, limit));
            DuringFetch?.Invoke();
            if (Gate != null) await Gate();
            return Jobs.Where(j => since == null || j.UpdatedAt >= since)
                .OrderByDescending(j => j.UpdatedAt).Take(limit).ToList();
        }
    }

    [Fact]
    public async Task A_live_fax_during_the_first_request_does_not_turn_the_inbox_into_news()
    {
        var inbox = new FakeInbox();
        for (int i = 0; i < 10; i++) inbox.Jobs.Add(In("old" + i, i));
        var a = new InboundAnnouncer(State);
        inbox.DuringFetch = () => { inbox.Jobs.Add(In("live", 50)); Assert.True(a.TryAnnounce(In("live", 50))); };
        var news = new List<Job>();
        await new InboundCatchUp(a, inbox.Fetch, news.AddRange).RunAsync(null);
        Assert.Empty(news);
        Assert.False(a.NeedsBaseline);
    }

    [Fact]
    public async Task A_full_page_is_fetched_again_with_the_larger_limit()
    {
        var inbox = new FakeInbox();
        var a = Established();
        for (int i = 0; i < 250; i++) inbox.Jobs.Add(In("in" + i, i));
        var news = new List<Job>();
        await new InboundCatchUp(a, inbox.Fetch, news.AddRange).RunAsync(null);
        Assert.Equal(new[] { InboundCatchUp.PageSize, InboundCatchUp.MaxJobs }, inbox.Calls.Select(c => c.Limit));
        Assert.Equal(250, news.Count);
        Assert.Equal("in0", news[0].Id); // oldest first
    }

    [Fact]
    public async Task The_catch_up_is_capped()
    {
        var inbox = new FakeInbox();
        var a = Established();
        for (int i = 0; i < 1500; i++) inbox.Jobs.Add(In("in" + i, i));
        var news = new List<Job>();
        var logs = new List<string>();
        await new InboundCatchUp(a, inbox.Fetch, news.AddRange, log: logs.Add).RunAsync(null);
        Assert.Equal(2, inbox.Calls.Count);
        Assert.Equal(InboundCatchUp.MaxJobs, news.Count);
        Assert.Equal("in1499", news[^1].Id); // the newest are kept
        Assert.Contains(logs, l => l.Contains("1000"));
    }

    [Fact]
    public async Task A_short_page_needs_one_request()
    {
        var inbox = new FakeInbox();
        var a = Established();
        inbox.Jobs.Add(In("in1", 1));
        await new InboundCatchUp(a, inbox.Fetch, _ => { }).RunAsync(null);
        Assert.Single(inbox.Calls);
    }

    [Fact]
    public async Task A_catch_up_requested_while_one_runs_queues_exactly_one_more()
    {
        var inbox = new FakeInbox();
        var a = Established();
        var release = new TaskCompletionSource();
        inbox.Gate = () => release.Task;
        var news = new List<Job>();
        var c = new InboundCatchUp(a, inbox.Fetch, news.AddRange);
        var first = c.RunAsync(null);
        Assert.True(c.Running);
        inbox.Jobs.Add(In("in7", 7)); // arrives while the first request is out
        var second = c.RunAsync(T0);
        var third = c.RunAsync(null);
        Assert.True(second.IsCompleted && third.IsCompleted); // merged into the running one, not dropped
        inbox.Gate = null;
        release.SetResult();
        await first;
        Assert.Equal(2, inbox.Calls.Count);
        Assert.Equal(T0, inbox.Calls[1].Since); // the explicit resync time wins
        Assert.Equal(new[] { "in7" }, news.Select(j => j.Id));
        Assert.False(c.Running);
    }

    [Fact]
    public async Task A_queued_reconnect_with_nothing_seen_yet_wins_over_a_queued_resync()
    {
        // No fax seen yet: "since the newest seen" means everything, which reaches further back than any resync time.
        var inbox = new FakeInbox();
        var a = Established();
        Assert.Null(a.LastSeen);
        var release = new TaskCompletionSource();
        inbox.Gate = () => release.Task;
        var c = new InboundCatchUp(a, inbox.Fetch, _ => { });
        var first = c.RunAsync(null);
        _ = c.RunAsync(T0);
        _ = c.RunAsync(null);
        inbox.Gate = null;
        release.SetResult();
        await first;
        Assert.Equal(2, inbox.Calls.Count);
        Assert.Null(inbox.Calls[1].Since);
    }

    [Fact]
    public async Task A_failed_catch_up_is_reported_and_leaves_the_baseline_pending()
    {
        var a = new InboundAnnouncer(State);
        var errors = new List<Exception>();
        var c = new InboundCatchUp(a, (_, _) => throw new FaxApiConfigException("bad token"), _ => { }, errors.Add);
        await c.RunAsync(null);
        Assert.IsType<FaxApiConfigException>(Assert.Single(errors));
        Assert.True(a.NeedsBaseline);
        Assert.False(c.Running);
    }

    [Theory]
    [InlineData(1, "Fax received from 15551230000")]
    [InlineData(4, "4 faxes were received while LANFax was not connected")]
    public void Catch_up_notices(int n, string first)
    {
        var jobs = Enumerable.Range(0, n).Select(i => In("in" + i, i)).ToList();
        Assert.Equal(first, NotificationText.ForCaughtUp(jobs).First());
        Assert.True(NotificationText.ForCaughtUp(jobs).Count <= 3);
    }
}

public class InstanceGuardTests
{
    [Theory]
    [InlineData(true, false, InstanceRole.Run)]
    [InlineData(false, true, InstanceRole.ShowRunningHere)]
    [InlineData(false, false, InstanceRole.RunningInAnotherSession)]
    public void Decides_what_a_starting_instance_does(bool ownsMutex, bool signalledLocal, InstanceRole expected) =>
        Assert.Equal(expected, InstanceGuard.Decide(ownsMutex, signalledLocal));

    [Theory]
    [InlineData(false, false, ShowSignalAction.ShowWindow)]
    [InlineData(false, true, ShowSignalAction.ExitPendingBalloon)] // waiting for a send before exiting: say so
    [InlineData(true, false, ShowSignalAction.Ignore)]
    [InlineData(true, true, ShowSignalAction.Ignore)]
    public void Decides_what_the_running_tray_does_with_a_show_signal(bool exiting, bool exitPending, ShowSignalAction expected) =>
        Assert.Equal(expected, InstanceGuard.OnShowSignal(exiting, exitPending));

    static (string Mutex, string Event) Names()
    {
        var id = Guid.NewGuid().ToString("N");
        return (@"Local\LANFax.Test.M." + id, @"Local\LANFax.Test.E." + id);
    }

    [Fact]
    public void The_owner_has_its_show_signal_as_soon_as_it_owns_the_mutex()
    {
        var (m, e) = Names();
        using var owner = InstanceGuard.Acquire(m, e, TimeSpan.Zero);
        Assert.Equal(InstanceRole.Run, owner.Role);
        Assert.NotNull(owner.ShowSignal);
        using var second = InstanceGuard.Acquire(m, e, TimeSpan.Zero);
        Assert.Equal(InstanceRole.ShowRunningHere, second.Role);
        Assert.Null(second.ShowSignal);
        Assert.True(owner.ShowSignal!.WaitOne(0)); // the running tray was asked to show itself
    }

    [Fact]
    public void A_second_start_during_the_owners_startup_is_not_reported_as_another_session()
    {
        // The owner holds the mutex but its signal does not exist yet; the second start waits for it (within its
        // patience) rather than concluding that another Windows session runs LANFax.
        var (_, e) = Names();
        EventWaitHandle? late = null;
        int tries = 0;
        var role = InstanceGuard.Resolve(ownsMutex: false, trySignal: () =>
        {
            if (++tries == 3) late = new EventWaitHandle(false, EventResetMode.AutoReset, e);
            return InstanceGuard.TrySignal(e);
        }, patience: TimeSpan.FromSeconds(5), sleep: _ => { });
        using (late)
        {
            Assert.Equal(InstanceRole.ShowRunningHere, role);
            Assert.Equal(3, tries);
        }
    }

    [Fact]
    public void A_second_start_during_the_owners_shutdown_becomes_the_owner()
    {
        // The owner's signal is already gone and its mutex is released on the third try: the second start takes the
        // mutex inside its patience instead of exiting with "another session".
        int owns = 0, sleeps = 0;
        var role = InstanceGuard.Resolve(tryOwn: () => ++owns == 3, trySignal: () => false,
            patience: TimeSpan.FromSeconds(2), sleep: _ => sleeps++);
        Assert.Equal(InstanceRole.Run, role);
        Assert.Equal(3, owns);
        Assert.Equal(2, sleeps);
    }

    [Fact]
    public void A_mutex_that_stays_owned_elsewhere_is_another_session_after_the_patience()
    {
        int owns = 0;
        var slept = TimeSpan.Zero;
        var role = InstanceGuard.Resolve(tryOwn: () => { owns++; return false; }, trySignal: () => false,
            patience: TimeSpan.FromSeconds(2), sleep: d => slept += d);
        Assert.Equal(InstanceRole.RunningInAnotherSession, role);
        Assert.True(owns > 1);
        Assert.Equal(TimeSpan.FromSeconds(2), slept);
    }

    [Fact]
    public void A_mutex_released_by_its_owner_is_taken_by_the_next_acquire()
    {
        var (m, e) = Names();
        var owner = InstanceGuard.Acquire(m, e, TimeSpan.Zero);
        Assert.Equal(InstanceRole.Run, owner.Role);
        owner.Dispose();
        using var next = InstanceGuard.Acquire(m, e, TimeSpan.Zero);
        Assert.Equal(InstanceRole.Run, next.Role);
    }

    [Fact]
    public void Without_a_signal_in_this_session_the_owner_is_in_another_session()
    {
        var (_, e) = Names();
        int tries = 0;
        var role = InstanceGuard.Resolve(false, () => { tries++; return InstanceGuard.TrySignal(e); }, TimeSpan.FromSeconds(1), _ => { });
        Assert.Equal(InstanceRole.RunningInAnotherSession, role);
        Assert.True(tries > 1);
    }
}
