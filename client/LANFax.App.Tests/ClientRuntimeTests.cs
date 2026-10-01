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

    private ClientRuntime Make(TimeSpan? poll = null, TimeSpan? printerCheck = null)
    {
        var cfg = new ClientConfig
        {
            Server = "127.0.0.1", Port = _srv.Port, Token = "tok123", SpoolDir = Path.Combine(_dir, "spool"),
        };
        _rt = new ClientRuntime(cfg, _owner.Post)
        {
            Listener = _listener,
            Printer = new PrinterSetup(_keys),
            AnnouncedPath = Path.Combine(_dir, "announced.json"),
            ListPrintQueue = _ => [],
            PollInterval = poll ?? TimeSpan.FromHours(1),
            PrinterCheckInterval = printerCheck ?? TimeSpan.FromHours(1),
            Info = m => { lock (_log) _log.Add(m); },
            Error = (m, e) => { lock (_log) _log.Add("ERROR " + m + (e == null ? "" : ": " + e.Message)); },
            RunStream = async (es, ct) =>
            {
                Interlocked.Increment(ref _runsEntered);
                int now = Interlocked.Increment(ref _activeRuns);
                lock (_log) _maxActiveRuns = Math.Max(_maxActiveRuns, now);
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
        public bool Exists(string printerName) => true;
        public string? Port(string printerName) { Interlocked.Increment(ref _reads); return PrinterSetup.ExpectedPort(); }
    }

    private sealed class FakeListener : IPrintListener
    {
        public event Action<Guid, string>? NewPrintJob;
        public event Action<string, Exception>? Error;
        public event Action<bool>? ReceivingChanged;
        public volatile bool IsRunning;
        public volatile bool IsReceiving;
        public int StartCalls, StopCalls;
        bool IPrintListener.IsRunning => IsRunning;
        bool IPrintListener.IsReceiving => IsReceiving;
        public void Start() { Interlocked.Increment(ref StartCalls); IsRunning = true; }
        public void Stop() { Interlocked.Increment(ref StopCalls); IsRunning = false; }
        public void Dispose() => Stop();
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
        /// <summary>Actions posted and not yet started.</summary>
        public int Pending => Volatile.Read(ref _pending);

        public OwnerThread()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "runtime owner" };
            _thread.Start();
        }

        private void Loop()
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
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

        public Task InvokeAsync(Func<Task> f)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async () => { try { await f(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } });
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        /// <summary>Returns once everything posted before it has run.</summary>
        public void Drain() => Invoke(() => { });

        public void Dispose() => _queue.CompleteAdding();

        private sealed class Context(OwnerThread owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object? state) => owner.Post(() => d(state));
            public override void Send(SendOrPostCallback d, object? state) => owner.Invoke(() => d(state));
            public override SynchronizationContext CreateCopy() => this;
        }
    }
}
