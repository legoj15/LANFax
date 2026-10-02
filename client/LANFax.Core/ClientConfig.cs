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
    public const string DefaultServer = "192.168.168.168";
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

    /// <summary>What <see cref="TokenError"/> says about a token that cannot be sent (never the token itself).</summary>
    public const string TokenRule = "Token must be plain ASCII without spaces";

    /// <summary>
    /// Why <see cref="Server"/>/<see cref="Port"/> cannot form a request URI, or why <see cref="PrinterName"/> or
    /// <see cref="SpoolDir"/> cannot go into the elevated install command line; null when all can. Never throws.
    /// </summary>
    public string? Validate() =>
        EndpointError() ?? QuoteError("printer name", PrinterName) ?? QuoteError("spool folder", SpoolDir);

    /// <summary>Why <see cref="Server"/>/<see cref="Port"/> cannot form a request URI, or null when they can. Never throws.</summary>
    public string? EndpointError()
    {
        if (Port is < 1 or > 65535) return $"port {Port} is not between 1 and 65535";
        return ServerError(Server);
    }

    /// <summary>
    /// Null when <paramref name="token"/> can be sent as an HTTP <c>Bearer</c> parameter (visible ASCII, no quote or
    /// backslash) or is empty (no token); else <see cref="TokenRule"/>. A token that fails here would make the request
    /// throw only once it is on its way, so it is refused before anything is sent.
    /// </summary>
    public static string? TokenError(string? token) =>
        string.IsNullOrEmpty(token) || token.All(c => c is > ' ' and <= '~' and not '"' and not '\\') ? null : TokenRule;

    /// <summary>The printer name and spool folder are quoted into one elevated command line: a quote would end the argument.</summary>
    private static string? QuoteError(string what, string? value) =>
        value != null && value.Contains('"') ? $"the {what} \"{value}\" contains a quote (\")" : null;

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
            if (!string.IsNullOrWhiteSpace(m.PrinterName))
            {
                if (QuoteError("printer name", m.PrinterName) is { } err) errors.Add($"{err}; using \"{c.PrinterName}\"");
                else c.PrinterName = m.PrinterName;
            }
            if (!string.IsNullOrWhiteSpace(m.SpoolDir))
            {
                if (QuoteError("spool folder", m.SpoolDir) is { } err) errors.Add($"{err}; using {c.SpoolDir}");
                else c.SpoolDir = m.SpoolDir;
            }
        }
        c.LoadToken(path, m, tokenPath, errors);
        c.LoadError = errors.Count == 0 ? null : string.Join("; ", errors);
        return c;
    }

    /// <summary>
    /// The user's own <c>token.bin</c> first; when it is missing or does not decrypt for this user, a machine-scope
    /// token still in the shared file (older versions) is migrated into it; so is one whose token cannot be sent
    /// (<see cref="TokenError"/>). The shared token is removed only once a usable token decrypted here AND sits in
    /// this user's file: an undecryptable or unusable user file never costs the only copy.
    /// </summary>
    private void LoadToken(string path, FileModel? m, string tokenPath, List<string> errors)
    {
        byte[]? userBlob = null;
        try { if (File.Exists(tokenPath)) userBlob = File.ReadAllBytes(tokenPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"the saved token could not be read ({e.GetType().Name}); enter it again in Settings");
        }
        string? token = null;
        bool inUserFile = false;
        string? userError = null;
        if (userBlob != null)
        {
            try
            {
                var own = Encoding.UTF8.GetString(Dpapi.Unprotect(userBlob)).Trim();
                // A token that cannot be sent is no better than none: fall through to the shared file's, if any.
                if (TokenError(own) is { } bad) userError = $"the saved token cannot be used ({bad}); enter it again in Settings";
                else { token = own; inUserFile = true; }
            }
            catch (CryptographicException)
            {
                userError = "the saved token could not be decrypted for this Windows user; enter it again in Settings";
            }
        }
        if (token == null && !string.IsNullOrEmpty(m?.Token))
        {
            // Migration: an older version kept a machine-scope token in the shared file.
            try { token = Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(m.Token))); }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
            {
                errors.Add(userError ?? "the saved token could not be decrypted on this machine; enter it again in Settings");
                return; // leave the shared file alone: nothing was moved
            }
            userError = null;
            try { WriteToken(tokenPath, token.Trim()); inUserFile = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { /* kept in memory; retried next start */ }
        }
        if (userError != null) errors.Add(userError);
        if (token != null)
        {
            token = token.Trim();
            if (TokenError(token) is { } bad) errors.Add($"the saved token cannot be used ({bad}); enter it again in Settings");
            else Token = token.Length == 0 ? null : token;
        }
        // Once this user's own file holds a working token, the shared file must not keep one any local user could decrypt.
        if (token != null && inUserFile && TokenError(token) == null && m is { Token.Length: > 0 })
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
    /// <remarks>The token is trimmed first (a pasted token often carries a newline or space).</remarks>
    public void Save(string path, string tokenPath)
    {
        Token = string.IsNullOrWhiteSpace(Token) ? null : Token.Trim();
        if (Token != null) WriteToken(tokenPath, Token);
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
            // Flushed to disk before the rename: a power cut after the rename must not leave an empty file.
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }
}
