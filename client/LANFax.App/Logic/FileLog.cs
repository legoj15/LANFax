namespace LANFax.App.Logic;

/// <summary>Tiny rolling log (1 MB, one .1 backup). Callers must never pass the token or PDF contents.</summary>
public static class FileLog
{
    private static readonly object Gate = new();
    public const long MaxBytes = 1_000_000;

    public static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LANFax", "logs", "app.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? e = null) => Write("ERROR", e == null ? msg : $"{msg}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(Path, Path + ".1", overwrite: true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* logging must never break the app */ }
    }
}
