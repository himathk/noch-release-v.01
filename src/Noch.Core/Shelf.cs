using System.Text.Json;

namespace Noch.Core;

public enum ShelfKind { File, Link, Text }

public sealed record ShelfItem(ShelfKind Kind, string Value)
{
    public string Name => Kind switch
    {
        ShelfKind.File => Path.GetFileName(Value.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Value,
        ShelfKind.Link => Uri.TryCreate(Value, UriKind.Absolute, out var u) ? (u.Host + u.AbsolutePath).TrimEnd('/') : Value,
        _ => Describe.OneLine(Value, 60)
    };
}

/// A place to set files down for a moment. Holds where they are, never copies or moves them.
public sealed class Shelf
{
    public const int Max = 24;
    const int MaxText = 100_000;
    readonly List<ShelfItem> _items = [];
    readonly string? _save;
    public event Action? Changed;
    public IReadOnlyList<ShelfItem> Items => [.. _items];

    public Shelf(string? save = null)
    {
        _save = save;
        try { if (save != null && File.Exists(save)) _items.AddRange(JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(save)) ?? []); } catch { }
        _items.RemoveAll(i => i?.Value is null || !Enum.IsDefined(i.Kind));
    }

    public static ShelfItem? FromText(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxText) return null;
        return new(Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? ShelfKind.Link : ShelfKind.Text, text);
    }

    public int Add(IEnumerable<ShelfItem> items)
    {
        var added = 0;
        foreach (var i in items)
        {
            if (_items.Any(x => x.Kind == i.Kind && string.Equals(x.Value, i.Value, i.Kind == ShelfKind.File ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) continue;
            _items.Insert(0, i);
            added++;
        }
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        if (added > 0) Save();
        return added;
    }

    public void Remove(ShelfItem i) { if (_items.Remove(i)) Save(); }
    public void Clear() { if (_items.Count == 0) return; _items.Clear(); Save(); }

    /// Forgets files that have been moved or deleted.
    public void Prune(Func<string, bool> exists)
    {
        if (_items.RemoveAll(i => i.Kind == ShelfKind.File && !exists(i.Value)) > 0) Save();
    }

    void Save()
    {
        try
        {
            if (_save != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_save)!);
                File.WriteAllText(_save, JsonSerializer.Serialize(_items));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke();
    }
}
