using LANFax.Core;

namespace LANFax.App.Logic;

/// <summary>One dialog at a time: print jobs that arrive while a send dialog is open wait their turn.</summary>
public sealed class DialogQueue<T> where T : class
{
    private readonly Queue<T> _waiting = new();

    public T? Current { get; private set; }
    public int Waiting => _waiting.Count;

    /// <summary>Returns the item when it should be shown now (nothing open), else null (queued).</summary>
    public T? Enqueue(T item)
    {
        if (Current == null) { Current = item; return item; }
        _waiting.Enqueue(item);
        return null;
    }

    public bool Contains(Func<T, bool> match) => (Current != null && match(Current)) || _waiting.Any(match);

    /// <summary>The current dialog closed: returns the next one to show, or null.</summary>
    public T? Complete()
    {
        Current = _waiting.Count > 0 ? _waiting.Dequeue() : null;
        return Current;
    }

    /// <summary>
    /// Opens <paramref name="item"/> (the current one). When opening throws, reports it, moves on and opens the
    /// next, so one broken item never wedges the queue.
    /// </summary>
    public void Show(T? item, Action<T> open, Action<T, Exception> failed)
    {
        while (item != null)
        {
            try { open(item); return; }
            catch (Exception e) { failed(item, e); }
            item = Complete();
        }
    }

    public string Title(string baseTitle) => Waiting == 0 ? baseTitle : $"{baseTitle} ({Waiting} more waiting)";
}

/// <summary>Why a send dialog is opened; decides its title, prefill, lock and warning.</summary>
public enum SendKind
{
    /// <summary>A fresh print job (or an unsent PDF with no record).</summary>
    New,
    /// <summary>The server reported the job failed: prefilled, fresh key.</summary>
    Retry,
    /// <summary>An earlier attempt was definitely not accepted: prefilled, fresh key.</summary>
    Draft,
    /// <summary>A send was in flight or uncertain when the app stopped: locked to the stored key and plan.</summary>
    Resume,
    /// <summary>The record is unreadable or the server no longer knows the job: may already have been sent; confirm before a fresh key is used.</summary>
    Suspect,
}

/// <summary>What a send dialog is opened for: a spooled PDF or, with no PDF, a test page.</summary>
public sealed record SendRequest(Guid SpoolId, string? PdfPath, string? Number = null, string? Subject = null,
    SendKind Kind = SendKind.New, SendPlan? LockedPlan = null)
{
    public bool NeedsConfirmation => Kind == SendKind.Suspect;
}

/// <summary>The event stream's 401 balloon: once per app run, re-armed when Settings are saved.</summary>
public sealed class UnauthorizedNotice
{
    private bool _shown;

    public bool ShouldShow(Exception? e)
    {
        if (_shown || e is not FaxApiException { StatusCode: 401 }) return false;
        _shown = true;
        return true;
    }

    public void Reset() => _shown = false;
}

public enum OfferCategory { Unsent, Retry }

/// <summary>
/// Documents waiting for the user outside the dialog queue (a failed fax whose balloon was missed, a resumed send the
/// user postponed). Backs the tray items "Unsent documents (N)..." and "Retry failed fax (N)...". One entry per spool id.
/// </summary>
public sealed class OfferBook
{
    private readonly Dictionary<Guid, (OfferCategory Category, SendRequest Request)> _items = new();

    public void Add(OfferCategory category, SendRequest request) => _items[request.SpoolId] = (category, request);

    public void Remove(Guid spoolId) => _items.Remove(spoolId);

    /// <summary>Removes the entry; true only if it was still waiting (not yet opened or handled).</summary>
    public bool Take(Guid spoolId) => _items.Remove(spoolId);

    public int Count(OfferCategory category) => _items.Values.Count(v => v.Category == category);

    public IReadOnlyList<SendRequest> TakeAll(OfferCategory category)
    {
        var taken = _items.Values.Where(v => v.Category == category).Select(v => v.Request).ToList();
        foreach (var r in taken) _items.Remove(r.SpoolId);
        return taken;
    }

    public static string MenuText(OfferCategory category, int n) =>
        category == OfferCategory.Unsent ? $"Unsent documents ({n})..." : $"Retry failed fax ({n})...";
}
