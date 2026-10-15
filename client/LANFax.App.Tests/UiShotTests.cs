using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using LANFax.App.Logic;
using LANFax.Core;

namespace LANFax.App.Tests;

/// <summary>A fact that renders screenshots only when LANFAX_UISHOT names the output directory.</summary>
public sealed class UiShotFactAttribute : FactAttribute
{
    public UiShotFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LANFAX_UISHOT")))
            Skip = "set LANFAX_UISHOT=<dir> to render the screenshots";
    }
}

/// <summary>
/// Renders the windows to PNGs so a person (or an agent with eyes) can look at them:
/// run with LANFAX_UISHOT=&lt;dir&gt; (e.g. <c>dotnet test --filter UiShot</c> with the
/// variable set). Skipped without the variable; never asserts about looks.
/// </summary>
public sealed class UiShotTests
{
    [UiShotFact]
    public void Render_the_windows_to_LANFAX_UISHOT()
    {
        var dir = Environment.GetEnvironmentVariable("LANFAX_UISHOT")!;
        Directory.CreateDirectory(dir);
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                RenderMainForm(Path.Combine(dir, "mainform.png"));
                RenderSendDialog(Path.Combine(dir, "send-3pages.png"), new Size(1100, 700));
                RenderSendDialog(Path.Combine(dir, "send-wide.png"), new Size(1500, 760));
                RenderSendDialog(Path.Combine(dir, "send-note.png"), new Size(1100, 700), number: "");
                RenderReceipt(Path.Combine(dir, "receipt.png"));
            }
            catch (Exception e) { failure = e; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the screenshot thread hung");
        if (failure != null) throw new Xunit.Sdk.XunitException("UiShot render failed: " + failure);
    }

    private static void RenderMainForm(string path)
    {
        var src = new ShotSource();
        src.RaiseStatus(ServerStatus.Empty with
        {
            AmiConnected = true,
            Arm = new ArmState(false, null, 0, "300"),
            Registrations = [("reg-in", "Registered"), ("reg-out", "Registered")],
        });
        var t0 = DateTimeOffset.Now;
        src.Load(
            J("lf1", "out", "sent", "17634051040", 1, t0),
            J("lf2", "out", "sent", "000", 3, t0.AddMinutes(-1)),
            J("lf3", "out", "queued", "17634051040", 1, t0.AddMinutes(-2)),
            J("in1", "in", "received", "SELFTEST", 1, t0));
        using var form = new MainForm(src, cacheDir: Path.GetTempPath(), openFile: _ => { }, openSettings: () => { },
            sendFax: () => { }, showReceipt: _ => { });
        form.Show();
        Pump();
        Shot(form, path);
    }

