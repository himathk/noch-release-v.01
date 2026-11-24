namespace Noch.Core;

/// Most urgent last, so they can be compared.
public enum NoticeKind { Volume, Info, Power, App, Timer, Warning, Meeting }

/// A short message from something other than an agent.
public sealed record Notice(string Id, NoticeKind Kind, string Title, string Detail = "")
{
    public string? Body { get; init; }
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
    /// Second, quieter choice, such as muting an app.
    public string? SideLabel { get; init; }
    public Action? Side { get; init; }
    public TimeSpan Life { get; init; } = TimeSpan.FromSeconds(5);
    /// A picture for the app it came from. Opaque here, an image in the app.
    public object? Icon { get; init; }
    /// 0 to 1 for things that have a level, such as volume or battery.
    public double? Level { get; init; }
    public DateTime At { get; init; } = DateTime.UtcNow;
}

/// Holds messages until their time is up and says which one to show.
public sealed class Notices
{
    const int Max = 6;
    readonly object _gate = new();
    readonly List<Notice> _list = [];
    DateTime? _held;
    public event Action? Changed;

    /// The most urgent one; among equals, the oldest, so nothing is skipped. Volume always answers at once.
    public Notice? Current
    {
        get
        {
            lock (_gate)
                return _list.FirstOrDefault(n => n.Kind == NoticeKind.Volume) ?? _list.OrderByDescending(n => n.Kind).ThenBy(n => n.At).FirstOrDefault();
        }
    }

    /// Same id replaces, so a changing level does not pile up.
    public void Post(Notice n)
    {
        lock (_gate)
        {
            _list.RemoveAll(x => x.Id == n.Id);
            _list.Add(n);
            while (_list.Count > Max) _list.Remove(_list.OrderBy(x => x.Kind).ThenBy(x => x.At).First());
        }
        Changed?.Invoke();
    }

    public void Dismiss(string id)
    {
        bool any;
        lock (_gate) any = _list.RemoveAll(x => x.Id == id) > 0;
        if (any) Changed?.Invoke();
    }

    public void DismissKind(NoticeKind kind)
    {
        bool any;
        lock (_gate) any = _list.RemoveAll(x => x.Kind == kind) > 0;
        if (any) Changed?.Invoke();
    }

    readonly Dictionary<string, DateTime> _shownAt = [];

    /// Time only counts while a message is the one on screen, and not while the pointer is on it.
    public void Tick(DateTime now, bool hold)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var gone in _shownAt.Keys.Where(k => _list.All(n => n.Id + n.At.Ticks != k)).ToList()) _shownAt.Remove(gone);
            var c = Current;
            if (c is null) { _held = null; return; }
            var key = c.Id + c.At.Ticks;
            if (!_shownAt.TryGetValue(key, out var since)) _shownAt[key] = since = now;
            if (hold) { _held ??= now; return; }
            if (_held is { } h) { since += now - h; _shownAt[key] = since; _held = null; }
            if (now - since >= c.Life) { _list.Remove(c); _shownAt.Remove(key); changed = true; }
        }
        if (changed) Changed?.Invoke();
    }
}
