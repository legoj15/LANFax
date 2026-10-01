using LANFax.Core;

namespace LANFax.App.Logic;

public enum PdfOpenOutcome { Opened, OpenedCachedCopy, Unauthorized, NoPdf, Failed }

/// <summary>What happened, and the line for the window's message bar (null: show the usual hint).</summary>
public sealed record PdfOpenResult(PdfOpenOutcome Outcome, string? Message);

/// <summary>
/// Double-click on a fax: download its PDF to <c>&lt;cacheDir&gt;\&lt;id&gt;.pdf</c> (fresh each time: the server
/// says no-store), then open it. When the copy there cannot be replaced (a viewer holds it open: Windows answers
/// access denied or a sharing violation), that copy is opened. A broken or stalled download is an error, never a
/// silent stale copy (<see cref="FaxApi.DownloadPdfAsync"/> raises those as <see cref="FaxApiException"/>).
/// Owner (UI) thread only.
/// </summary>
public sealed class PdfOpener(Func<IMonitorApi> api, string cacheDir, Action<string> openFile)
{
    public const string Unauthorized = "The server refused the token. Check it in Settings.";
    private readonly HashSet<string> _opening = [];

    /// <returns>Null when this fax is already being opened (a double double-click).</returns>
    public async Task<PdfOpenResult?> OpenAsync(string id)
    {
        if (!_opening.Add(id)) return null;
        try
        {
            var path = MonitorModel.PdfCachePath(cacheDir, id);
            var outcome = PdfOpenOutcome.Opened;
            string? message = null;
            try { await api().DownloadPdfAsync(id, path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && File.Exists(path))
            {
                FileLog.Info($"monitor: pdf {id} not replaced ({e.GetType().Name}: {e.Message}); opening the cached copy");
                outcome = PdfOpenOutcome.OpenedCachedCopy;
                message = "The PDF is open in another program; showing the copy already on this PC.";
            }
            openFile(path);
            return new PdfOpenResult(outcome, message);
        }
        catch (FaxApiUnauthorizedException) { return new PdfOpenResult(PdfOpenOutcome.Unauthorized, Unauthorized); }
        catch (FaxApiException e) when (e.StatusCode == 404) { return new PdfOpenResult(PdfOpenOutcome.NoPdf, "The server has no PDF for this fax."); }
        catch (Exception e) when (e is FaxApiException or IOException or UnauthorizedAccessException or ArgumentException
                                      or System.ComponentModel.Win32Exception)
        {
            FileLog.Error($"monitor: opening the pdf of {id} failed", e);
            return new PdfOpenResult(PdfOpenOutcome.Failed, "Could not open the PDF: " + e.Message);
        }
        finally { _opening.Remove(id); }
    }
}