    private static void RenderSendDialog(string path, Size size, string number = "555 867 5309")
    {
        var dir = Path.Combine(Path.GetTempPath(), "uishot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cover = Path.Combine(dir, "cover.pdf");
            File.WriteAllText(cover, "%PDF-1.4\n%%EOF\n");
            var partId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(dir, partId + ".pdf"), "%PDF-1.4\n%%EOF\n");
            var api = new ShotApi { Pages = [Page("Page 1 — cover letter"), Page("Page 2 — invoice"), Page("Page 3 — invoice, continued")] };
            using var dialog = new SendDialog(
                new ClientConfig { Server = "127.0.0.1", Port = 8080, Token = "t" }, api,
                new SendRequest(Guid.NewGuid(), cover, number, "March invoices", SendKind.New, Parts: [partId]),
                journal: null, openSettings: () => { });
            dialog.Show();
            Pump();
            dialog.Size = size;
            Pump();
            Shot(dialog, path);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void RenderReceipt(string path)
    {
        var api = new ReceiptApi();
        using var form = new ReceiptDialog(() => api, "lf1", Path.GetTempPath());
        form.Show();
        Pump();
        Shot(form, path);
    }

    private static byte[] Page(string label)
    {
        using var bmp = new Bitmap(680, 880);   // what faxd renders at 80 dpi for a Letter page
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.DrawRectangle(Pens.LightGray, 0, 0, bmp.Width - 1, bmp.Height - 1);
        g.DrawString("TO 15558675309 FROM OFFICE 2026-10-14 17:02", SystemFonts.MessageBoxFont!, Brushes.DimGray, 16, 10);
        g.DrawString(label, new Font(SystemFonts.MessageBoxFont!.FontFamily, 18f), Brushes.Black, 60, 300);
        g.DrawString("Page number goes at the bottom-left", SystemFonts.MessageBoxFont!, Brushes.DimGray, 16, 850);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static void Shot(Form form, string path)
    {
        form.Activate();
        form.BringToFront();
        Application.DoEvents();
        // PrintWindow (not the screen): the form's own pixels only — a background process
        // cannot take the foreground, so a screen copy would capture whatever is on top.
        using var bmp = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try { _ = PrintWindow(form.Handle, hdc, PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
        }
        bmp.Save(path, ImageFormat.Png);
    }

    private const uint PW_RENDERFULLCONTENT = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in AllControls(c)) yield return d;
        }
    }

