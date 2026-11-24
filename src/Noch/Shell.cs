using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Noch.Core;

namespace Noch;

/// A value that moves towards its target like something soft on a spring.
sealed class Spring(double value)
{
    public double Value = value, Target = value, Velocity;
    public bool Settled => Math.Abs(Target - Value) < .05 && Math.Abs(Velocity) < .5;
    public void Snap(double v) { Value = Target = v; Velocity = 0; }
    public void Step(double dt, double response, double damping)
    {
        var w = 2 * Math.PI / response;
        for (var left = dt; left > 0; left -= .004)
        {
            var h = Math.Min(left, .004);
            Velocity += (w * w * (Target - Value) - 2 * damping * w * Velocity) * h;
            Value += Velocity * h;
        }
        if (Settled) Snap(Target);
    }
}

/// The black shape, hanging from any edge or corner. Draws itself and clips whatever is inside it.
sealed class Shell : Canvas
{
    /// Size, corner radius, flare where it meets the edge, and centre position along the edge (in screen units).
    public readonly Spring W = new(120), H = new(6), R = new(6), Ear = new(4), At = new(0);
    public Noch.Core.Dock Dock = Noch.Core.Dock.TopCentre;
    /// The usable screen, and where this element's top left corner sits in it.
    public Size Area = new(1920, 1080);
    public Point Origin;

    /// A faint outline so the shape still reads on dark windows. Half of it is clipped away, leaving 1 px.
    public static readonly Pen Edge = MakeEdge();
    static Pen MakeEdge() { var p = new Pen(Theme.Brush(Colors.White, .12), 2); p.Freeze(); return p; }

    /// The body right now, in screen units.
    public Box Body()
    {
        double w = Math.Max(0, W.Value), h = Math.Max(0, H.Value);
        var x = Dock.Side switch { Side.Left => 0, Side.Right => Area.Width - w, _ => Dock.AtStart ? 0 : Dock.AtEnd ? Area.Width - w : At.Value - w / 2 };
        var y = Dock.Side switch { Side.Top => 0, Side.Bottom => Area.Height - h, _ => At.Value - h / 2 };
        return new(x, y, w, h);
    }

    public Box LocalBody() { var b = Body(); return new(b.X - Origin.X, b.Y - Origin.Y, b.W, b.H); }

    public bool Empty => W.Value < .3 || H.Value < .3;

    public Geometry Shape()
    {
        var b = LocalBody();
        bool top = Dock.Side == Side.Top, bottom = Dock.Side == Side.Bottom, left = Dock.Side == Side.Left || Dock.AtStart, right = Dock.Side == Side.Right || Dock.AtEnd;
        // How much room each rounded corner and flare has.
        double along = Dock.Vertical ? b.H : b.W, depth = Dock.Vertical ? b.W : b.H;
        var ear = Math.Max(0, Math.Min(Ear.Value, Dock.Corner ? Math.Min(b.W, b.H) / 2 : depth));
        var r = Math.Max(0, Math.Min(R.Value, Math.Min(depth - ear, along / 2)));
        double x0 = b.X, y0 = b.Y, x1 = b.X + b.W, y1 = b.Y + b.H;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            Point? first = null;
            // The sides that lie on the screen edge get no outline.
            void Go(Point p, bool onEdge) { if (first is null) { c.BeginFigure(p, true, false); first = p; } else c.LineTo(p, !onEdge, false); }
            // One corner, walking clockwise. `inEdge` and `outEdge` say whether the side we arrive on or leave on is the screen edge.
            void Corner(Point p, Vector arrive, Vector leave, bool inEdge, bool outEdge)
            {
                if (inEdge && outEdge) { Go(p, true); return; }
                if (!inEdge && !outEdge)
                {
                    // Slightly fuller than a circle, like the smoothed corners in the design.
                    const double k = .64;
                    Point a = p - arrive * r, z = p + leave * r;
                    Go(a, false);
                    c.BezierTo(a + (p - a) * k, z + (p - z) * k, z, true, true);
                    return;
                }
                if (inEdge) { Go(p + arrive * ear, true); c.ArcTo(p + leave * ear, new Size(ear, ear), 90, false, SweepDirection.Counterclockwise, true, false); }
                else { Go(p - arrive * ear, false); c.ArcTo(p - leave * ear, new Size(ear, ear), 90, false, SweepDirection.Counterclockwise, true, false); }
            }
            Vector rt = new(1, 0), dn = new(0, 1), lf = new(-1, 0), up = new(0, -1);
            Corner(new(x0, y0), up, rt, left, top);
            Corner(new(x1, y0), rt, dn, top, right);
            Corner(new(x1, y1), dn, lf, right, bottom);
            Corner(new(x0, y1), lf, up, bottom, left);
            Go(first!.Value, left);
        }
        g.Freeze();
        return g;
    }

    /// Keeps content fixed to the edge it hangs from, so growing reveals it instead of moving it.
    void Arrange()
    {
        var b = Body();
        foreach (var v in Children.OfType<View>())
        {
            var w = v.Width; var h = double.IsNaN(v.Height) ? v.DesiredSize.Height : v.Height;
            var x = Dock.Side switch { Side.Left => 0, Side.Right => Area.Width - w, _ => Dock.AtStart ? 0 : Dock.AtEnd ? Area.Width - w : b.CenterX - w / 2 };
            var y = Dock.Side switch { Side.Top => 0, Side.Bottom => Area.Height - h, _ => b.CenterY - h / 2 };
            SetLeft(v, Math.Round(x - Origin.X)); SetTop(v, Math.Round(y - Origin.Y));
        }
    }

    public void Refresh()
    {
        Arrange();
        Clip = Shape();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc) { if (!Empty) dc.DrawGeometry(Theme.Surface, Edge, Shape()); }
}
