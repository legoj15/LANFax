using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LANFax.Core;

/// <summary>
/// Settings. Server, port, printer and spool live in the shared %ProgramData%\LANFax\config.json; the token lives in
/// the Windows user's own %LocalAppData%\LANFax\token.bin, DPAPI-protected for that user only, never plain.
/// </summary>
public sealed class ClientConfig
{
    public const string DefaultServer = "192.0.2.10";
    public const int DefaultPort = 8080;

    public string Server { get; set; } = DefaultServer;
    public int Port { get; set; } = DefaultPort;
    public string? Token { get; set; }
    public bool KeepLocalCopies { get; set; }
    public string PrinterName { get; set; } = "LAN Fax";
    public string SpoolDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LANFax", "spool");

    /// <summary>Throws (UriFormatException and the like) when <see cref="Validate"/> would not return null.</summary>
    public Uri BaseUri => new UriBuilder(Uri.UriSchemeHttp, Server, Port).Uri;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LANFax", "config.json");

    /// <summary>The current Windows user's token file.</summary>
    public static string DefaultTokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LANFax", "token.bin");

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
        /// <summary>Only read, to migrate a machine-scope token written by an older version; always saved as null.</summary>
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

    /// <summary>Why <see cref="Server"/>/<see cref="Port"/> cannot form a request URI, or null when they can. Never throws.</summary>
    public string? Validate()
    {
        if (Port is < 1 or > 65535) return $"port {Port} is not between 1 and 65535";
        return ServerError(Server);
    }

    private static string? ServerError(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return "no server is set";
        var bad = $"server \"{server}\" is not a host name or IP address (enter only the name or address; the port goes in the Port box)";
        if (Uri.CheckHostName(server) == UriHostNameType.Unknown) return bad;
        try
        {
            var u = new UriBuilder(Uri.UriSchemeHttp, server, DefaultPort).Uri;
            return u.AbsolutePath == "/" && u.Port == DefaultPort && string.IsNullOrEmpty(u.Query) ? null : bad;
        }
        catch (Exception e) when (e is UriFormatException or InvalidOperationException or ArgumentException) { return bad; }
    }

    /// <summary>
    /// Never throws: a missing file gives defaults; a blank, corrupt or unreadable one, an unusable server or port,
    /// or an undecryptable token gives defaults for those values and <see cref="LoadError"/>. A token still in the
    /// shared file (machine-scope, older versions) moves to <paramref name="tokenPath"/> and leaves the shared file.
    /// </summary>
    public static ClientConfig Load(string path, string tokenPath)
    {
        var c = new ClientConfig();
        var errors = new List<string>();
        FileModel? m = null;
        try
        {
            if (File.Exists(path))
            {
                m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), FileOptions);
                if (m == null) errors.Add($"{path} holds no settings object; using defaults");
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            errors.Add($"{path} could not be read ({e.GetType().Name}: {e.Message}); using defaults");
        }
        if (m != null)
        {
            if (!string.IsNullOrWhiteSpace(m.Server))
            {
                var s = m.Server.Trim();
                if (ServerError(s) is { } err) errors.Add($"{err}; using {c.Server}");
                else c.Server = s;
            }
            if (m.Port is int p)
            {
                if (p is >= 1 and <= 65535) c.Port = p;
                else errors.Add($"port {p} is not between 1 and 65535; using {c.Port}");
            }
            if (m.KeepLocalCopies is bool k) c.KeepLocalCopies = k;
            if (!string.IsNullOrWhiteSpace(m.PrinterName)) c.PrinterName = m.PrinterName;
            if (!string.IsNullOrWhiteSpace(m.SpoolDir)) c.SpoolDir = m.SpoolDir;
        }
        c.LoadToken(path, m, tokenPath, errors);
        c.LoadError = errors.Count == 0 ? null : string.Join("; ", errors);
        return c;
    }

    private void LoadToken(string path, FileModel? m, string tokenPath, List<string> errors)
    {
        byte[]? userBlob = null;
        try { if (File.Exists(tokenPath)) userBlob = File.ReadAllBytes(tokenPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"the saved token could not be read ({e.GetType().Name}); enter it again in Settings");
        }
        bool userFile = userBlob != null;
        if (userBlob != null)
        {
            try { Token = Encoding.UTF8.GetString(Dpapi.Unprotect(userBlob)); }
            catch (CryptographicException)
            {
                errors.Add("the saved token could not be decrypted for this Windows user; enter it again in Settings");
            }
        }
        else if (!string.IsNullOrEmpty(m?.Token))
        {
            // Migration: an older version kept a machine-scope token in the shared file.
            try { Token = Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(m.Token))); }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
            {
                errors.Add("the saved token could not be decrypted on this machine; enter it again in Settings");
                return; // leave the shared file alone: nothing was moved
            }
            try { WriteToken(tokenPath, Token); userFile = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { /* kept in memory; retried next start */ }
        }
        // Once this user has their own copy, the shared file must not keep a token any local user could decrypt.
        if (userFile && m is { Token.Length: > 0 })
        {
            m.Token = null;
            try { WriteShared(path, m); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not writable by this user */ }
        }
    }

    /// <summary>
    /// Writes the shared settings (never the token) to <paramref name="path"/> and the token, protected for the
    /// current Windows user, to <paramref name="tokenPath"/> (deleted when there is none).
    /// </summary>
    public void Save(string path, string tokenPath)
    {
        if (!string.IsNullOrEmpty(Token)) WriteToken(tokenPath, Token);
        else if (File.Exists(tokenPath)) File.Delete(tokenPath);
        WriteShared(path, new FileModel
        {
            Server = Server, Port = Port, Token = null, KeepLocalCopies = KeepLocalCopies, PrinterName = PrinterName, SpoolDir = SpoolDir,
        });
    }

    private static void WriteShared(string path, FileModel m) =>
        WriteAtomic(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, FileOptions)));

    private static void WriteToken(string tokenPath, string token) =>
        WriteAtomic(tokenPath, Dpapi.Protect(Encoding.UTF8.GetBytes(token), DpapiScope.CurrentUser));

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
