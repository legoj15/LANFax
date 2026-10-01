using System.Windows.Forms;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

/// <summary>
/// The monitor window built against a fake runtime on an STA thread with a message pump: it loads, takes live events,
/// hides on close and lets go of the runtime when disposed. Skipped (passes empty) without an interactive desktop.
/// </summary>
public sealed class MainFormSmokeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static Job J(string id, string dir, string state) =>
        new(id, dir, state, null, dir == "out" ? "5551234" : null, dir == "in" ? "5559876" : null, null, null, 1, null, T0, T0, null);

    [Fact]
    public void MainForm_loads_takes_events_hides_on_close_and_disposes_cleanly()
    {
        if (!Environment.UserInteractive) return; // no desktop session: nothing to show a window on

        var src = new FakeSource();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(src, cacheDir: Path.GetTempPath(), openFile: _ => { });
                form.Show();
                Pump(() => form.JobRowCount == 1 && form.InboxRowCount == 1);
                Assert.Equal(1, src.Api.FullLoads);

                src.RaiseJob(J("lf2", "out", "sending"));
                src.RaiseInbox(J("in1", "in", "received")); // already there: updated, not duplicated
                src.RaiseStatus(ServerStatus.Empty with { AmiConnected = true, Arm = new ArmState(true, DateTimeOffset.UtcNow.AddMinutes(20), 20, "300") });
                src.RaiseConnection(false);
                src.RaiseConnection(true); // a reconnect reloads with updated_since
                Pump(() => src.Api.SinceLoads == 1);
                src.RaiseResynced(T0);
                Pump(() => src.Api.SinceLoads == 2);
                Assert.Equal(2, form.JobRowCount);
                Assert.Equal(1, form.InboxRowCount);
                Assert.Equal(T0, src.Api.LastSince);

                form.Close(); // the user's close hides; the app lives in the tray
                Pump(() => !form.Visible);
                Assert.False(form.IsDisposed);
                form.Show();
                Pump(() => form.Visible);
            }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the form thread hung");
        if (failure != null) throw new Xunit.Sdk.XunitException("MainForm smoke failed: " + failure);
        Assert.Equal(0, src.Subscribers); // disposed: no handler left on the runtime
    }

    private static void Pump(Func<bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!until())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached while pumping");
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private sealed class FakeApi : IMonitorApi
    {
        public int FullLoads, SinceLoads;
        public DateTimeOffset? LastSince;

        public Task<IReadOnlyList<Job>> GetJobsAsync(string? direction = null, IEnumerable<string>? states = null, int limit = 100,
            DateTimeOffset? updatedSince = null, CancellationToken ct = default)
        {
            if (updatedSince == null) { FullLoads++; return Task.FromResult<IReadOnlyList<Job>>([J("lf1", "out", "queued")]); }
            SinceLoads++;
            LastSince = updatedSince;
            return Task.FromResult<IReadOnlyList<Job>>([J("lf1", "out", "sent") with { UpdatedAt = T0.AddMinutes(1) }]);
        }

        public Task<IReadOnlyList<Job>> GetInboxAsync(int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Job>>([J("in1", "in", "received")]);

        public Task DownloadPdfAsync(string id, string destPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ArmState> ArmAsync(int minutes, CancellationToken ct = default) => Task.FromResult(new ArmState(true, null, minutes, "300"));
        public Task<ArmState> DisarmAsync(CancellationToken ct = default) => Task.FromResult(new ArmState(false, null, 0, "300"));
        public Task<ArmState> ReceiveTestAsync(int minutes = 20, CancellationToken ct = default) => ArmAsync(minutes, ct);
    }

    private sealed class FakeSource : IMonitorSource
    {
        public readonly FakeApi Api = new();
        public IMonitorApi MonitorApi => Api;
        public bool Connected { get; private set; } = true;
        public bool EverConnected => true;
        public ServerStatus Status { get; private set; } = ServerStatus.Empty;
        public event Action<bool>? ConnectionChanged;
        public event Action<ServerStatus>? StatusChanged;
        public event Action<Job>? JobChanged;
        public event Action<Job>? InboxChanged;
        public event Action<DateTimeOffset?>? Resynced;

        public int Subscribers =>
            (ConnectionChanged?.GetInvocationList().Length ?? 0) + (StatusChanged?.GetInvocationList().Length ?? 0) +
            (JobChanged?.GetInvocationList().Length ?? 0) + (InboxChanged?.GetInvocationList().Length ?? 0) +
            (Resynced?.GetInvocationList().Length ?? 0);

        public void RaiseConnection(bool c) { Connected = c; ConnectionChanged?.Invoke(c); }
        public void RaiseStatus(ServerStatus s) { Status = s; StatusChanged?.Invoke(s); }
        public void RaiseJob(Job j) => JobChanged?.Invoke(j);
        public void RaiseInbox(Job j) => InboxChanged?.Invoke(j);
        public void RaiseResynced(DateTimeOffset? since) => Resynced?.Invoke(since);
    }
}
