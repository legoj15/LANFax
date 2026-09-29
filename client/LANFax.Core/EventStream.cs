using System.Text;
using System.Text.Json;

namespace LANFax.Core;

/// <summary>
/// Server-Sent Events client for GET /events. The token goes in the query string (the one route where faxd
/// accepts it). Handlers run on a thread-pool thread; marshal to the UI thread yourself.
/// The HttpClient passed in must have an infinite Timeout.
/// </summary>
public sealed class EventStream(ClientConfig cfg, HttpClient http)
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public event Action<Job>? JobChanged;
    public event Action<Job>? Received;
    public event Action<ArmState>? ArmChanged;
    /// <summary>Full or partial status object; merge with <see cref="ServerStatus.Merge"/>. Element stays valid after the call.</summary>
    public event Action<JsonElement>? StatusChanged;
    public event Action? Connected;
    public event Action<Exception?>? Disconnected;

    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

    public string? LastEventId { get; private set; }

    /// <summary>Runs until cancelled (returns normally on cancellation): connect, read, reconnect with backoff.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            Exception? error = null;
            try
            {
                var url = new Uri(cfg.BaseUri, "events?token=" + Uri.EscapeDataString(cfg.Token ?? ""));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                if (LastEventId != null) req.Headers.TryAddWithoutValidation("Last-Event-ID", LastEventId);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                    throw new FaxApiException((int)resp.StatusCode, "events: HTTP " + (int)resp.StatusCode);
                backoff = MinBackoff;
                Raise(() => Connected?.Invoke());
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                await ReadAsync(stream, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { error = e; }

            if (ct.IsCancellationRequested) break;
            Raise(() => Disconnected?.Invoke(error));
            try { await Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task ReadAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? evType = null, id = null;
        var data = new StringBuilder();
        bool hasData = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) return; // server closed the stream
            if (line.Length == 0)
            {
                if (id != null) LastEventId = id;
                if (hasData) Dispatch(evType ?? "message", data.ToString());
                evType = null; id = null; data.Clear(); hasData = false;
                continue;
            }
            if (line[0] == ':') continue; // comment / keepalive
            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "event": evType = value; break;
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value); hasData = true; break;
                case "id": if (!value.Contains('\0')) id = value; break;
            }
        }
    }

    private void Dispatch(string type, string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var el = doc.RootElement;
            switch (type)
            {
                case "job":
                    if (el.Deserialize<Job>(Json.Options) is { } j) Raise(() => JobChanged?.Invoke(j));
                    break;
                case "received":
                    if (el.Deserialize<Job>(Json.Options) is { } r) Raise(() => Received?.Invoke(r));
                    break;
                case "arm":
                    if (el.Deserialize<ArmState>(Json.Options) is { } a) Raise(() => ArmChanged?.Invoke(a));
                    break;
                case "status":
                    var clone = el.Clone();
                    Raise(() => StatusChanged?.Invoke(clone));
                    break;
            }
        }
        catch (JsonException) { /* malformed event: skip it */ }
    }

    private static void Raise(Action a)
    {
        try { a(); } catch { /* a faulty subscriber must not kill the stream */ }
    }
}
