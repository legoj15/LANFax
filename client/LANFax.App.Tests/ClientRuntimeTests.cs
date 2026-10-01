using System.Collections.Concurrent;
using System.Net;
using System.Text;
using LANFax.App.Logic;
using LANFax.Core;
using LANFax.Tests;

namespace LANFax.App.Tests;

/// <summary>
/// ClientRuntime against the in-process faxd fake and a fake print listener. The runtime runs on <see cref="OwnerThread"/>,
/// a dedicated thread with its own SynchronizationContext, as the tray's UI thread is in the app.
/// </summary>
public sealed class ClientRuntimeTests : IDisposable
{
    private readonly FakeServer _srv = new();
    private readonly OwnerThread _owner = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-rt-" + Guid.NewGuid().ToString("N"));
    private readonly FakeListener _listener = new();
    private readonly FakeKeys _keys = new();
    private readonly List<string> _log = new();
    private readonly CancellationTokenSource _done = new();
    private int _eventsConnections;
    private int _activeRuns, _maxActiveRuns, _runsEntered;
    private ClientRuntime? _rt;

    /// <summary>Per /events connection (0, 1, ...): what the fake server does with it.</summary>
    private Func<int, HttpListenerContext, RecordedRequest, Task> _events;

    public ClientRuntimeTests()
    {
        Directory.CreateDirectory(_dir);
        _events = async (_, ctx, _) => { Open(ctx); await Write(ctx, ": hello\n\n"); await Hold(ctx); };
        _srv.Handler = async (ctx, rec) =>
        {
            var path = rec.PathAndQuery;
            if (path.StartsWith("/events"))
            {
                int n = Interlocked.Increment(ref _eventsConnections) - 1;
                await _events(n, ctx, rec);
            }
            else if (path.StartsWith("/jobs?")) await FakeServer.Json(ctx, """{"jobs":[]}""");
            else if (path.StartsWith("/jobs/")) await FakeServer.Json(ctx, JobJson(path["/jobs/".Length..], "sent"));
            else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
        };
    }

    public void Dispose()
    {
        _done.Cancel();
        try { _owner.Invoke(() => _rt?.Dispose()); } catch { }
        _owner.Dispose();
        _srv.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
        Assert.Empty(_owner.Errors);
    }

    /// <summary>The runtime's clock (the stuck-job window); tests move it.</summary>
    private DateTime _now = DateTime.UtcNow;

    private ClientRuntime Make(TimeSpan? poll = null, TimeSpan? printerCheck = null, PrinterSetup? printer = null,
        IReadOnlyList<QueuedJob>? queue = null)
    {
        var cfg = new ClientConfig
        {
            Server = "127.0.0.1", Port = _srv.Port, Token = "tok123", SpoolDir = Path.Combine(_dir, "spool"),
        };
        _rt = new ClientRuntime(cfg, _owner.Context)
        {
            Listener = _listener,
            Printer = printer ?? new PrinterSetup(_keys),
            UtcNow = () => _now,
            AnnouncedPath = Path.Combine(_dir, "announced.json"),
            ListPrintQueue = _ => queue ?? [],
            PollInterval = poll ?? TimeSpan.FromHours(1),
            PrinterCheckInterval = printerCheck ?? TimeSpan.FromHours(1),
            Info = m => { lock (_log) _log.Add(m); },
            Error = (m, e) => { lock (_log) _log.Add("ERROR " + m + (e == null ? "" : ": " + e.Message)); },
            RunStream = async (es, ct) =>
            {
                Interlocked.Increment(ref _runsEntered);
                int now = Interlocked.Increment(ref _activeRuns);
                lock (_log) _maxActiveRuns = Math.Max(_maxActiveRuns, now);
                es.Delay = (_, c) => Task.Delay(20, c); // reconnect at once (the real backoff starts at 1 s)
                try { await es.StartAsync(ct); }
                finally { Interlocked.Decrement(ref _activeRuns); }
            },
        };
        return _rt;
    }

    // ---- (a) one run at a time; a cancelled run's late events are dropped; LastEventId survives ----

