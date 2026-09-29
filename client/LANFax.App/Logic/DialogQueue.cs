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

    /// <summary>The current dialog closed: returns the next one to show, or null.</summary>
    public T? Complete()
    {
        Current = _waiting.Count > 0 ? _waiting.Dequeue() : null;
        return Current;
    }

    public string Title(string baseTitle) => Waiting == 0 ? baseTitle : $"{baseTitle} (1 of {Waiting + 1} waiting)";
}

/// <summary>What a send dialog is opened for: a spooled PDF (possibly a retry with prefilled fields) or, with no PDF, a test page.</summary>
public sealed record SendRequest(Guid SpoolId, string? PdfPath, string? Number = null, string? Subject = null);

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
