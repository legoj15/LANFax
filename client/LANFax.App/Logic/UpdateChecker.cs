using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LANFax.App.Logic;

/// <summary>Why an update check or download failed; the message is safe to show the user.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A published release that is newer than the running app.</summary>
public sealed record UpdateInfo(Version Version, Uri Url, long Size, string Sha256, string Notes);

/// <summary>
/// Looks for a newer stable release on GitHub and downloads its installer. The installer runs elevated, so a release
/// is only offered when it names a SHA-256 for the installer asset (GitHub records one on every upload) and a
/// download is only kept when it matches that checksum and size.
/// </summary>
public sealed class UpdateChecker
{
    public const string Repository = "legoj15/LANFax";
    public const string InstallerAsset = "LANFax-Setup.exe";
    public static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>The installer is ~70 MB; anything far past that is not it.</summary>
    private const long MaxInstallerBytes = 500L * 1024 * 1024;
    private static readonly Regex TagPattern = new(@"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^sha256:([0-9a-fA-F]{64})$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly Uri _latest;
    private readonly TimeSpan _checkTimeout;
    private readonly TimeSpan _downloadTimeout;

    /// <param name="http">Caller-owned; give it an infinite Timeout: the two calls below carry their own limits.</param>
    /// <param name="latestUrl">Only the tests change this.</param>
    /// <param name="checkTimeout">Limit for the release lookup (30 s).</param>
    /// <param name="downloadTimeout">Limit for the whole installer download (15 min: ~0.6 Mbit/s still finishes).</param>
    public UpdateChecker(HttpClient http, Uri? latestUrl = null, TimeSpan? checkTimeout = null, TimeSpan? downloadTimeout = null)
    {
        _http = http;
        _latest = latestUrl ?? LatestReleaseUrl;
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(30);
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(15);
    }

    /// <summary>The running app's version (assembly informational version without the "+commit" suffix).</summary>
    public static Version CurrentVersion()
    {
        var asm = typeof(UpdateChecker).Assembly;
        var text = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        if (ParseTag(text) is { } v) return v;
        var a = asm.GetName().Version;
        return a == null ? new Version(0, 0, 0) : new Version(a.Major, a.Minor, Math.Max(a.Build, 0));
    }

    /// <summary>"v1.2.3", "1.2" and the like as a three-part version; null for pre-releases and anything else.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (tag == null) return null;
        var m = TagPattern.Match(tag.Trim());
        if (!m.Success) return null;
        try { return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0); }
        catch (OverflowException) { return null; }
    }

    /// <summary>The newest stable release when it is newer than <paramref name="current"/>; null when not (or none exists yet).</summary>
    /// <exception cref="UpdateException">GitHub unreachable or answering oddly, or the newest release is unusable.</exception>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken userCt = default)
    {
        string body;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        limit.CancelAfter(_checkTimeout);
        var ct = limit.Token;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _latest);
            req.Headers.TryAddWithoutValidation("User-Agent", "LANFax/" + current);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(true);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release published yet
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}".TrimEnd());
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            if (userCt.IsCancellationRequested) throw;
            if (e is OperationCanceledException) throw new UpdateException("the request timed out", e);
            throw new UpdateException("GitHub could not be reached (" + e.Message + ")", e);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Pick(doc.RootElement, current);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new UpdateException("GitHub's answer could not be understood", e);
        }
    }

    private static UpdateInfo? Pick(JsonElement root, Version current)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new UpdateException("GitHub's answer was not a release");
        if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
        var version = ParseTag(Text(root, "tag_name"));
        if (version == null || version <= current) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new UpdateException($"Release {version} has no {InstallerAsset} to download");
        foreach (var a in assets.EnumerateArray())
        {
            if (Text(a, "name") != InstallerAsset) continue;
            var digest = DigestPattern.Match(Text(a, "digest") ?? "");
            if (!digest.Success) throw new UpdateException($"Release {version} has no checksum for {InstallerAsset}, so it will not be installed");
            if (!Uri.TryCreate(Text(a, "browser_download_url"), UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new UpdateException($"Release {version} points somewhere other than github.com, so it will not be installed");
            long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : -1;
            if (size <= 0 || size > MaxInstallerBytes) throw new UpdateException($"Release {version} lists an unusable size for {InstallerAsset}");
            return new UpdateInfo(version, url, size, digest.Groups[1].Value.ToLowerInvariant(), Text(root, "body") ?? "");
        }
        throw new UpdateException($"Release {version} has no {InstallerAsset} to download");
    }

    private static string? Text(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Downloads the installer into <paramref name="dir"/> and returns its path, only once size and SHA-256 match;
    /// otherwise nothing is left behind. Installers of earlier updates in the folder are removed first.
    /// </summary>
    /// <exception cref="UpdateException">The download failed or did not match.</exception>
    public async Task<string> DownloadAsync(UpdateInfo info, string dir, CancellationToken userCt = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(userCt);
        limit.CancelAfter(_downloadTimeout);
        var ct = limit.Token;
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "LANFax-Setup-*"))
            try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var final = Path.Combine(dir, $"LANFax-Setup-{info.Version}.exe");
        var part = final + ".part";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.Url);
            req.Headers.TryAddWithoutValidation("User-Agent", "LANFax/" + CurrentVersion());
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(true);
            if (!resp.IsSuccessStatusCode) throw new UpdateException($"the server answered {(int)resp.StatusCode} {resp.ReasonPhrase}".TrimEnd());
            using var sha = SHA256.Create();
            long total = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(true))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(true)) > 0)
                {
                    total += n;
                    if (total > info.Size) throw new UpdateException("the file is larger than the release says");
                    sha.TransformBlock(buf, 0, n, null, 0);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(true);
                }
                sha.TransformFinalBlock([], 0, 0);
            }
            if (total != info.Size) throw new UpdateException($"the file is {total} bytes, the release says {info.Size}");
            if (!Convert.ToHexString(sha.Hash!).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("the file's checksum does not match the release");
            File.Move(part, final, overwrite: true);
            return final;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (userCt.IsCancellationRequested) throw;
            throw new UpdateException(e is OperationCanceledException ? "the download timed out" : e.Message, e);
        }
        finally { try { File.Delete(part); } catch { } }
    }
}
