namespace Noch.Core;

public enum Side { Top, Bottom, Left, Right }

public readonly record struct Box(double X, double Y, double W, double H)
{
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;
    public bool Contains(double x, double y, double slack = 0) => x >= X - slack && x <= X + W + slack && y >= Y - slack && y <= Y + H + slack;
}

/// Where the notch hangs: an edge, and how far along it (0 to 1). 0 and 1 on the top or bottom are the corners.
public readonly record struct Dock(Side Side, double Along)
{
    public static readonly Dock TopCentre = new(Side.Top, .5);
    const double CornerZone = 130, CentreZone = 80, Margin = 24;

    public bool Vertical => Side is Side.Left or Side.Right;
    public bool AtStart => !Vertical && Along <= 0;
    public bool AtEnd => !Vertical && Along >= 1;
    public bool Corner => AtStart || AtEnd;

    /// Distance of a point from one edge of a w by h area.
    public static double Depth(Side s, double x, double y, double w, double h) => s switch { Side.Top => y, Side.Bottom => h - y, Side.Left => x, _ => w - x };

    public static Side NearestSide(double x, double y, double w, double h) =>
        new[] { Side.Top, Side.Bottom, Side.Left, Side.Right }.MinBy(s => Depth(s, x, y, w, h));

    /// The place a notch dropped at this point should settle. Corners and edge centres pull it in.
    public static Dock Nearest(double x, double y, double w, double h)
    {
        var side = NearestSide(x, y, w, h);
        var vertical = side is Side.Left or Side.Right;
        double along = vertical ? y : x, length = vertical ? h : w;
        if (along < CornerZone || along > length - CornerZone)
            return vertical ? new(along < CornerZone ? Side.Top : Side.Bottom, side == Side.Left ? 0 : 1) : new(side, along < CornerZone ? 0 : 1);
        if (Math.Abs(along - length / 2) < CentreZone) return new(side, .5);
        return new(side, along / length);
    }

    /// The rectangle a body of this size takes up when hanging here.
    public Box Place(double w, double h, double areaW, double areaH)
    {
        var along = Along;
        double Slide(double size, double length) => length <= size + 2 * Margin ? (length - size) / 2 : Math.Clamp(along * length - size / 2, Margin, length - size - Margin);
        var x = Side switch { Side.Left => 0, Side.Right => areaW - w, _ => AtStart ? 0 : AtEnd ? areaW - w : Slide(w, areaW) };
        var y = Side switch { Side.Top => 0, Side.Bottom => areaH - h, _ => Slide(h, areaH) };
        return new(x, y, w, h);
    }

    public override string ToString() => FormattableString.Invariant($"{Side}:{Along:0.####}");

    public static Dock Parse(string? s)
    {
        var p = s?.Split(':');
        return p is { Length: 2 } && Enum.TryParse<Side>(p[0], out var side) && Enum.IsDefined(side)
            && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) && a is >= 0 and <= 1
            ? new(side, a) : TopCentre;
    }
}
