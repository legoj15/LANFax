using System.Text;
using LANFax.Core;

namespace LANFax.Tests;

public class SpoolWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-spool-" + Guid.NewGuid().ToString("N"));
    private readonly List<(Guid id, string path, DateTime at)> _events = new();
    private readonly List<(string reason, Exception error)> _errors = new();

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SpoolWatcher Make()
    {
        var w = new SpoolWatcher(_dir);
        w.NewPrintJob += (id, p) => { lock (_events) _events.Add((id, p, DateTime.UtcNow)); };
        w.Error += (r, e) => { lock (_errors) _errors.Add((r, e)); };
        return w;
    }

    private int Count { get { lock (_events) return _events.Count; } }
    private int ErrorCount { get { lock (_errors) return _errors.Count; } }

    /// <summary>A minimal file that passes the %PDF header check, padded to <paramref name="len"/> bytes.</summary>
    private static byte[] Pdf(int len = 16, byte fill = (byte)'x')
    {
        var b = Enumerable.Repeat(fill, Math.Max(len, 5)).ToArray();
        Encoding.ASCII.GetBytes("%PDF-").CopyTo(b, 0);
        return b;
    }

    private static async Task WaitFor(Func<bool> cond, int ms = 15000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond()) { if (DateTime.UtcNow > end) throw new TimeoutException("condition not met"); await Task.Delay(25); }
    }

    [Fact]
    public async Task MissingDirIsCreatedAndFileIsMoved()
    {
        Assert.False(Directory.Exists(_dir));
        using var w = Make();
        w.Start();
        Assert.True(Directory.Exists(_dir));
        var src = Path.Combine(_dir, "print.pdf");
        var pdf = Pdf(40);
        File.WriteAllBytes(src, pdf);
        await WaitFor(() => Count == 1);
        var (id, path, _) = _events[0];
        Assert.Equal(Path.Combine(_dir, "jobs", id.ToString() + ".pdf"), path);
        Assert.Equal(pdf, File.ReadAllBytes(path));
        Assert.False(File.Exists(src));
        await Task.Delay(3000);
        Assert.Equal(1, Count);
        Assert.Equal(0, ErrorCount);
    }

    [Fact]
    public async Task LockedAndGrowingFileWaitsUntilReleased()
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        DateTime release;
        using (var fs = new FileStream(src, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            fs.Write(Encoding.ASCII.GetBytes("%PDF-"));
            for (int i = 0; i < 6; i++)
            {
                fs.Write(new byte[1000]);
                fs.Flush();
                await Task.Delay(500);
            }
            Assert.Equal(0, Count); // still locked after 3 s
            release = DateTime.UtcNow;
        }
        await WaitFor(() => Count == 1);
        Assert.True(_events[0].at >= release, "raised before the writer released the file");
        Assert.Equal(6005, new FileInfo(_events[0].path).Length);
        await Task.Delay(2500);
        Assert.Equal(1, Count);
    }

    [Fact]
    public async Task SecondJobAfterFirstIsHandled()
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        File.WriteAllBytes(src, Pdf(10, (byte)'1'));
        await WaitFor(() => Count == 1);
        File.WriteAllBytes(src, Pdf(12, (byte)'2'));
        await WaitFor(() => Count == 2);
        Assert.NotEqual(_events[0].id, _events[1].id);
        Assert.Equal(Pdf(12, (byte)'2'), File.ReadAllBytes(_events[1].path));
        Assert.Equal(Pdf(10, (byte)'1'), File.ReadAllBytes(_events[0].path));
    }

    [Fact]
    public async Task FileDeletedMidCheckIsTolerated()
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        File.WriteAllBytes(src, Pdf());
        await Task.Delay(300);
        File.Delete(src);
        await Task.Delay(2500);
        Assert.Equal(0, Count);
        File.WriteAllBytes(src, Pdf(20));
        await WaitFor(() => Count == 1);
    }

    [Fact]
    public void StopIsIdempotent()
    {
        var w = Make();
        w.Stop();
        w.Start();
        w.Stop();
        w.Stop();
        w.Start(); // restart after Stop works (the old CTS was disposed, a new one is made)
        w.Stop();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NonPdfIsDeletedWithErrorAndNotRaised(int kind)
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        File.WriteAllBytes(src, kind == 0 ? Array.Empty<byte>() : Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 postscript"));
        await WaitFor(() => ErrorCount >= 1);
        await WaitFor(() => !File.Exists(src), 3000);
        Assert.Equal(0, Count);
        Assert.Contains("not a PDF", _errors[0].reason);
        Assert.Empty(Directory.GetFiles(w.JobsDir));
    }

    [Fact]
    public async Task PermanentMoveFailureRaisesErrorOnceAndNoJob()
    {
        var logs = new List<string>();
        using var w = Make();
        w.Log = m => { lock (logs) logs.Add(m); };
        w.Start();
        Directory.Delete(w.JobsDir);
        File.WriteAllText(w.JobsDir, "a file where the jobs dir should be");
        File.WriteAllBytes(w.PrintPath, Pdf());
        await WaitFor(() => ErrorCount >= 1);
        await Task.Delay(6000); // several more failed attempts: still one Error (rate-limited per reason)
        Assert.Equal(1, ErrorCount);
        Assert.Equal(0, Count);
        Assert.True(File.Exists(w.PrintPath)); // the job is not lost
        Assert.Contains("move", _errors[0].reason, StringComparison.OrdinalIgnoreCase);

        // once the cause is fixed the job goes through
        File.Delete(w.JobsDir);
        Directory.CreateDirectory(w.JobsDir);
        await WaitFor(() => Count == 1);
        Assert.Equal(1, ErrorCount);
    }

    [Fact]
    public async Task StopWhileAJobIsBeingProcessedLeavesItAlone()
    {
        var w = Make();
        w.Start();
        File.WriteAllBytes(w.PrintPath, Pdf());
        await Task.Delay(700); // inside the 1 s age wait / size recheck
        w.Stop();
        await Task.Delay(2500);
        Assert.Equal(0, Count);
        Assert.Equal(0, ErrorCount);
        Assert.True(File.Exists(w.PrintPath));
        Assert.Empty(Directory.GetFiles(w.JobsDir));
    }
}
