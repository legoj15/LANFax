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

    [Fact]
    public void A_live_fax_is_announced_once()
    {
        var a = new InboundAnnouncer(State);
        Assert.True(a.TryAnnounce(In("in1", 1)));
        Assert.False(a.TryAnnounce(In("in1", 1)));
        Assert.Empty(a.Absorb([In("in1", 1)]));
    }

    [Fact]
    public void Catch_up_announces_only_the_missed_ones_oldest_first()
    {
        var a = new InboundAnnouncer(State);
        a.TryAnnounce(In("in1", 1));
        var news = a.Absorb([In("in4", 4), In("in1", 1), In("in3", 3)]);
        Assert.Equal(new[] { "in3", "in4" }, news.Select(j => j.Id));
        Assert.Equal(T0.AddMinutes(4), a.LastSeen);
    }

    [Fact]
    public void Only_received_inbound_faxes_count()
    {
        var a = new InboundAnnouncer(State);
        a.TryAnnounce(In("in1", 1));
        Assert.False(a.TryAnnounce(In("lf1", 2, "sent", "out")));
        Assert.False(a.TryAnnounce(In("in2", 2, "failed")));
        Assert.Empty(a.Absorb([In("lf2", 3, "queued", "out"), In("in3", 3, "failed")]));
    }

    [Fact]
    public void A_restart_does_not_announce_again()
    {
        var a = new InboundAnnouncer(State);
        a.TryAnnounce(In("in1", 1));
        a.Absorb([In("in2", 2)]);

        var b = new InboundAnnouncer(State);
        Assert.Equal(T0.AddMinutes(2), b.LastSeen);
        Assert.Empty(b.Absorb([In("in1", 1), In("in2", 2)]));
        Assert.Equal(new[] { "in9" }, b.Absorb([In("in2", 2), In("in9", 9)]).Select(j => j.Id));
    }

    [Fact]
    public void The_first_run_takes_the_existing_inbox_as_a_silent_baseline()
    {
        var a = new InboundAnnouncer(State);
        Assert.Null(a.LastSeen);
        Assert.Empty(a.Absorb([In("old1", 1), In("old2", 2)]));
        Assert.Equal(new[] { "new3" }, a.Absorb([In("old2", 2), In("new3", 3)]).Select(j => j.Id));
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
        Assert.Single(logs);
        Assert.True(a.TryAnnounce(In("in1", 1)));
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
    [Fact]
    public void The_guard_is_machine_wide() => Assert.StartsWith(@"Global\", InstanceGuard.MutexName);

    [Theory]
    [InlineData(true, false, InstanceRole.Run)]
    [InlineData(false, true, InstanceRole.ShowRunningHere)]
    [InlineData(false, false, InstanceRole.RunningInAnotherSession)]
    public void Decides_what_a_starting_instance_does(bool ownsMutex, bool signalledLocal, InstanceRole expected) =>
        Assert.Equal(expected, InstanceGuard.Decide(ownsMutex, signalledLocal));

    [Fact]
    public void Other_session_text() =>
        Assert.Equal("LANFax is already running in another session", InstanceGuard.OtherSessionText);
}
