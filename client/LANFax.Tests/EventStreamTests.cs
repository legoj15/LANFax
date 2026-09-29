using System.Net;
using System.Text;
using System.Text.Json;
using LANFax.Core;

namespace LANFax.Tests;

public class EventStreamTests : IDisposable
{
    private readonly FakeServer _srv = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly List<TimeSpan> _delays = new();
    private readonly CancellationTokenSource _cts = new();

    public void Dispose() { _cts.Cancel(); _srv.Dispose(); _http.Dispose(); }

    private EventStream Stream()
    {
        var cfg = new ClientConfig { Server = "127.0.0.1", Port = _srv.Port, Token = "tok abc" };
        return new EventStream(cfg, _http)
        {
            Delay = (d, _) => { lock (_delays) _delays.Add(d); return Task.Delay(5); }
        };
    }

    private static async Task Write(HttpListenerContext ctx, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        await ctx.Response.OutputStream.WriteAsync(b);
        await ctx.Response.OutputStream.FlushAsync();
    }

    private static void Open(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.SendChunked = true;
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException("condition not met"); await Task.Delay(10); }
    }

    private const string JobJson = """{"type":"job","ts":"t","id":"lf1","direction":"out","state":"sending","filename":"f.pdf","to_number":"1","pages":2,"created_at":"2026-09-29T15:00:00+00:00","updated_at":"2026-09-29T15:00:01+00:00","finished_at":null}""";

    [Fact]
    public async Task ParsesTypedEventsAndIgnoresKeepalive()
    {
        _srv.Handler = async (ctx, _) =>
        {
            Open(ctx);
            await Write(ctx, ": keepalive\n\n");
            await Write(ctx, "event: status\ndata: {\"receiving\":true,\"from\":\"555\"}\n\n");
            await Write(ctx, "event: arm\ndata: {\"armed\":true,\"until\":null,\"remaining_min\":20,\"ext\":\"300\"}\n\n");
            await Write(ctx, ": keepalive\n\n");
            await Write(ctx, "id: 4\nevent: job\ndata: " + JobJson + "\n\n");
            await Write(ctx, "event: received\ndata: " + JobJson.Replace("\"sending\"", "\"received\"") + "\n\n");
            await Write(ctx, "event: unknown\ndata: {}\n\n");
            await Write(ctx, "event: job\ndata: {\"id\":\"lf2\",\"direction\":\"out\",\"state\":\"queued\",\n");
            await Write(ctx, "data: \"created_at\":\"2026-09-29T15:00:00+00:00\",\"updated_at\":\"2026-09-29T15:00:00+00:00\"}\n\n");
            await Task.Delay(Timeout.Infinite, _cts.Token);
        };
        var es = Stream();
        var jobs = new List<Job>(); var recv = new List<Job>(); var arms = new List<ArmState>(); var status = new List<JsonElement>();
        int connected = 0;
        es.JobChanged += j => { lock (jobs) jobs.Add(j); };
        es.Received += j => { lock (recv) recv.Add(j); };
        es.ArmChanged += a => { lock (arms) arms.Add(a); };
        es.StatusChanged += s => { lock (status) status.Add(s.Clone()); };
        es.Connected += () => Interlocked.Increment(ref connected);
        var run = es.StartAsync(_cts.Token);
        await WaitFor(() => { lock (jobs) return jobs.Count >= 2; });
        _cts.Cancel();
        await run;

        Assert.Equal(1, connected);
        Assert.Equal(new[] { "lf1", "lf2" }, jobs.Select(j => j.Id));
        Assert.Equal("sending", jobs[0].State);
        Assert.Equal(2, jobs[0].Pages);
        var r = Assert.Single(recv);
        Assert.Equal("received", r.State);
        var a = Assert.Single(arms);
        Assert.True(a.Armed);
        Assert.Equal(20, a.RemainingMin);
        var s = Assert.Single(status);
        Assert.True(s.GetProperty("receiving").GetBoolean());
        Assert.Equal("/events?token=tok%20abc", _srv.Requests[0].PathAndQuery);
        Assert.Null(_srv.Requests[0].Headers["Last-Event-ID"]);
    }

    [Fact]
    public async Task ReconnectsWithLastEventIdAndBackoffResetsOnSuccess()
    {
        int n = 0;
        _srv.Handler = async (ctx, _) =>
        {
            int c = Interlocked.Increment(ref n);
            if (c <= 2) { await FakeServer.Json(ctx, "{\"error\":\"down\"}", 500); return; }
            Open(ctx);
            await Write(ctx, "id: 7\nevent: arm\ndata: {\"armed\":false,\"remaining_min\":0,\"ext\":\"300\"}\n\n");
            if (c == 3) { ctx.Response.Close(); return; }
            await Write(ctx, "id: 8\nevent: arm\ndata: {\"armed\":true,\"remaining_min\":5,\"ext\":\"300\"}\n\n");
            await Task.Delay(Timeout.Infinite, _cts.Token);
        };
        var es = Stream();
        int arms = 0, discon = 0;
        es.ArmChanged += _ => Interlocked.Increment(ref arms);
        es.Disconnected += _ => Interlocked.Increment(ref discon);
        var run = es.StartAsync(_cts.Token);
        await WaitFor(() => Volatile.Read(ref arms) >= 3);
        _cts.Cancel();
        await run;

        var reqs = _srv.Requests;
        Assert.True(reqs.Length >= 4);
        Assert.Null(reqs[0].Headers["Last-Event-ID"]);
        Assert.Null(reqs[1].Headers["Last-Event-ID"]);
        Assert.Null(reqs[2].Headers["Last-Event-ID"]);
        Assert.Equal("7", reqs[3].Headers["Last-Event-ID"]);
        lock (_delays)
            Assert.Equal(new[] { 1.0, 2.0, 1.0 }, _delays.Take(3).Select(d => d.TotalSeconds));
        Assert.True(discon >= 3);
    }

    [Fact]
    public async Task CancelBeforeConnectReturns()
    {
        _srv.Handler = async (ctx, _) => { Open(ctx); await Write(ctx, ": hi\n\n"); await Task.Delay(Timeout.Infinite, _cts.Token); };
        var es = Stream();
        var run = es.StartAsync(_cts.Token);
        await Task.Delay(100);
        _cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
