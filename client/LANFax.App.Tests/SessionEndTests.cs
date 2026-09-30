using LANFax.App.Logic;

namespace LANFax.App.Tests;

public class SessionEndTests
{
    private sealed class Probe
    {
        public bool Receiving;
        public int Stops;
        public readonly List<string> Log = [];
        public Action? OnStop;
        public bool Run(bool ending) => SessionEnd.OnEndSession(ending, () => Receiving, () => { Stops++; OnStop?.Invoke(); }, Log.Add);
    }

    [Fact]
    public void Session_ending_while_a_job_arrives_stops_the_listener_synchronously()
    {
        var p = new Probe { Receiving = true };
        p.OnStop = () => p.Receiving = false; // Stop's grace let the job finish
        Assert.True(p.Run(ending: true));
        Assert.Equal(1, p.Stops);
        Assert.Contains(p.Log, l => l.Contains("finished"));
    }

    [Fact]
    public void Session_not_ending_or_nothing_arriving_leaves_the_listener_alone()
    {
        var cancelled = new Probe { Receiving = true };
        Assert.False(cancelled.Run(ending: false)); // WM_ENDSESSION wParam 0: the session goes on
        var idle = new Probe { Receiving = false };
        Assert.False(idle.Run(ending: true));
        Assert.Equal(0, cancelled.Stops + idle.Stops);
    }

    [Fact]
    public void A_job_still_arriving_after_the_grace_or_a_failing_stop_is_logged_not_thrown()
    {
        var slow = new Probe { Receiving = true };
        Assert.True(slow.Run(ending: true));
        Assert.Contains(slow.Log, l => l.Contains("still arriving"));
        var broken = new Probe { Receiving = true, OnStop = () => throw new InvalidOperationException("boom") };
        Assert.True(broken.Run(ending: true));
        Assert.Contains(broken.Log, l => l.Contains("boom"));
    }
}
