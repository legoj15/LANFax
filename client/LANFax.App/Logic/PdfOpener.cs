using LANFax.Core;

namespace LANFax.App.Logic;

public enum PdfOpenOutcome { Opened, OpenedCachedCopy, Unauthorized, NoPdf, Failed }

/// <summary>What happened, and the line for the window's message bar (null: show the usual hint).</summary>
public sealed record PdfOpenResult(PdfOpenOutcome Outcome, string? Message);

/// <summary>
/// Double-click on a fax: download its PDF to <c>&lt;cacheDir&gt;\&lt;id&gt;.pdf</c> (fresh each time: the server
/// says no-store), then open it. Only when the finished download cannot be put in place because a viewer holds the
/// copy there (<see cref="PdfFileInUseException"/>) is that copy opened. A broken or stalled download, or a cache
/// folder that cannot take the file (disk full, read-only), is an error, never a silent stale copy.
/// Owner (UI) thread only.
/// </summary>
public sealed class PdfOpener(Func<IMonitorApi> api, string cacheDir, Action<string> openFile)
{
    private readonly HashSet<string> _opening = [];

    /// <returns>Null when this fax is already being opened (a double double-click).</returns>
    public async Task<PdfOpenResult?> OpenAsync(string id) => await OpenAsync(id, PdfOpenerKind.Pdf);

    /// <summary>The fax's printable confirmation sheet (server-built PDF), from <c>confirm-&lt;id&gt;.pdf</c> in the cache.</summary>
    public async Task<PdfOpenResult?> OpenConfirmationAsync(string id) => await OpenAsync(id, PdfOpenerKind.Confirmation);

    private async Task<PdfOpenResult?> OpenAsync(string id, PdfOpenerKind kind)
    {
        var what = kind == PdfOpenerKind.Confirmation ? "the confirmation sheet" : "the PDF";
        if (!_opening.Add(kind + id)) return null;
        try
        {
            var path = kind == PdfOpenerKind.Confirmation
                ? MonitorModel.ConfirmationCachePath(cacheDir, id)
                : MonitorModel.PdfCachePath(cacheDir, id);
            var outcome = PdfOpenOutcome.Opened;
            string? message = null;
            try
            {
                if (kind == PdfOpenerKind.Confirmation) await api().DownloadConfirmationAsync(id, path);
                else await api().DownloadPdfAsync(id, path);
            }
            catch (PdfFileInUseException e) when (File.Exists(path))
            {
                FileLog.Info($"monitor: {what} {id} not replaced ({e.InnerException?.GetType().Name}: {e.Message}); opening the cached copy");
                outcome = PdfOpenOutcome.OpenedCachedCopy;
                message = kind == PdfOpenerKind.Confirmation
                    ? "The confirmation sheet is open in another program; showing the copy already on this PC."
                    : "The PDF is open in another program; showing the copy already on this PC.";
            }
            openFile(path);
            return new PdfOpenResult(outcome, message);
        }
        catch (FaxApiUnauthorizedException) { return new PdfOpenResult(PdfOpenOutcome.Unauthorized, NotificationText.WindowUnauthorized); }
        catch (FaxApiException e) when (e.StatusCode == 404)
        {
            return new PdfOpenResult(PdfOpenOutcome.NoPdf, kind == PdfOpenerKind.Confirmation
                ? "The server has no confirmation sheet for this fax."
                : "The server has no PDF for this fax.");
        }
        catch (Exception e) when (e is FaxApiException or IOException or UnauthorizedAccessException or ArgumentException
                                      or System.ComponentModel.Win32Exception)
        {
            FileLog.Error($"monitor: opening {what} of {id} failed", e);
            return new PdfOpenResult(PdfOpenOutcome.Failed, "Could not open " + what + ": " + e.Message);
        }
        finally { _opening.Remove(kind + id); }
    }

    private enum PdfOpenerKind { Pdf, Confirmation }
}
