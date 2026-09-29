using LANFax.Core;

namespace LANFax.Tests;

public class SpoolWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanfax-spool-" + Guid.NewGuid().ToString("N"));
    private readonly List<(Guid id, string path)> _events = new();

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SpoolWatcher Make()
    {
        var w = new SpoolWatcher(_dir);
        w.NewPrintJob += (id, p) => { lock (_events) _events.Add((id, p)); };
        return w;
    }

    private int Count { get { lock (_events) return _events.Count; } }

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
        File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4 });
        await WaitFor(() => Count == 1);
        var (id, path) = _events[0];
        Assert.Equal(Path.Combine(_dir, "jobs", id.ToString() + ".pdf"), path);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path));
        Assert.False(File.Exists(src));
        await Task.Delay(3000);
        Assert.Equal(1, Count);
    }

    [Fact]
    public async Task LockedAndGrowingFileWaitsUntilReleased()
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        var release = DateTime.UtcNow;
        using (var fs = new FileStream(src, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
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
        Assert.True(DateTime.UtcNow >= release);
        Assert.Equal(6000, new FileInfo(_events[0].path).Length);
        await Task.Delay(2500);
        Assert.Equal(1, Count);
    }

    [Fact]
    public async Task SecondJobAfterFirstIsHandled()
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        File.WriteAllBytes(src, new byte[] { 1 });
        await WaitFor(() => Count == 1);
        File.WriteAllBytes(src, new byte[] { 2, 2 });
        await WaitFor(() => Count == 2);
        Assert.NotEqual(_events[0].id, _events[1].id);
        Assert.Equal(new byte[] { 2, 2 }, File.ReadAllBytes(_events[1].path));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(_events[0].path));
    }

    [Fact]
    public async Task FileDeletedMidCheckIsTolerated()
    {
        using var w = Make();
        w.Start();
        var src = Path.Combine(_dir, "print.pdf");
        File.WriteAllBytes(src, new byte[] { 1 });
        await Task.Delay(300);
        File.Delete(src);
        await Task.Delay(2500);
        Assert.Equal(0, Count);
        File.WriteAllBytes(src, new byte[] { 9 });
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
    }
}
