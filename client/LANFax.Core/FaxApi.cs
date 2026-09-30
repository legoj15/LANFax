using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LANFax.Core;

/// <summary>
/// Non-2xx answer from faxd (or a network failure, StatusCode 0), or a 2xx whose body is not the expected JSON
/// (StatusCode is then that 2xx code). Message is the server's "error" text when it sent one.
/// </summary>
public class FaxApiException(int statusCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class FaxApiUnauthorizedException() : FaxApiException(401, "unauthorized (check the token in Settings)");

/// <summary>
/// The settings cannot form a request (a server with a port or path in it, a port outside 1..65535, a token that is not
/// a valid Bearer parameter). Thrown before any request is built, so it proves nothing was sent; never retried.
/// StatusCode is 0.
/// </summary>
public sealed class FaxApiConfigException(string message) : FaxApiException(0, message);

/// <summary>
/// Where one send goes: the base URI and token as they were when the send started. Every attempt of that send
/// (FaxApi's retries included) uses this snapshot, so saving Settings mid-retry cannot replay its Idempotency-Key
/// against another server.
/// </summary>
public sealed record SendEndpoint(Uri BaseUri, string? Token)
{
    /// <exception cref="FaxApiConfigException">
    /// the server or port cannot form a URI (<see cref="ClientConfig.EndpointError"/>), or the token cannot be sent as a
    /// Bearer parameter (<see cref="ClientConfig.TokenError"/>): refused here, before anything is sent
    /// </exception>
    public static SendEndpoint From(ClientConfig cfg)
    {
        if (cfg.EndpointError() is { } err) throw new FaxApiConfigException(err);
        if (ClientConfig.TokenError(cfg.Token) is { } bad) throw new FaxApiConfigException(bad);
        try { return new SendEndpoint(cfg.BaseUri, cfg.Token); }
        catch (Exception e) when (e is UriFormatException or InvalidOperationException or ArgumentException)
        {
            throw new FaxApiConfigException("the server address cannot be used: " + e.Message);
        }
    }
}

/// <summary>The two requests that submit a fax; the send dialog's <c>SendExecutor</c> depends on this, not on HTTP.</summary>
public interface ISendApi
{
    Task<Job> PostJobAsync(SendEndpoint endpoint, byte[] pdf, string to, string? subject, string idempotencyKey, CancellationToken ct = default);
    Task<Job> SendTestPageAsync(SendEndpoint endpoint, string to, string? note, string idempotencyKey, CancellationToken ct = default);
}

/// <summary>
/// HttpClient wrapper for the faxd API. The token is sent as a Bearer header and never logged. Calls without a
/// <see cref="SendEndpoint"/> snapshot the config once per call; a config that cannot form a URI throws
/// <see cref="FaxApiConfigException"/> before any request.
/// </summary>
public sealed class FaxApi(ClientConfig cfg, HttpClient http) : ISendApi
{
    public const int MaxAttempts = 3;

    /// <summary>faxd's MAX_BODY: a larger POST /jobs body is refused (413), so the client does not upload it.</summary>
    public const int MaxBodyBytes = 25 * 1024 * 1024;

    /// <summary>Waits before attempt 2, 3 (index 0, 1). Tests replace <see cref="Delay"/>.</summary>
    public IReadOnlyList<TimeSpan> Backoffs { get; set; } =
        new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

    /// <summary>Optional diagnostics sink (skipped list rows and the like). Never receives the token.</summary>
    public Action<string>? Log { get; set; }

    // ---- reads ----

    public async Task<ServerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var (code, body) = await SendAsync(() => Req(HttpMethod.Get, "status"), ct);
        using var doc = ParseDoc(code, body, "status");
        try { return ServerStatus.FromJson(doc.RootElement); }
        catch (Exception e) when (e is JsonException or FormatException) { throw Unreadable(code, "status", e); }
    }

    public async Task<IReadOnlyList<Job>> GetJobsAsync(string? direction = null, IEnumerable<string>? states = null,
        int limit = 100, DateTimeOffset? updatedSince = null, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (direction != null) q.Add("direction=" + Uri.EscapeDataString(direction));
        var st = states?.ToArray();
        if (st is { Length: > 0 }) q.Add("state=" + string.Join(",", st.Select(Uri.EscapeDataString)));
        q.Add("limit=" + limit.ToString(CultureInfo.InvariantCulture));
        if (updatedSince is { } s)
            q.Add("updated_since=" + Uri.EscapeDataString(
                s.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        return await ListAsync("jobs?" + string.Join("&", q), ct);
    }

    public Task<IReadOnlyList<Job>> GetInboxAsync(int limit = 50, CancellationToken ct = default) =>
        ListAsync("inbox?limit=" + limit.ToString(CultureInfo.InvariantCulture), ct);

    public async Task<Job> GetJobAsync(string id, CancellationToken ct = default)
    {
        var (code, body) = await SendAsync(() => Req(HttpMethod.Get, "jobs/" + Uri.EscapeDataString(id)), ct);
        return ParseJob(code, body);
    }

    public async Task DownloadPdfAsync(string id, string destPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = destPath + ".part";
        try
        {
            using var req = Req(HttpMethod.Get, "jobs/" + Uri.EscapeDataString(id) + "/pdf");
            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception e) when (IsNetwork(e, ct)) { throw new FaxApiException(0, "cannot reach faxd: " + e.Message, e); }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) throw await ToException(resp);
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    await src.CopyToAsync(dst, ct);
            }
            File.Move(tmp, destPath, overwrite: true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ---- writes ----

    /// <summary>
    /// POST /jobs. Retries only timeout, connection failure, 502 and 503 (same Idempotency-Key), up to
    /// <see cref="MaxAttempts"/> attempts. 200 and 201 both mean accepted. <paramref name="to"/> is sent
    /// normalized (<see cref="Numbers.Normalize"/>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="to"/> has non-ASCII characters or no digits; nothing is sent.</exception>
    public async Task<Job> PostJobAsync(Stream pdf, string to, string? subject, string idempotencyKey, CancellationToken ct = default)
    {
        var ep = SendEndpoint.From(cfg); // before reading: a config error sends and reads nothing
        using var buf = new MemoryStream();
        await pdf.CopyToAsync(buf, ct);
        return await PostJobAsync(ep, buf.ToArray(), to, subject, idempotencyKey, ct);
    }

    /// <summary><see cref="PostJobAsync(Stream, string, string?, string, CancellationToken)"/> with the document already in memory (not copied).</summary>
    public Task<Job> PostJobAsync(byte[] pdf, string to, string? subject, string idempotencyKey, CancellationToken ct = default) =>
        PostJobAsync(SendEndpoint.From(cfg), pdf, to, subject, idempotencyKey, ct);

    /// <summary>POST /jobs to <paramref name="endpoint"/> for every attempt; <paramref name="pdf"/> is sent as is, never copied.</summary>
    /// <exception cref="ArgumentException"><paramref name="to"/> has non-ASCII characters or no digits; nothing is sent.</exception>
    public Task<Job> PostJobAsync(SendEndpoint endpoint, byte[] pdf, string to, string? subject, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(pdf);
        to = WireNumber(to);
        var uri = Url(endpoint, "jobs"); // built once, outside the "may be on the wire" region
        return PostWithRetryAsync(() =>
        {
            var r = Req(endpoint, HttpMethod.Post, uri);
            r.Content = new ByteArrayContent(pdf);
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            r.Headers.TryAddWithoutValidation("X-Fax-To", to);
            if (!string.IsNullOrEmpty(subject)) r.Headers.TryAddWithoutValidation("X-Fax-Subject", Ascii(subject));
            r.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            return r;
        }, ct);
    }

    /// <summary>POST /test/send, same retry rules and number handling as <see cref="PostJobAsync"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="to"/> has non-ASCII characters or no digits; nothing is sent.</exception>
    public Task<Job> SendTestPageAsync(string to, string? note, string idempotencyKey, CancellationToken ct = default) =>
        SendTestPageAsync(SendEndpoint.From(cfg), to, note, idempotencyKey, ct);

    /// <summary>POST /test/send to <paramref name="endpoint"/> for every attempt.</summary>
    /// <exception cref="ArgumentException"><paramref name="to"/> has non-ASCII characters or no digits; nothing is sent.</exception>
    public Task<Job> SendTestPageAsync(SendEndpoint endpoint, string to, string? note, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        to = WireNumber(to);
        var uri = Url(endpoint, "test/send");
        return PostWithRetryAsync(() =>
        {
            var r = Req(endpoint, HttpMethod.Post, uri);
            r.Content = JsonBody(new { to, note = note ?? "" });
            r.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            return r;
        }, ct);
    }

    public async Task<ArmState> ArmAsync(int minutes, CancellationToken ct = default) =>
        await PostArmAsync("arm", new { minutes }, ct);

    public async Task<ArmState> DisarmAsync(CancellationToken ct = default) =>
        await PostArmAsync("disarm", null, ct);

    public async Task<ArmState> ReceiveTestAsync(int minutes = 20, CancellationToken ct = default) =>
        await PostArmAsync("test/receive", new { minutes }, ct);

    // ---- plumbing ----

    private async Task<ArmState> PostArmAsync(string path, object? body, CancellationToken ct)
    {
        var (code, resp) = await SendAsync(() =>
        {
            var r = Req(HttpMethod.Post, path);
            r.Content = body == null ? new ByteArrayContent(Array.Empty<byte>()) : JsonBody(body);
            return r;
        }, ct);
        return Deserialize<ArmState>(code, resp, path);
    }

    private async Task<Job> PostWithRetryAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var (code, body) = await SendAsync(make, ct);
                return ParseJob(code, body); // an unreadable 2xx throws with its 2xx code: not retried
            }
            catch (FaxApiException e) when (attempt < MaxAttempts && e.StatusCode is 0 or 502 or 503 && e is not (FaxApiUnauthorizedException or FaxApiConfigException))
            {
                await Delay(Backoffs[Math.Min(attempt - 1, Backoffs.Count - 1)], ct);
            }
        }
    }

    private async Task<IReadOnlyList<Job>> ListAsync(string path, CancellationToken ct)
    {
        var (code, body) = await SendAsync(() => Req(HttpMethod.Get, path), ct);
        using var doc = ParseDoc(code, body, path);
        var list = new List<Job>();
        int skipped = 0;
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("jobs", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                Job? j = null;
                try { j = e.Deserialize<Job>(Json.Options); }
                catch (JsonException) { }
                if (IsUsable(j)) list.Add(j!);
                else skipped++;
            }
        if (skipped > 0) Log?.Invoke($"GET /{PathOnly(path)}: skipped {skipped} unreadable row(s)");
        return list;
    }

    // Json.Options does not enforce nullable annotations, so a row without id/state deserializes with nulls.
    private static bool IsUsable(Job? j) => j is not null && j.Id is not null && j.State is not null;

    private static string PathOnly(string path) => path.Split('?')[0];

    private static Job ParseJob(HttpStatusCode code, byte[] body)
    {
        var j = Deserialize<Job>(code, body, "job");
        if (!IsUsable(j)) throw new FaxApiException((int)code, "faxd returned a job without id or state");
        return j;
    }

    /// <summary>A 2xx body that is not the expected JSON becomes a FaxApiException carrying that status, never a raw JsonException.</summary>
    private static T Deserialize<T>(HttpStatusCode code, byte[] body, string what) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json.Options)
                   ?? throw new FaxApiException((int)code, $"faxd returned an empty {what} response");
        }
        catch (JsonException e) { throw Unreadable(code, what, e); }
    }

    private static JsonDocument ParseDoc(HttpStatusCode code, byte[] body, string what)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException e) { throw Unreadable(code, PathOnly(what), e); }
    }

    private static FaxApiException Unreadable(HttpStatusCode code, string what, Exception e) =>
        new((int)code, $"faxd returned an unreadable {what} response (not the expected JSON)", e);

    /// <summary>
    /// The number as sent: <see cref="Numbers.Normalize"/>d digits. Non-ASCII input is refused outright (faxd's
    /// <c>\D</c> keeps non-ASCII digits, the client drops them, so the two would disagree).
    /// </summary>
    private static string WireNumber(string to)
    {
        ArgumentNullException.ThrowIfNull(to);
        if (to.Any(c => c > '~')) throw new ArgumentException("the fax number may contain only ASCII characters", nameof(to));
        var n = Numbers.Normalize(to);
        if (n.Length == 0 || !n.All(c => c is >= '0' and <= '9'))
            throw new ArgumentException("the fax number has no digits", nameof(to));
        return n;
    }

    /// <summary>A request against a snapshot of the config (throws <see cref="FaxApiConfigException"/> for an unusable one).</summary>
    private HttpRequestMessage Req(HttpMethod m, string path)
    {
        var ep = SendEndpoint.From(cfg);
        return Req(ep, m, Url(ep, path));
    }

    private static Uri Url(SendEndpoint ep, string path) => new(ep.BaseUri, path);

    private static HttpRequestMessage Req(SendEndpoint ep, HttpMethod m, Uri uri)
    {
        var r = new HttpRequestMessage(m, uri);
        if (!string.IsNullOrEmpty(ep.Token)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.Token);
        return r;
    }

    private static StringContent JsonBody(object o) =>
        new(JsonSerializer.Serialize(o, Json.Options), Encoding.UTF8, "application/json");

    private static bool IsNetwork(Exception e, CancellationToken ct) =>
        e is HttpRequestException || (e is TaskCanceledException or TimeoutException && !ct.IsCancellationRequested);

    private async Task<(HttpStatusCode, byte[])> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        // Built before the network try: a failure here (FaxApiConfigException) provably sent nothing.
        using var req = make();
        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) throw await ToException(resp);
            return (resp.StatusCode, await resp.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception e) when (IsNetwork(e, ct))
        {
            throw new FaxApiException(0, "cannot reach faxd: " + e.Message, e);
        }
    }

    private static async Task<FaxApiException> ToException(HttpResponseMessage resp)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return new FaxApiUnauthorizedException();
        string msg = resp.ReasonPhrase ?? ("HTTP " + (int)resp.StatusCode);
        try
        {
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                msg = e.GetString() ?? msg;
        }
        catch { /* not JSON: keep the reason phrase */ }
        return new FaxApiException((int)resp.StatusCode, msg);
    }

    /// <summary>HTTP header values must be ASCII; replace anything else so a subject never breaks the request.</summary>
    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is >= ' ' and <= '~' ? c : '?');
        return sb.ToString();
    }
}