    [Fact]
    public async Task Restarts_never_overlap_two_stream_runs()
    {
        var rt = Make();
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => rt.Connected);
        for (int i = 0; i < 5; i++) _owner.Invoke(rt.RestartStream);
        await WaitFor(() => rt.Connected && Volatile.Read(ref _activeRuns) == 1);
        Assert.True(Volatile.Read(ref _runsEntered) >= 2);
        Assert.Equal(1, _maxActiveRuns);
    }

    [Fact]
    public async Task A_cancelled_runs_late_events_are_dropped_and_LastEventId_survives_the_restart()
    {
        var sendStale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? resumedFrom = null;
        _events = async (n, ctx, rec) =>
        {
            Open(ctx);
            if (n == 0)
            {
                await Write(ctx, ": hello\n\n");
                await sendStale.Task;
                await Write(ctx, "id: 7\nevent: status\ndata: {\"receiving\":true,\"queue\":{\"queued\":9}}\n\n");
            }
            else
            {
                resumedFrom = rec.Headers["Last-Event-ID"];
                await Write(ctx, "id: 8\nevent: status\ndata: {\"queue\":{\"queued\":1}}\n\n");
            }
            await Hold(ctx);
        };
        var rt = Make();
        var seen = new ConcurrentQueue<ServerStatus>();
        var connections = new ConcurrentQueue<bool>();
        rt.StatusChanged += s => seen.Enqueue(s);
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => rt.Connected);
        await Settle();

        // On the owner thread, as a settings save is: the stale event is raised (queued behind this action) and then
        // the run is cancelled before the queued action gets to run.
        _owner.Invoke(() =>
        {
            int before = _owner.Pending;
            sendStale.SetResult();
            var end = DateTime.UtcNow.AddSeconds(8);
            while (_owner.Pending <= before && DateTime.UtcNow < end) Thread.Sleep(5);
            Assert.True(_owner.Pending > before, "the stale status event never reached the owner queue");
            rt.ConnectionChanged += c => connections.Enqueue(c);
            rt.RestartStream();
        });

        await WaitFor(() => seen.Any(s => s.Queue.GetValueOrDefault("queued") == 1));
        Assert.DoesNotContain(seen, s => s.Queue.GetValueOrDefault("queued") == 9);
        var status = _owner.Invoke(() => rt.Status);
        Assert.False(status.Receiving);
        Assert.Equal(1, status.Queue["queued"]);
        Assert.Equal("7", resumedFrom); // the one EventStream kept LastEventId across the restart
        Assert.False(connections.First()); // the restart itself reports "not connected" first
    }

    // ---- (b) connect and resync trigger the inbound catch-up and the tracked-job poll ----

    [Fact]
    public async Task Connect_and_resync_run_the_inbound_catch_up_and_the_tracked_job_poll()
    {
        var sendResync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _events = async (n, ctx, _) =>
        {
            Open(ctx);
            await Write(ctx, ": hello\n\n");
            if (n == 0)
            {
                await sendResync.Task;
                await Write(ctx, "event: resync\ndata: {\"since\":\"2026-09-30T10:00:00+00:00\"}\n\n");
            }
            await Hold(ctx);
        };
        var rt = Make();
        var finished = new ConcurrentQueue<(Job Job, TerminalResult? Tracked)>();
        rt.JobFinished += (j, r) => finished.Enqueue((j, r));
        Assert.Same(rt.Api, ((IMonitorSource)rt).MonitorApi); // the monitor window's view of the runtime
        var resyncs = new ConcurrentQueue<DateTimeOffset?>();
        rt.Resynced += since => resyncs.Enqueue(since); // the monitor's grids reload from here
        _owner.Invoke(() => rt.TrackSubmitted("lf1"));
        _owner.Invoke(() => rt.Start());

        await WaitFor(() => finished.Any(f => f.Job.Id == "lf1"));
        await WaitFor(() => CatchUps().Length == 1);
        Assert.Null(finished.Single().Tracked); // a test page: not in the tracker
        Assert.DoesNotContain("updated_since", CatchUps()[0]); // nothing seen yet: the silent baseline asks for all

        _owner.Invoke(() => rt.TrackSubmitted("lf2"));
        sendResync.SetResult();
        await WaitFor(() => finished.Any(f => f.Job.Id == "lf2"));
        await WaitFor(() => CatchUps().Length == 2);
        Assert.Contains("updated_since=2026-09-30T10", Uri.UnescapeDataString(CatchUps()[1]));
        Assert.Single(_srv.Requests, r => r.PathAndQuery == "/jobs/lf1"); // finished: not polled again
        Assert.Single(_srv.Requests, r => r.PathAndQuery == "/jobs/lf2");
        Assert.Equal(1, _eventsConnections); // a resync reloads; it does not reconnect
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), Assert.Single(resyncs));
    }

    // ---- (c) the snapshot follows the stream and is there for a late reader ----

    [Fact]
    public async Task Snapshot_follows_stream_events_and_is_readable_without_having_subscribed()
    {
        _events = async (_, ctx, _) =>
        {
            Open(ctx);
            await Write(ctx, "event: status\ndata: {\"daemon\":{\"ami_connected\":true},\"registrations\":[{\"name\":\"fax\",\"status\":\"Registered\"}],\"queue\":{\"queued\":2},\"receiving\":false,\"arm\":{\"armed\":false,\"until\":null,\"remaining_min\":0,\"ext\":\"300\"}}\n\n");
            await Write(ctx, "event: arm\ndata: {\"armed\":true,\"until\":\"2026-10-01T12:00:00+00:00\",\"remaining_min\":20,\"ext\":\"300\"}\n\n");
            await Write(ctx, "event: status\ndata: {\"receiving\":true}\n\n");
            await Hold(ctx);
        };
        var rt = Make();
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => _owner.Invoke(() => rt.Status.Receiving));

        // A window opened now (it never saw an event) reads the current state.
        var (connected, status, arm) = _owner.Invoke(() => (rt.Connected, rt.Status, rt.Arm));
        Assert.True(connected);
        Assert.True(status.AmiConnected);
        Assert.Equal(("fax", "Registered"), Assert.Single(status.Registrations));
        Assert.Equal(2, status.Queue["queued"]);
        Assert.True(arm.Armed);
        Assert.Equal(20, arm.RemainingMin);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), arm.Until);
        Assert.Equal(arm, status.Arm);
    }

    [Fact]
    public async Task Status_and_arm_changes_are_raised_on_the_owner_thread()
    {
        _events = async (_, ctx, _) =>
        {
            Open(ctx);
            await Write(ctx, "event: arm\ndata: {\"armed\":true,\"until\":null,\"remaining_min\":5,\"ext\":\"300\"}\n\n");
            await Hold(ctx);
        };
        var rt = Make();
        var threads = new ConcurrentQueue<int>();
        var arms = new ConcurrentQueue<ArmState>();
        rt.ArmChanged += a => { threads.Enqueue(Environment.CurrentManagedThreadId); arms.Enqueue(a); };
        rt.ConnectionChanged += _ => threads.Enqueue(Environment.CurrentManagedThreadId);
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => !arms.IsEmpty);
        Assert.True(arms.Single().Armed);
        Assert.All(threads, t => Assert.Equal(_owner.ThreadId, t));
    }

    // ---- (d) stop ordering ----

    [Fact]
    public async Task StopAsync_stops_the_listener_awaits_the_stream_kills_the_timers_and_raises_nothing_after()
    {
        _srv.Handler = WithJobState("sending", _srv.Handler); // never terminal: polled on every tick
        var rt = Make(poll: TimeSpan.FromMilliseconds(80), printerCheck: TimeSpan.FromMilliseconds(80));
        int raisedAfterStop = 0;
        bool stopping = false;
        void After() { if (stopping) Interlocked.Increment(ref raisedAfterStop); }
        rt.ConnectionChanged += _ => After();
        rt.StatusChanged += _ => After();
        rt.ArmChanged += _ => After();
        rt.JobFinished += (_, _) => After();
        rt.SendOffered += _ => After();
        rt.ReceivingChanged += _ => After();
        rt.Notice += _ => After();
        rt.Unauthorized += After;
        _owner.Invoke(() => rt.TrackSubmitted("lf1"));
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => rt.Connected);
        await WaitFor(() => Polls() >= 3 && _keys.Reads >= 3); // both timers tick

        await _owner.InvokeAsync(() => { stopping = true; return rt.StopAsync(); });
        Assert.Equal(1, _listener.StopCalls);
        Assert.Equal(0, Volatile.Read(ref _activeRuns)); // the stream run was cancelled and awaited

        int polls = Polls(), reads = _keys.Reads, connections = _eventsConnections;
        await Task.Delay(500);
        Assert.Equal(polls, Polls());
        Assert.Equal(reads, _keys.Reads);
        Assert.Equal(connections, _eventsConnections);

        _listener.RaiseNewPrintJob();
        _listener.RaiseReceiving(true);
        _listener.RaiseError();
        _owner.Invoke(() => rt.ApplyJobUpdate(Job("lf1", "sent")));
        _listener.IsRunning = false;
        _owner.Invoke(rt.CheckNow);
        _owner.Drain();
        Assert.Equal(0, raisedAfterStop);
        Assert.Equal(1, _listener.StartCalls); // a stop on purpose is not a listener to restart
    }

    [Fact]
    public void Session_end_stop_is_on_purpose_so_the_check_does_not_restart_the_listener()
    {
        var rt = Make();
        var notices = new List<string>();
        rt.Notice += notices.Add;
        _owner.Invoke(() => rt.Start());
        _owner.Invoke(rt.StopListener);
        Assert.Equal(1, _listener.StopCalls);
        _owner.Invoke(rt.CheckNow);
        Assert.Equal(1, _listener.StartCalls);
        Assert.Empty(notices);
    }

    [Fact]
    public void A_listener_that_stopped_by_itself_is_restarted_and_announced_once()
    {
        var rt = Make();
        var notices = new List<string>();
        rt.Notice += notices.Add;
        _owner.Invoke(() => rt.Start());
        _listener.IsRunning = false;
        _owner.Invoke(rt.CheckNow);
        Assert.Equal(2, _listener.StartCalls);
        _listener.IsRunning = false;
        _owner.Invoke(rt.CheckNow);
        Assert.Equal(3, _listener.StartCalls);
        Assert.Single(notices, n => n.Contains("LAN Fax"));
    }

    [Fact]
    public void Receiving_changes_follow_the_listener_and_a_stale_start_is_dropped()
    {
        var rt = Make();
        var seen = new List<bool>();
        rt.ReceivingChanged += seen.Add;
        _owner.Invoke(() => rt.Start());
        _listener.IsReceiving = true;
        _listener.RaiseReceiving(true);
        _owner.Drain();
        _listener.IsReceiving = false;
        _listener.RaiseReceiving(true); // already over by the time it is handled: no block
        _listener.RaiseReceiving(false);
        _owner.Drain();
        Assert.Equal([true, false], seen);
        Assert.False(_owner.Invoke(() => rt.IsReceiving));
    }

    [Fact]
    public void A_new_print_job_is_offered_for_sending()
    {
        var rt = Make();
        var offered = new List<SendRequest>();
        rt.SendOffered += offered.Add;
        _owner.Invoke(() => rt.Start());
        var id = _listener.RaiseNewPrintJob();
        _owner.Drain();
        Assert.Equal(id, Assert.Single(offered).SpoolId);
    }

    // ---- (e) pass-through events for the MainForm grids ----

    [Fact]
    public async Task Every_job_and_received_event_is_passed_through_before_the_tracker_and_the_announcer()
    {
        _srv.Handler = WithJobState("sending", _srv.Handler); // the connect's poll: not terminal
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _events = async (_, ctx, _) =>
        {
            Open(ctx);
            await Write(ctx, ": hello\n\n");
            await go.Task;
            await Write(ctx, "event: job\ndata: " + JobJson("lf5", "queued") + "\n\n");
            await Write(ctx, "event: job\ndata: " + JobJson("lf5", "sent") + "\n\n");
            await Write(ctx, "event: received\ndata: " + InJson("in1") + "\n\n");
            await Write(ctx, "event: received\ndata: " + InJson("in1") + "\n\n");
            await Hold(ctx);
        };
        var rt = Make();
        var order = new ConcurrentQueue<string>();
        rt.JobChanged += j => order.Enqueue($"changed {j.Id} {j.State}");
        rt.JobFinished += (j, _) => order.Enqueue($"finished {j.Id}");
        rt.InboxChanged += j => order.Enqueue($"inbox {j.Id}");
        rt.FaxReceived += j => order.Enqueue($"announced {j.Id}");
        _owner.Invoke(() => rt.TrackSubmitted("lf5"));
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => order.Contains("changed lf5 sending")); // the connect's poll, passed through too
        _owner.Drain();
        order.Clear();
        go.SetResult();
        await WaitFor(() => order.Count(o => o.StartsWith("inbox")) == 2);
        _owner.Drain();
        Assert.Equal(["changed lf5 queued", "changed lf5 sent", "finished lf5", "inbox in1", "announced in1", "inbox in1"], order);
    }

    [Fact]
    public async Task A_polled_job_is_passed_through_too()
    {
        _srv.Handler = WithJobState("sending", _srv.Handler);
        var rt = Make();
        var changed = new ConcurrentQueue<Job>();
        rt.JobChanged += changed.Enqueue;
        _owner.Invoke(() => rt.Start());
        _owner.Invoke(() => rt.TrackSubmitted("lf1"));
        await _owner.InvokeAsync(rt.PollTrackedAsync); // or the connect's poll, already running, asks
        await WaitFor(() => changed.Any(j => j.Id == "lf1" && j.State == "sending"));
    }

    // ---- (f) arm and snapshot ----

    [Fact]
    public async Task ArmChanged_is_raised_only_when_the_arm_state_changes()
    {
        const string arm = "{\"armed\":true,\"until\":null,\"remaining_min\":5,\"ext\":\"300\"}";
        _events = async (_, ctx, _) =>
        {
            Open(ctx);
            await Write(ctx, "event: arm\ndata: " + arm + "\n\n");
            await Write(ctx, "event: arm\ndata: " + arm + "\n\n");
            await Write(ctx, "event: status\ndata: {\"receiving\":true,\"arm\":" + arm + "}\n\n");
            await Hold(ctx);
        };
        var rt = Make();
        int arms = 0;
        rt.ArmChanged += _ => arms++;
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => _owner.Invoke(() => rt.Status.Receiving));
        Assert.Equal(1, _owner.Invoke(() => arms));
    }

    [Fact]
    public async Task ArmChanged_is_raised_after_the_snapshot_moved_so_the_trays_expiry_check_reads_the_new_window()
    {
        // TrayContext.CheckArmExpiry reads rt.Arm (not the event argument), on ArmChanged and on its timer.
        _events = async (_, ctx, _) =>
        {
            Open(ctx);
            await Write(ctx, "event: arm\ndata: {\"armed\":true,\"until\":\"2026-10-01T12:04:00+00:00\",\"remaining_min\":4,\"ext\":\"300\"}\n\n");
            await Hold(ctx);
        };
        var rt = Make();
        var watch = new ArmExpiryWatch();
        var balloons = new ConcurrentQueue<string>();
        rt.ArmChanged += a =>
        {
            Assert.Equal(a, rt.Arm);
            if (watch.Check(rt.Arm, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)) is { } text) balloons.Enqueue(text);
        };
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => !balloons.IsEmpty);
        Assert.Single(balloons);
    }

    [Fact]
    public async Task A_restart_clears_the_previous_servers_snapshot()
    {
        _events = async (n, ctx, _) =>
        {
            Open(ctx);
            if (n == 0) await Write(ctx, "event: status\ndata: {\"registrations\":[{\"name\":\"fax\",\"status\":\"Registered\"}],\"arm\":{\"armed\":true,\"until\":null,\"remaining_min\":5,\"ext\":\"300\"}}\n\n");
            else await Write(ctx, ": hello\n\n");
            await Hold(ctx);
        };
        var rt = Make();
        var statuses = new ConcurrentQueue<ServerStatus>();
        var arms = new ConcurrentQueue<ArmState>();
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => _owner.Invoke(() => rt.Status.Registrations.Count) == 1);
        var after = _owner.Invoke(() =>
        {
            rt.StatusChanged += statuses.Enqueue;
            rt.ArmChanged += arms.Enqueue;
            rt.RestartStream();
            return rt.Status;
        });
        Assert.Same(ServerStatus.Empty, after);
        Assert.Same(ServerStatus.Empty, Assert.Single(statuses));
        Assert.False(Assert.Single(arms).Armed);
    }

    // ---- (g) unauthorized ----

    [Fact]
    public async Task Unauthorized_is_raised_once_until_the_settings_are_saved()
    {
        _events = (_, ctx, _) => { ctx.Response.StatusCode = 401; ctx.Response.Close(); return Task.CompletedTask; };
        var rt = Make();
        int raised = 0;
        rt.Unauthorized += () => raised++;
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => Volatile.Read(ref _eventsConnections) >= 4); // refused again and again
        _owner.Drain();
        Assert.Equal(1, _owner.Invoke(() => raised));

        int before = Volatile.Read(ref _eventsConnections);
        _owner.Invoke(rt.SettingsSaved);
        await WaitFor(() => Volatile.Read(ref _eventsConnections) >= before + 3);
        _owner.Drain();
        Assert.Equal(2, _owner.Invoke(() => raised));
    }

    // ---- (h) a job the server lost ----

    [Fact]
    public async Task A_poll_404_offers_the_document_again_once()
    {
        var inner = _srv.Handler;
        _srv.Handler = (ctx, rec) =>
        {
            if (rec.PathAndQuery == "/jobs/lf9") { ctx.Response.StatusCode = 404; ctx.Response.Close(); return Task.CompletedTask; }
            return inner(ctx, rec);
        };
        var rt = Make();
        var offered = new ConcurrentQueue<SendRequest>();
        rt.SendOffered += offered.Enqueue;
        _owner.Invoke(() => rt.Start());
        var id = Guid.NewGuid();
        _owner.Invoke(() =>
        {
            var pdf = rt.Tracker.PdfPath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(pdf)!);
            File.WriteAllText(pdf, "%PDF-1.4");
            rt.Tracker.Track(SendPlan.Create(id, pdf, "5552345678", null, false, false, "k1"), Job("lf9", "queued"));
        });
        await _owner.InvokeAsync(rt.PollTrackedAsync); // may find the connect's poll running: then that one asks
        await WaitFor(() => !offered.IsEmpty);
        await _owner.InvokeAsync(rt.PollTrackedAsync); // no longer pollable: not offered twice
        await Task.Delay(100);
        _owner.Drain();
        var req = Assert.Single(offered);
        Assert.Equal(id, req.SpoolId);
        Assert.Equal(SendKind.Suspect, req.Kind);
    }

    // ---- (i) printer and print queue checks ----

    [Fact]
    public void CheckNow_reports_each_printer_change_once()
    {
        var rt = Make();
        var problems = new List<PrinterStatus>();
        rt.PrinterProblem += problems.Add;
        _owner.Invoke(() => rt.Start());
        _keys.PortValue = "LPT1:";
        _owner.Invoke(rt.CheckNow);
        _owner.Invoke(rt.CheckNow);
        _keys.PortValue = null;
        _owner.Invoke(rt.CheckNow);
        Assert.Equal([PrinterState.Misconfigured, PrinterState.Missing], problems.Select(p => p.State));
    }

    [Fact]
    public async Task CheckNow_reports_documents_stuck_since_before_the_start_once_inside_the_window()
    {
        var start = _now;
        IReadOnlyList<QueuedJob> queue = [new QueuedJob(5, PrintQueueJobs.JobStatusError, start.AddMinutes(-1), "doc", "me")];
        var rt = Make(queue: queue);
        var stuck = new ConcurrentQueue<int>();
        rt.StuckPrintJobs += stuck.Enqueue;
        _owner.Invoke(() => rt.Start());

        _now = start + ClientRuntime.StuckCheckDelay - TimeSpan.FromSeconds(1); // too early: the spooler still retries
        _owner.Invoke(rt.CheckNow);
        await Task.Delay(100);
        _owner.Drain();
        Assert.Empty(stuck);

        _now = start + ClientRuntime.StuckCheckDelay;
        _owner.Invoke(rt.CheckNow);
        await WaitFor(() => !stuck.IsEmpty);
        Assert.Equal(1, Assert.Single(stuck));

        _owner.Invoke(rt.CheckNow); // seen: not again
        await Task.Delay(100);
        _owner.Drain();
        Assert.Single(stuck);
    }

    // ---- (j) install ----

    [Fact]
    public async Task Install_reports_its_result_before_offering_what_reached_jobs()
    {
        var rt = Make(printer: new PrinterSetup(_keys, (_, _) => Task.FromResult(0)));
        var order = new ConcurrentQueue<string>();
        rt.SendOffered += r => order.Enqueue("offer " + r.SpoolId);
        _owner.Invoke(() => rt.Start());
        var id = PlantJobsPdf();
        var result = await _owner.InvokeAsync(() => rt.InstallPrinterAsync(_ => order.Enqueue("report")));
        Assert.NotNull(result);
        Assert.Equal(["report", "offer " + id], order);
        Assert.False(_owner.Invoke(() => rt.Installing));
    }

    [Fact]
    public async Task Install_still_offers_what_reached_jobs_when_the_report_throws()
    {
        var rt = Make(printer: new PrinterSetup(_keys, (_, _) => Task.FromResult(0)));
        var offered = new ConcurrentQueue<SendRequest>();
        rt.SendOffered += offered.Enqueue;
        _owner.Invoke(() => rt.Start());
        var id = PlantJobsPdf();
        var result = await _owner.InvokeAsync(() => rt.InstallPrinterAsync(_ => throw new InvalidOperationException("balloon")));
        Assert.NotNull(result);
        Assert.Equal(id, Assert.Single(offered).SpoolId);
        lock (_log) Assert.Contains(_log, l => l.StartsWith("ERROR") && l.Contains("balloon"));
    }

    // ---- (k) threading contract and disposal ----

    [Fact]
    public void Start_off_the_owner_context_is_refused()
    {
        var rt = Make();
        Assert.Throws<InvalidOperationException>(() => rt.Start()); // the test thread has no owner context
    }

    [Fact]
    public async Task Dispose_without_StopAsync_ends_the_stream_run_before_the_clients_go()
    {
        var rt = Make();
        _owner.Invoke(() => rt.Start());
        await WaitFor(() => rt.Connected);
        _owner.Invoke(rt.Dispose);
        Assert.Equal(0, Volatile.Read(ref _activeRuns));
        Assert.True(_listener.StopCalls >= 1);
        await Task.Delay(200);
        lock (_log) Assert.DoesNotContain(_log, l => l.StartsWith("ERROR"));
    }

    [Fact]
    public void Dispose_stops_an_injected_listener_but_leaves_its_disposal_to_its_owner()
    {
        var rt = Make();
        _owner.Invoke(() => rt.Start());
        _owner.Invoke(rt.Dispose);
        Assert.Equal(0, _listener.DisposeCalls);
        Assert.False(_listener.IsRunning);
    }

    // ---- helpers ----

    private string[] CatchUps() => _srv.Requests.Select(r => r.PathAndQuery)
        .Where(p => p.StartsWith("/jobs?direction=in")).ToArray();

    private int Polls() => _srv.Requests.Count(r => r.PathAndQuery == "/jobs/lf1");

    private Func<HttpListenerContext, RecordedRequest, Task> WithJobState(string state, Func<HttpListenerContext, RecordedRequest, Task> inner) =>
        (ctx, rec) => rec.PathAndQuery.StartsWith("/jobs/")
            ? FakeServer.Json(ctx, JobJson(rec.PathAndQuery["/jobs/".Length..], state))
            : inner(ctx, rec);

    /// <summary>Lets the connect's poll and catch-up finish so later owner-queue counts only see what a test sends.</summary>
    private async Task Settle()
    {
        await WaitFor(() => CatchUps().Length >= 1);
        await Task.Delay(150);
        _owner.Drain();
    }

    private static Job Job(string id, string state) => new(id, "out", state, null, "5551234", null, null, null, 1, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static string InJson(string id) =>
        $$"""{"id":"{{id}}","direction":"in","state":"received","from_number":"5559876","pages":1,"created_at":"2026-09-30T10:00:00+00:00","updated_at":"2026-09-30T10:00:05+00:00","finished_at":"2026-09-30T10:00:05+00:00"}""";

    /// <summary>A document that reached spool\jobs with no record (as an install's rescue leaves one).</summary>
    private Guid PlantJobsPdf()
    {
        var id = Guid.NewGuid();
        var jobs = Path.Combine(_dir, "spool", "jobs");
        Directory.CreateDirectory(jobs);
        File.WriteAllText(Path.Combine(jobs, id + ".pdf"), "%PDF-1.4");
        return id;
    }

    private static string JobJson(string id, string state) =>
        $$"""{"id":"{{id}}","direction":"out","state":"{{state}}","to_number":"5551234","pages":1,"created_at":"2026-09-30T10:00:00+00:00","updated_at":"2026-09-30T10:00:05+00:00","finished_at":null}""";

    private static void Open(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.SendChunked = true;
    }

    private static async Task Write(HttpListenerContext ctx, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        await ctx.Response.OutputStream.WriteAsync(b);
        await ctx.Response.OutputStream.FlushAsync();
    }

    /// <summary>Keeps the stream open (keepalives) until the client goes away or the test ends.</summary>
    private async Task Hold(HttpListenerContext ctx)
    {
        try
        {
            while (!_done.IsCancellationRequested)
            {
                await Task.Delay(50, _done.Token);
                await Write(ctx, ": keepalive\n\n");
            }
        }
        catch { /* client gone */ }
        try { ctx.Response.Abort(); } catch { }
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException("condition not met"); await Task.Delay(10); }
    }

    private sealed class FakeKeys : IPrinterKeyReader
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        /// <summary>The printer's port; null is a missing printer.</summary>
        public volatile string? PortValue = PrinterSetup.ExpectedPort();
        public bool Exists(string printerName) => PortValue != null;
        public string? Port(string printerName) { Interlocked.Increment(ref _reads); return PortValue; }
    }

    private sealed class FakeListener : IPrintListener
    {
        public event Action<Guid, string>? NewPrintJob;
        public event Action<string, Exception>? Error;
        public event Action<bool>? ReceivingChanged;
        public volatile bool IsRunning;
        public volatile bool IsReceiving;
        public int StartCalls, StopCalls, DisposeCalls;
        bool IPrintListener.IsRunning => IsRunning;
        bool IPrintListener.IsReceiving => IsReceiving;
        public void Start() { Interlocked.Increment(ref StartCalls); IsRunning = true; }
        public void Stop() { Interlocked.Increment(ref StopCalls); IsRunning = false; }
        public void Dispose() { Interlocked.Increment(ref DisposeCalls); Stop(); }
        public Guid RaiseNewPrintJob() { var id = Guid.NewGuid(); NewPrintJob?.Invoke(id, id + ".pdf"); return id; }
        public void RaiseReceiving(bool on) => ReceivingChanged?.Invoke(on);
        public void RaiseError() => Error?.Invoke("test", new IOException("test"));
    }

    /// <summary>
    /// A dedicated thread that runs posted actions in order, with a SynchronizationContext that brings await
    /// continuations back to it (what WinForms does for the tray's UI thread).
    /// </summary>
    private sealed class OwnerThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private int _pending;
        public readonly ConcurrentQueue<Exception> Errors = new();
        public int ThreadId => _thread.ManagedThreadId;
        /// <summary>The owner thread's SynchronizationContext, the one the runtime is given.</summary>
        public SynchronizationContext Context { get; }
        /// <summary>Actions posted and not yet started.</summary>
        public int Pending => Volatile.Read(ref _pending);

        public OwnerThread()
        {
            Context = new OwnerContext(this);
            _thread = new Thread(Loop) { IsBackground = true, Name = "runtime owner" };
            _thread.Start();
        }

        private void Loop()
        {
            SynchronizationContext.SetSynchronizationContext(Context);
            foreach (var a in _queue.GetConsumingEnumerable())
            {
                Interlocked.Decrement(ref _pending);
                try { a(); } catch (Exception e) { Errors.Enqueue(e); }
            }
        }

        public void Post(Action a)
        {
            Interlocked.Increment(ref _pending);
            try { _queue.Add(a); } catch (InvalidOperationException) { Interlocked.Decrement(ref _pending); }
        }

        public T Invoke<T>(Func<T> f)
        {
            if (Environment.CurrentManagedThreadId == ThreadId) return f();
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        }

        public void Invoke(Action a) => Invoke(() => { a(); return 0; });

        public Task InvokeAsync(Func<Task> f) => InvokeAsync(async () => { await f(); return 0; });

        public Task<T> InvokeAsync<T>(Func<Task<T>> f)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async () => { try { tcs.SetResult(await f()); } catch (Exception e) { tcs.SetException(e); } });
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        /// <summary>Returns once everything posted before it has run.</summary>
        public void Drain() => Invoke(() => { });

        public void Dispose() => _queue.CompleteAdding();

        private sealed class OwnerContext(OwnerThread owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) => owner.Post(() => d(state));
            public override void Send(SendOrPostCallback d, object? state) => owner.Invoke(() => d(state));
            public override SynchronizationContext CreateCopy() => this;
        }
    }
}
