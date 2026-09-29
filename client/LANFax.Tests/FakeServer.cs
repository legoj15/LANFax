using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;

namespace LANFax.Tests;

public sealed record RecordedRequest(string Method, string PathAndQuery, NameValueCollection Headers, byte[] Body);

/// <summary>In-process HttpListener fake of faxd on 127.0.0.1 with a random free port.</summary>
public sealed class FakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<RecordedRequest> _requests = new();
    public Func<HttpListenerContext, RecordedRequest, Task> Handler { get; set; } =
        (ctx, _) => { ctx.Response.StatusCode = 404; ctx.Response.Close(); return Task.CompletedTask; };
    public int Port { get; }

    public FakeServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    using var ms = new MemoryStream();
                    await ctx.Request.InputStream.CopyToAsync(ms);
                    var rec = new RecordedRequest(ctx.Request.HttpMethod, ctx.Request.Url!.PathAndQuery,
                        new NameValueCollection(ctx.Request.Headers), ms.ToArray());
                    lock (_requests) _requests.Add(rec);
                    await Handler(ctx, rec);
                }
                catch { try { ctx.Response.Abort(); } catch { } }
            });
        }
    }

    public RecordedRequest[] Requests { get { lock (_requests) return _requests.ToArray(); } }

    public static async Task Json(HttpListenerContext ctx, string json, int status = 200)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = b.Length;
        await ctx.Response.OutputStream.WriteAsync(b);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Abort(); } catch { }
    }
}
