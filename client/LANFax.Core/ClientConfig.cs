using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LANFax.Core;

/// <summary>%ProgramData%\LANFax\config.json. The token is DPAPI (machine scope) protected on disk, never plain.</summary>
public sealed class ClientConfig
{
    public string Server { get; set; } = "192.0.2.10";
    public int Port { get; set; } = 8080;
    public string? Token { get; set; }
    public bool KeepLocalCopies { get; set; }
    public string PrinterName { get; set; } = "LAN Fax";
    public string SpoolDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LANFax", "spool");

    public Uri BaseUri => new UriBuilder(Uri.UriSchemeHttp, Server, Port).Uri;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LANFax", "config.json");

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed class FileModel
    {
        public string? Server { get; set; }
        public int? Port { get; set; }
        public string? Token { get; set; }
        public bool? KeepLocalCopies { get; set; }
        public string? PrinterName { get; set; }
        public string? SpoolDir { get; set; }
    }

    /// <summary>
    /// Why the last <see cref="Load"/> fell back to defaults or dropped the token (null when the file was fine or
    /// absent). Safe to show the user: it never contains the token.
    /// </summary>
    public string? LoadError { get; private set; }

    /// <summary>Never throws: a missing file gives defaults; a blank, corrupt or unreadable one gives defaults and <see cref="LoadError"/>.</summary>
    public static ClientConfig Load(string path)
    {
        var c = new ClientConfig();
        FileModel? m;
        try
        {
            if (!File.Exists(path)) return c;
            m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), FileOptions);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            c.LoadError = $"{path} could not be read ({e.GetType().Name}: {e.Message}); using defaults";
            return c;
        }
        if (m == null)
        {
            c.LoadError = $"{path} holds no settings object; using defaults";
            return c;
        }
        if (!string.IsNullOrWhiteSpace(m.Server)) c.Server = m.Server;
        if (m.Port is int p) c.Port = p;
        if (m.KeepLocalCopies is bool k) c.KeepLocalCopies = k;
        if (!string.IsNullOrWhiteSpace(m.PrinterName)) c.PrinterName = m.PrinterName;
        if (!string.IsNullOrWhiteSpace(m.SpoolDir)) c.SpoolDir = m.SpoolDir;
        if (!string.IsNullOrEmpty(m.Token))
        {
            try
            {
                var plain = Dpapi.Unprotect(Convert.FromBase64String(m.Token));
                c.Token = Encoding.UTF8.GetString(plain);
            }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
            {
                c.Token = null; // unreadable (other machine or hand-edited): user re-enters it in Settings
                c.LoadError = "the saved token could not be decrypted on this machine; enter it again in Settings";
            }
        }
        return c;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string? prot = null;
        if (!string.IsNullOrEmpty(Token))
            prot = Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(Token)));
        var m = new FileModel { Server = Server, Port = Port, Token = prot, KeepLocalCopies = KeepLocalCopies, PrinterName = PrinterName, SpoolDir = SpoolDir };
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, FileOptions));
            File.Move(tmp, path, overwrite: true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
