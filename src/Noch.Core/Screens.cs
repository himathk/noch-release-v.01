namespace Noch.Core;

/// A rectangle in real pixels, as Windows reports monitors.
public readonly record struct Px(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// One monitor. The notch measures in this screen's own units: pixels divided by its scaling.
public sealed record ScreenInfo(string Name, Px Bounds, Px Work, double Scale, bool Primary)
{
    public double Width => Work.Width / Scale;
    public double Height => Work.Height / Scale;
    public (double X, double Y) ToArea(double px, double py) => ((px - Work.Left) / Scale, (py - Work.Top) / Scale);
    public (int X, int Y) ToPx(double x, double y) => ((int)Math.Round(Work.Left + x * Scale), (int)Math.Round(Work.Top + y * Scale));
}

public static class Screens
{
    /// The screen it was left on, or the main one while that is not plugged in.
    public static ScreenInfo Pick(IReadOnlyList<ScreenInfo> all, string? name) =>
        all.FirstOrDefault(s => name != null && s.Name == name) ?? all.FirstOrDefault(s => s.Primary) ?? all[0];

    public static ScreenInfo? At(IReadOnlyList<ScreenInfo> all, int x, int y) => all.FirstOrDefault(s => s.Bounds.Contains(x, y));

    /// The sides where the pointer carries on onto another screen.
    public static HashSet<Side> Open(ScreenInfo s, IReadOnlyList<ScreenInfo> all)
    {
        var open = new HashSet<Side>();
        var b = s.Bounds;
        foreach (var o in all)
        {
            if (o == s) continue;
            var c = o.Bounds;
            bool rows = c.Top < b.Bottom && c.Bottom > b.Top, columns = c.Left < b.Right && c.Right > b.Left;
            if (rows && c.Left == b.Right) open.Add(Side.Right);
            if (rows && c.Right == b.Left) open.Add(Side.Left);
            if (columns && c.Top == b.Bottom) open.Add(Side.Bottom);
            if (columns && c.Bottom == b.Top) open.Add(Side.Top);
        }
        return open;
    }

    /// Whether the pointer stops at this side of the usable area: no taskbar in between, and no screen beyond.
    public static bool Hard(ScreenInfo s, Side side, IReadOnlyList<ScreenInfo> all)
    {
        var flush = side switch
        {
            Side.Top => s.Work.Top == s.Bounds.Top, Side.Bottom => s.Work.Bottom == s.Bounds.Bottom,
            Side.Left => s.Work.Left == s.Bounds.Left, _ => s.Work.Right == s.Bounds.Right
        };
        return flush && !Open(s, all).Contains(side);
    }
}