    private static void Pump()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline) Application.DoEvents();
    }

    private static Job J(string id, string dir, string state, string party, int pages, DateTimeOffset t) =>
        new(id, dir, state, null, dir == "out" ? party : null, dir == "in" ? party : null, null, null, pages, null, t, t,
            state is "sent" or "received" ? t.AddSeconds(85) : null);

    /// <summary>Preview pages for the dialog; the send itself is not exercised here.</summary>
    private sealed class ShotApi : ISendApi
    {
        public IReadOnlyList<byte[]> Pages { get; set; } = [];

        public Task<Job> PostJobAsync(SendEndpoint endpoint, byte[] pdf, string to, string? subject, string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(new Job("lfshot", "out", "queued", null, to, null, subject, null, null, null, DateTimeOffset.Now, DateTimeOffset.Now, null));

        public Task<Job> PostJobAsync(SendEndpoint endpoint, IReadOnlyList<(byte[] Bytes, string Name)> parts, string to, string? subject, string idempotencyKey, CancellationToken ct = default) =>
            PostJobAsync(endpoint, parts[0].Bytes, to, subject, idempotencyKey, ct);

        public Task<Job> SendTestPageAsync(SendEndpoint endpoint, string to, string? note, string idempotencyKey, CancellationToken ct = default) =>
            PostJobAsync(endpoint, Array.Empty<byte>(), to, subject: note, idempotencyKey, ct);

        public Task<FaxCheck?> AnalyzeJobAsync(SendEndpoint endpoint, IReadOnlyList<(byte[] Bytes, string Name)> parts, CancellationToken ct = default) =>
            Task.FromResult<FaxCheck?>(new FaxCheck(Pages.Count, []));

        public Task<FaxPreview?> PreviewJobAsync(SendEndpoint endpoint, IReadOnlyList<(byte[] Bytes, string Name)> parts, string to, CancellationToken ct = default) =>
            Task.FromResult<FaxPreview?>(new FaxPreview(Pages.Count, Pages.Count, Pages, Stamped: true));
    }

    /// <summary>One sent fax with its transmission log.</summary>
    private sealed class ReceiptApi : IMonitorApi
    {
        private static readonly DateTimeOffset T = new(2026, 10, 14, 16, 44, 0, TimeSpan.FromHours(2));

        public Task<JobDetail> GetJobDetailAsync(string id, CancellationToken ct = default) => Task.FromResult(new JobDetail(
            new Job("lf1", "out", "sent", null, "17634051040", null, "March invoices", "api", 1, null,
                T, T.AddMinutes(2), T.AddMinutes(2), "sent 1 page(s) at 14400"),
            [
                new JobEvent(1, T, "queued", "enqueued via api"),
                new JobEvent(2, T.AddSeconds(3), "dialing", null),
                new JobEvent(3, T.AddSeconds(9), "sending", "calling"),
                new JobEvent(4, T.AddMinutes(2), "sent", "sent 1 page(s) at 14400"),
            ]));

        public Task<IReadOnlyList<Job>> GetJobsAsync(string? direction = null, IEnumerable<string>? states = null, int limit = 100,
            DateTimeOffset? updatedSince = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Job>>([]);
        public Task<IReadOnlyList<Job>> GetInboxAsync(int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Job>>([]);
        public Task DownloadPdfAsync(string id, string destPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task DownloadConfirmationAsync(string id, string destPath, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ArmState> ArmAsync(int minutes, CancellationToken ct = default) =>
            Task.FromResult(new ArmState(true, null, minutes, "300"));
        public Task<ArmState> DisarmAsync(CancellationToken ct = default) =>
            Task.FromResult(new ArmState(false, null, 0, "300"));
        public Task<ArmState> ReceiveTestAsync(int minutes = 20, CancellationToken ct = default) => ArmAsync(minutes, ct);
        public Task<Job> CancelJobAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>The runtime as the monitor window sees it, preloaded with a few rows.</summary>
    private sealed class ShotSource : IMonitorSource
    {
        private readonly ShotMonitorApi _api = new();
        public IMonitorApi MonitorApi => _api;
        public bool Connected { get; private set; } = true;
        public bool EverConnected => true;
        public ServerStatus Status { get; private set; } = ServerStatus.Empty;
        public MonitorModel Monitor { get; } = new(TimeZoneInfo.Local);
        public Exception? MonitorLoadError { get; private set; }
        public int ServerGeneration { get; private set; }
        public event Action<bool>? ConnectionChanged;
        public event Action<ServerStatus>? StatusChanged;
        public event Action? MonitorChanged;
        public event Action<Exception>? MonitorLoadFailed;

        public void NoteArm(ArmState answer)
        {
            if (answer == Status.Arm) return;
            Status = Status with { Arm = answer };
            StatusChanged?.Invoke(Status);
        }

        public Task<Job> CancelJobAsync(string id) => throw new NotSupportedException();

        public void Load(params Job[] jobs)
        {
            Monitor.ApplyLoad(jobs, DateTimeOffset.UtcNow);
            MonitorChanged?.Invoke();
        }

        public void RaiseStatus(ServerStatus s) { Status = s; StatusChanged?.Invoke(s); }

        public void RaiseConnection(bool c) { Connected = c; ConnectionChanged?.Invoke(c); }
        public void FailLoad(Exception e) { MonitorLoadError = e; MonitorLoadFailed?.Invoke(e); }

        private sealed class ShotMonitorApi : IMonitorApi
        {
            public Task<IReadOnlyList<Job>> GetJobsAsync(string? direction = null, IEnumerable<string>? states = null, int limit = 100,
                DateTimeOffset? updatedSince = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Job>>([]);
            public Task<IReadOnlyList<Job>> GetInboxAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Job>>([]);
            public Task DownloadPdfAsync(string id, string destPath, CancellationToken ct = default) => Task.CompletedTask;
            public Task DownloadConfirmationAsync(string id, string destPath, CancellationToken ct = default) => Task.CompletedTask;
            public Task<ArmState> ArmAsync(int minutes, CancellationToken ct = default) =>
                Task.FromResult(new ArmState(true, null, minutes, "300"));
            public Task<ArmState> DisarmAsync(CancellationToken ct = default) =>
                Task.FromResult(new ArmState(false, null, 0, "300"));
            public Task<ArmState> ReceiveTestAsync(int minutes = 20, CancellationToken ct = default) => ArmAsync(minutes, ct);
            public Task<Job> CancelJobAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<JobDetail> GetJobDetailAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        }
    }
}
