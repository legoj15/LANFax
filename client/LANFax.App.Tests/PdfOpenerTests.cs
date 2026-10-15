using System.Text;
using LANFax.App.Logic;
using LANFax.Core;
using LANFax.Tests;

namespace LANFax.App.Tests;

/// <summary>
/// Double-click to open a fax's PDF, against the in-process faxd fake and the real <see cref="FaxApi"/>: a fresh copy,
/// the cached copy when a viewer holds the file, and the 404, 401 and broken-download answers.
/// </summary>
public sealed class PdfOpenerTests : IDisposable
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\nnew\n%%EOF");
    private readonly FakeServer _srv = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-pdf-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _opened = [];

    public PdfOpenerTests()
    {
        _srv.Handler = async (ctx, _) =>
        {
            ctx.Response.ContentType = "application/pdf";
            ctx.Response.ContentLength64 = Pdf.Length;
            await ctx.Response.OutputStream.WriteAsync(Pdf);
            ctx.Response.Close();
        };
    }

    public void Dispose()
    {
        _srv.Dispose();
        _http.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private PdfOpener Make(IMonitorApi? api = null)
    {
        var real = new FaxApi(new ClientConfig { Server = "127.0.0.1", Port = _srv.Port, Token = "tok123" }, _http);
        return new PdfOpener(() => api ?? real, _dir, _opened.Add);
    }

    private string CachePath(string id) => MonitorModel.PdfCachePath(_dir, id);

    [Fact]
    public async Task Downloads_a_fresh_copy_and_opens_it()
    {
        var r = await Make().OpenAsync("lfabc");
        Assert.Equal(PdfOpenOutcome.Opened, r!.Outcome);
        Assert.Equal([CachePath("lfabc")], _opened);
        Assert.Equal(Pdf, File.ReadAllBytes(CachePath("lfabc")));
    }

    [Fact]
    public async Task A_copy_held_open_by_a_viewer_is_opened_as_is()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath("lfabc"), "old copy");
        // A PDF viewer keeps the file open and lets others read it, not replace it.
        using (new FileStream(CachePath("lfabc"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var r = await Make().OpenAsync("lfabc");
            Assert.Equal(PdfOpenOutcome.OpenedCachedCopy, r!.Outcome);
            Assert.Contains("open in another program", r.Message);
        }
        Assert.Equal([CachePath("lfabc")], _opened);
        Assert.Equal("old copy", File.ReadAllText(CachePath("lfabc")));
    }

    [Fact]
    public async Task A_download_cut_off_midway_is_an_error_not_the_stale_cached_copy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath("lfabc"), "old copy");
        _srv.Handler = async (ctx, _) =>
        {
            ctx.Response.ContentType = "application/pdf";
            ctx.Response.ContentLength64 = 1000;
            await ctx.Response.OutputStream.WriteAsync(Pdf);
            await ctx.Response.OutputStream.FlushAsync();
            ctx.Response.Abort();
        };
        var r = await Make().OpenAsync("lfabc");
        Assert.Equal(PdfOpenOutcome.Failed, r!.Outcome);
        Assert.StartsWith("Could not open the PDF", r.Message);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task A_cache_folder_that_cannot_take_the_download_is_an_error_not_the_stale_cached_copy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CachePath("lfabc"), "old copy");
        Directory.CreateDirectory(CachePath("lfabc") + ".part"); // stands in for a full disk or a read-only cache folder
        var r = await Make().OpenAsync("lfabc");
        Assert.Equal(PdfOpenOutcome.Failed, r!.Outcome);
        Assert.StartsWith("Could not open the PDF", r.Message);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task No_pdf_on_the_server()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, """{"error":"no pdf"}""", 404);
        var r = await Make().OpenAsync("lfabc");
        Assert.Equal(PdfOpenOutcome.NoPdf, r!.Outcome);
        Assert.Equal("The server has no PDF for this fax.", r.Message);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task A_refused_token()
    {
        _srv.Handler = (ctx, _) => FakeServer.Json(ctx, """{"error":"unauthorized"}""", 401);
        var r = await Make().OpenAsync("lfabc");
        Assert.Equal(PdfOpenOutcome.Unauthorized, r!.Outcome);
        Assert.Equal(NotificationText.WindowUnauthorized, r.Message);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task A_second_open_of_the_same_fax_while_the_first_runs_is_ignored_and_a_bad_id_fails()
    {
        var gate = new TaskCompletionSource();
        var opener = Make(new GatedApi(gate.Task));
        var first = opener.OpenAsync("lfabc");
        Assert.Null(await opener.OpenAsync("lfabc"));
        gate.SetResult();
        Assert.Equal(PdfOpenOutcome.Opened, (await first)!.Outcome);
        Assert.NotNull(await opener.OpenAsync("lfabc")); // done: may open again

        var bad = await opener.OpenAsync("..\evil");
        Assert.Equal(PdfOpenOutcome.Failed, bad!.Outcome);
    }

    private sealed class GatedApi(Task gate) : IMonitorApi
    {
        public Task<IReadOnlyList<Job>> GetJobsAsync(string? direction = null, IEnumerable<string>? states = null, int limit = 100,
            DateTimeOffset? updatedSince = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Job>> GetInboxAsync(int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadPdfAsync(string id, string destPath, CancellationToken ct = default) => gate;
        public Task DownloadConfirmationAsync(string id, string destPath, CancellationToken ct = default) => gate;
        public Task<ArmState> ArmAsync(int minutes, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArmState> DisarmAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArmState> ReceiveTestAsync(int minutes = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Job> CancelJobAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobDetail> GetJobDetailAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
