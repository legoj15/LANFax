using System.Windows.Forms;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

/// <summary>
/// The monitor window built against a fake runtime on an STA thread with a message pump: it loads, takes live events,
/// hides on close and lets go of the runtime when disposed. Reported as skipped without an interactive desktop.
/// </summary>
public sealed class MainFormSmokeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static Job J(string id, string dir, string state) =>
        new(id, dir, state, null, dir == "out" ? "5551234" : null, dir == "in" ? "5559876" : null, null, null, 1, null, T0, T0, null);

    [InteractiveFact]
    public void MainForm_shows_the_runtimes_rows_takes_events_hides_on_close_and_disposes_cleanly()
    {
        var src = new FakeSource();
        src.FailLoad(new FaxApiException(0, "cannot reach faxd")); // before the window existed
        int settingsOpened = 0;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(src, cacheDir: Path.GetTempPath(), openFile: _ => { }, openSettings: () => settingsOpened++);
                form.Show();
                Assert.StartsWith("Could not load the job lists", form.MessageText); // a window opened later still says so
                src.Load(J("lf1", "out", "queued"), J("in1", "in", "received")); // the runtime's next load
                Pump(() => form.JobRowCount == 1 && form.InboxRowCount == 1);
                Assert.DoesNotContain("Could not load", form.MessageText);

                src.RaiseJob(J("lf2", "out", "sending"));
                src.RaiseJob(J("in1", "in", "received")); // already there: not duplicated
                src.RaiseStatus(ServerStatus.Empty with { AmiConnected = true, Arm = new ArmState(true, DateTimeOffset.UtcNow.AddMinutes(20), 20, "300") });
                Assert.Equal("ARMED", form.ArmTitleText);
                src.RaiseConnection(false);
                src.RaiseConnection(true);
                src.Load(J("lf1", "out", "sent") with { UpdatedAt = T0.AddMinutes(1) });
                Assert.Equal(2, form.JobRowCount);
                Assert.Equal(1, form.InboxRowCount);
                Assert.Equal("Sent", form.JobsGrid.Rows.Cast<DataGridViewRow>().Single(r => (string?)r.Tag == "lf1").Cells[2].Value);

                // A refused token on an arm call opens Settings.
                src.Api.ArmError = new FaxApiUnauthorizedException();
                Find<Button>(form, b => b.Text == "Arm 1 h").PerformClick();
                Pump(() => settingsOpened == 1);
                src.Api.ArmError = null;

                // A small window, so 40-odd rows overflow the grid whatever the display's size.
                form.WindowState = FormWindowState.Normal;
                form.Size = form.MinimumSize;
                for (int i = 0; i < 40; i++) src.RaiseJob(J("lfs" + i, "out", "queued") with { CreatedAt = T0.AddMinutes(i + 1) });
                var grid = form.JobsGrid;
                Assert.True(grid.DisplayedRowCount(false) < grid.Rows.Count - 12, "the grid must overflow for the scroll checks");

                // At the top (waiting for a fax), a new fax shows at the top.
                grid.FirstDisplayedScrollingRowIndex = 0;
                src.RaiseJob(J("lftop", "out", "queued") with { CreatedAt = T0.AddHours(4) });
                Assert.Equal(0, grid.FirstDisplayedScrollingRowIndex);
                Assert.Equal("lftop", grid.Rows[0].Tag);

                // Scrolled down, a new fax at the top keeps the view on the same rows (anchored by id, not by index).
                grid.FirstDisplayedScrollingRowIndex = 10;
                var anchor = grid.Rows[grid.FirstDisplayedScrollingRowIndex].Tag;
                src.RaiseJob(J("lfnew", "out", "queued") with { CreatedAt = T0.AddHours(5) });
                Assert.Equal(44, form.JobRowCount);
                Assert.Equal(anchor, grid.Rows[grid.FirstDisplayedScrollingRowIndex].Tag);

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

    [InteractiveFact]
    public void A_server_change_drops_the_old_servers_arm_answer_even_one_still_in_flight()
    {
        var src = new FakeSource();
        src.Load(J("lf1", "out", "queued"));
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(src, cacheDir: Path.GetTempPath(), openFile: _ => { }, openSettings: () => { });
                form.Show();
                Pump(() => form.JobRowCount == 1);
                Assert.Equal("Disarmed", form.ArmTitleText);

                // The HTTP answer goes into the runtime's status before the stream brings any.
                Find<Button>(form, b => b.Text == "Arm 1 h").PerformClick();
                Pump(() => form.ArmTitleText == "ARMED");
                Assert.Equal(1, src.NoteArmCalls);
                // Settings saved to another server: the runtime's reset clears the noted arm state and the rows.
                src.ServerChanged();
                Assert.Equal("Disarmed", form.ArmTitleText);
                Assert.Equal(0, form.JobRowCount);

                // An arm call still in flight across the save: the old server's answer is not noted.
                var gate = new TaskCompletionSource<ArmState>();
                src.Api.ArmGate = gate;
                var arm = Find<Button>(form, b => b.Text == "Arm 1 h");
                arm.PerformClick();
                src.ServerChanged();
                gate.SetResult(new ArmState(true, null, 60, "300"));
                Pump(() => arm.Enabled);
                Assert.Equal("Disarmed", form.ArmTitleText);
                Assert.Equal(1, src.NoteArmCalls);
            }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the form thread hung");
        if (failure != null) throw new Xunit.Sdk.XunitException("MainForm arm reset failed: " + failure);
    }

    [InteractiveFact]
    public void A_load_failure_after_the_window_exists_is_shown_and_the_next_good_load_brings_the_hint_back()
    {
        var src = new FakeSource();
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(src, cacheDir: Path.GetTempPath(), openFile: _ => { }, openSettings: () => { });
                form.Show();
                Assert.DoesNotContain("Could not load", form.MessageText);

                src.FailLoad(new FaxApiException(0, "cannot reach faxd")); // the runtime's load failed while the window is open
                Assert.StartsWith("Could not load the job lists", form.MessageText);

                src.Load(J("lf1", "out", "queued")); // a later load succeeds: the error line goes
                Pump(() => form.JobRowCount == 1);
                Assert.DoesNotContain("Could not load", form.MessageText);
                Assert.StartsWith("Double-click", form.MessageText);
            }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "the form thread hung");
        if (failure != null) throw new Xunit.Sdk.XunitException("MainForm late load failure failed: " + failure);
    }

    private static T Find<T>(Control root, Func<T, bool> match) where T : Control =>
        All(root).OfType<T>().FirstOrDefault(match) ?? throw new InvalidOperationException("control not found");

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
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

    /// <summary>The window loads nothing itself (the runtime does): only its arm buttons and PDF opening call this.</summary>
    private sealed class FakeApi : IMonitorApi
    {
        public Exception? ArmError;
        public TaskCompletionSource<ArmState>? ArmGate;

        public Task<IReadOnlyList<Job>> GetJobsAsync(string? direction = null, IEnumerable<string>? states = null, int limit = 100,
            DateTimeOffset? updatedSince = null, CancellationToken ct = default) => throw new NotSupportedException("the window must not load");

        public Task<IReadOnlyList<Job>> GetInboxAsync(int limit = 50, CancellationToken ct = default) =>
            throw new NotSupportedException("the window must not load");

        public Task DownloadPdfAsync(string id, string destPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ArmState> ArmAsync(int minutes, CancellationToken ct = default) =>
            ArmError is { } e ? Task.FromException<ArmState>(e)
            : ArmGate is { } g ? g.Task
            : Task.FromResult(new ArmState(true, null, minutes, "300"));
        public Task<ArmState> DisarmAsync(CancellationToken ct = default) => Task.FromResult(new ArmState(false, null, 0, "300"));
        public Task<ArmState> ReceiveTestAsync(int minutes = 20, CancellationToken ct = default) => ArmAsync(minutes, ct);
    }

    /// <summary>The runtime as the window sees it: the test plays its loads, live updates, arm notes and restarts.</summary>
    private sealed class FakeSource : IMonitorSource
    {
        public readonly FakeApi Api = new();
        public IMonitorApi MonitorApi => Api;
        public bool Connected { get; private set; } = true;
        public bool EverConnected => true;
        public ServerStatus Status { get; private set; } = ServerStatus.Empty;
        public MonitorModel Monitor { get; } = new(TimeZoneInfo.Local);
        public Exception? MonitorLoadError { get; private set; }
        public int ServerGeneration { get; private set; }
        public int NoteArmCalls;
        public event Action<bool>? ConnectionChanged;
        public event Action<ServerStatus>? StatusChanged;
        public event Action? MonitorChanged;
        public event Action<Exception>? MonitorLoadFailed;

        public int Subscribers =>
            (ConnectionChanged?.GetInvocationList().Length ?? 0) + (StatusChanged?.GetInvocationList().Length ?? 0) +
            (MonitorChanged?.GetInvocationList().Length ?? 0) + (MonitorLoadFailed?.GetInvocationList().Length ?? 0);

        public void NoteArm(ArmState answer)
        {
            NoteArmCalls++;
            if (answer == Status.Arm) return;
            Status = Status with { Arm = answer };
            StatusChanged?.Invoke(Status);
        }

        public void Load(params Job[] jobs)
        {
            Monitor.ApplyLoad(jobs, DateTimeOffset.UtcNow);
            MonitorLoadError = null;
            MonitorChanged?.Invoke();
        }

        public void FailLoad(Exception e) { MonitorLoadError = e; MonitorLoadFailed?.Invoke(e); }
        public void RaiseConnection(bool c) { Connected = c; ConnectionChanged?.Invoke(c); }
        public void RaiseStatus(ServerStatus s) { Status = s; StatusChanged?.Invoke(s); }
        public void RaiseJob(Job j) { if (Monitor.Apply(j)) MonitorChanged?.Invoke(); }

        /// <summary>As <see cref="ClientRuntime.RestartStream"/>: generation bumped, status reset, rows cleared.</summary>
        public void ServerChanged()
        {
            ServerGeneration++;
            if (!ReferenceEquals(Status, ServerStatus.Empty)) RaiseStatus(ServerStatus.Empty);
            Monitor.Clear();
            MonitorLoadError = null;
            MonitorChanged?.Invoke();
        }
    }
}

/// <summary>A fact that needs a desktop session to show a window on; reported as skipped (not passed) without one.</summary>
public sealed class InteractiveFactAttribute : FactAttribute
{
    public InteractiveFactAttribute()
    {
        if (!Environment.UserInteractive) Skip = "needs an interactive desktop session to show a window";
    }
}
