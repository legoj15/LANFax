namespace LANFax.App.Logic;

/// <summary>
/// Every tray balloon goes through here: one at a time, so a later balloon (e.g. "Fax sent" from a poll) never
/// replaces one whose click action is still pending. A notice ends on click, on close, or after
/// <see cref="Duration"/> (the caller runs a timer and reports <see cref="OnTimeout"/> with the notice id).
/// Double-advance guards: a stale timer id is ignored, and a Closed that arrives within <see cref="CloseGrace"/>
/// of showing a notice belongs to the balloon it replaced. UI thread only.
/// </summary>
public sealed class NotificationQueue(Action<NotificationQueue.Notice> show, Func<DateTime>? clock = null)
{
    public sealed record Notice(long Id, string Text, Action? OnClick);

    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(1);

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Queue<Notice> _waiting = new();
    private long _nextId;
    private DateTime _shownAt;

    public Notice? Current { get; private set; }
    public int Waiting => _waiting.Count;

    public void Enqueue(string text, Action? onClick = null)
    {
        _waiting.Enqueue(new Notice(++_nextId, text, onClick));
        if (Current == null) Advance();
    }

    /// <summary>The balloon was clicked: runs the current notice's action once, then shows the next.</summary>
    public void OnClicked()
    {
        var c = Current;
        if (c == null) return;
        try { c.OnClick?.Invoke(); }
        finally { Advance(); }
    }

    public void OnClosed()
    {
        if (Current == null || _now() - _shownAt < CloseGrace) return;
        Advance();
    }

    public void OnTimeout(long id)
    {
        if (Current?.Id != id) return;
        Advance();
    }

    private void Advance()
    {
        Current = _waiting.Count > 0 ? _waiting.Dequeue() : null;
        if (Current == null) return;
        _shownAt = _now();
        show(Current);
    }
}
