using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Noch.Core;

namespace Noch;

/// What the notch becomes while it is being carried: it stretches off the edge like a drop of ink,
/// snaps free into a wobbling bubble, and is pulled back in by whichever edge it comes near.
/// Plain numbers and drawing, so it can be stepped frame by frame without a window.
sealed class Bubble
{
    const double Radius = 30, Break = 108, Grab = 74;
    public readonly Spring X = new(0), Y = new(0), R = new(16), Wobble = new(0), Base = new(0), Recoil = new(0);
    readonly Size _area;
    Side _recoilSide;
    double _recoilAt, _angle, _landing;
    double _offX, _offY;

    public Side? Attached { get; private set; }
    public Noch.Core.Dock? Target { get; private set; }
    public bool Landed { get; private set; }
    public bool Motion { get; init; } = true;
    /// How long the bar is while it melts into the edge.
    public const double Melted = Radius * 2.4;

    public Bubble(Noch.Core.Dock from, Box body, Size area, double px, double py)
    {
        _area = area; Attached = from.Side;
        // It swells out of the bar under the pointer.
        double at = from.Vertical ? body.CenterY : body.CenterX, length = from.Vertical ? body.H : body.W, depth = from.Vertical ? body.W : body.H;
        double half = Math.Max(0, length / 2 - 12), u = Math.Clamp(from.Vertical ? py : px, at - half, at + half), v = Math.Min(depth / 2, 18);
        var c = ToArea(from.Side, u, v);
        X.Snap(c.X); Y.Snap(c.Y); Base.Snap(u);
        R.Target = Radius;
    }

    /// (along the edge, away from the edge) to screen units, and back.
    Point ToArea(Side s, double u, double v) => s switch { Side.Top => new(u, v), Side.Bottom => new(u, _area.Height - v), Side.Left => new(v, u), _ => new(_area.Width - v, u) };
    static double Along(Side s, double x, double y) => s is Side.Left or Side.Right ? y : x;
    double Depth(Side s, double x, double y) => Noch.Core.Dock.Depth(s, x, y, _area.Width, _area.Height);

    public void Release()
    {
        if (Target != null) return;
        Target = Noch.Core.Dock.Nearest(Math.Clamp(X.Value, 0, _area.Width), Math.Clamp(Y.Value, 0, _area.Height), _area.Width, _area.Height);
        // Let go before it broke free: it belongs to the edge it is still stuck to.
        if (Attached is { } a && Target.Value.Side != a && Depth(a, X.Value, Y.Value) < Break)
        {
            var u = Along(a, X.Value, Y.Value);
            var len = a is Side.Left or Side.Right ? _area.Height : _area.Width;
            var edge = ToArea(a, Math.Clamp(u, 0, len), 1);
            Target = Noch.Core.Dock.Nearest(edge.X, edge.Y, _area.Width, _area.Height);
        }
        R.Target = 20;
    }

    public void Step(double dt, double px, double py)
    {
        if (Landed) return;
        double tx = px, ty = py;
        if (Target is { } d)
        {
            _landing += dt;
            var b = d.Place(d.Vertical ? 36 : 56, d.Vertical ? 56 : 36, _area.Width, _area.Height);
            tx = b.CenterX; ty = b.CenterY;
        }
        else if (Attached is { } s)
        {
            // Resists, like pulling something elastic.
            var pull = Math.Max(0, Depth(s, px, py));
            var p = ToArea(s, Along(s, px, py), 14 + pull * .62);
            tx = p.X; ty = p.Y;
        }
        X.Target = tx; Y.Target = ty;
        var (resp, damp) = !Motion ? (.05, 1.0) : Target != null ? (.32, .66) : Attached != null ? (.17, .8) : (.15, .7);
        X.Step(dt, resp, damp); Y.Step(dt, resp, damp);

        var near = Noch.Core.Dock.NearestSide(X.Value, Y.Value, _area.Width, _area.Height);
        if (Attached is { } a)
        {
            Base.Target = Along(a, X.Value, Y.Value);
            var depth = Depth(a, X.Value, Y.Value);
            if (depth > Break || Target is { } t && t.Side != a && depth > Grab)
            {
                // Snap. The edge recoils and the bubble jiggles.
                (_recoilSide, _recoilAt) = (a, Base.Value);
                Recoil.Value = 30; Recoil.Velocity = 260; Recoil.Target = 0;
                Attached = null;
                if (Motion) Wobble.Velocity += 5.5;
            }
        }
        else if (Depth(near, X.Value, Y.Value) < Grab)
        {
            Attached = near;
            Base.Snap(Along(near, X.Value, Y.Value));
            if (Motion) Wobble.Velocity -= 3;
        }
        Base.Step(dt, Target != null ? .05 : .2, .9);
        R.Step(dt, .34, .55); Recoil.Step(dt, .26, .38);
        Wobble.Step(dt, .27, .2);

        var speed = Math.Sqrt(X.Velocity * X.Velocity + Y.Velocity * Y.Velocity);
        if (speed > 60) _angle = Math.Atan2(Y.Velocity, X.Velocity) * 180 / Math.PI;

        if (Target != null && (_landing > .7 || Math.Abs(X.Value - tx) < 3 && Math.Abs(Y.Value - ty) < 3 && speed < 90)) Landed = true;
    }

    /// Everything that belongs to one edge: the neck, the recoil after a snap, the reach towards a nearby bubble.
    /// (ox, oy) is the top left of the picture, in screen units.
    public void DrawEdge(DrawingContext dc, Side side, double ox, double oy)
    {
        (_offX, _offY) = (ox, oy);
        var parts = new List<Geometry>();
        if (_recoilSide == side && Recoil.Value > .6) parts.Add(Mound(side, _recoilAt, Recoil.Value, 46));
        if (Attached == side) { parts.Add(Neck(side)); parts.Add(Ball()); }
        else if (Attached is null && Reach(side) is { } m) parts.Add(m);
        // Outlines first, then fills, so overlapping pieces read as one drop.
        foreach (var g in parts) dc.DrawGeometry(null, Shell.Edge, g);
        foreach (var g in parts) dc.DrawGeometry(Theme.Surface, null, g);
    }

    public void DrawBall(DrawingContext dc, double ox, double oy)
    {
        (_offX, _offY) = (ox, oy);
        // While it is attached the edge picture has the outline, which must not cut across the neck.
        dc.DrawGeometry(Theme.Surface, Attached is null ? Shell.Edge : null, Ball());
    }

    Point L(Side s, double u, double v) { var p = ToArea(s, u, v); return new(p.X - _offX, p.Y - _offY); }

    Geometry Ball()
    {
        var speed = Math.Sqrt(X.Velocity * X.Velocity + Y.Velocity * Y.Velocity);
        var k = Motion ? Math.Clamp(Math.Min(speed / 6500, .15) + Wobble.Value, -.24, .26) : 0;
        // Still held by the edge, so it has less freedom to wobble.
        if (Attached != null) k *= .3;
        var c = new Point(X.Value - _offX, Y.Value - _offY);
        var g = new EllipseGeometry(c, R.Value * (1 + k), R.Value * (1 - k * .8), new RotateTransform(_angle, c.X, c.Y));
        g.Freeze();
        return g;
    }

    /// The stretchy part between the edge and the bubble.
    Geometry Neck(Side s)
    {
        double d = Depth(s, X.Value, Y.Value), ub = Along(s, X.Value, Y.Value), r = R.Value;
        var t = Math.Clamp((d - r * .5) / (Break - r * .5), 0, 1);
        double foot = r * (2.3 - 1.15 * t), waist = Math.Max(4, r * (1.02 - .9 * Math.Pow(t, .85)));
        double v = Math.Max(d, r * .55), mid = v * .46, um = (Base.Value + ub) / 2;
        // Leaves the ball along its tangent, so there is no crease where the two meet.
        var lean = (68 - 26 * t) * Math.PI / 180;
        double sin = Math.Sin(lean), cos = Math.Cos(lean), shoulderV = v - r * cos * .98, shoulderU = r * sin * .98, reach = Math.Max(0, shoulderV - mid) * .62;
        double handleU = Math.Max(waist, shoulderU - cos * reach), handleV = shoulderV - sin * reach;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(L(s, Base.Value - foot, -3), true, true);
            c.LineTo(L(s, Base.Value - foot, 0), true, false);
            c.BezierTo(L(s, Base.Value - foot * .45, 0), L(s, um - waist, mid * .35), L(s, um - waist, mid), true, true);
            c.BezierTo(L(s, um - waist, mid + (shoulderV - mid) * .5), L(s, ub - handleU, handleV), L(s, ub - shoulderU, shoulderV), true, true);
            c.LineTo(L(s, ub + shoulderU, shoulderV), true, false);
            c.BezierTo(L(s, ub + handleU, handleV), L(s, um + waist, mid + (shoulderV - mid) * .5), L(s, um + waist, mid), true, true);
            c.BezierTo(L(s, um + waist, mid * .35), L(s, Base.Value + foot * .45, 0), L(s, Base.Value + foot, 0), true, true);
            c.LineTo(L(s, Base.Value + foot, -3), true, false);
        }
        g.Freeze();
        return g;
    }

    /// An edge that is close enough reaches out for the bubble.
    Geometry? Reach(Side s)
    {
        if (s != Noch.Core.Dock.NearestSide(X.Value, Y.Value, _area.Width, _area.Height)) return null;
        var d = Depth(s, X.Value, Y.Value);
        var t = Math.Clamp(1 - (d - Grab) / 80, 0, 1);
        return t <= 0 || !Motion ? null : Mound(s, Along(s, X.Value, Y.Value), t * t * 24, 34 + 20 * t);
    }

    Geometry Mound(Side s, double at, double h, double half)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(L(s, at - half * 1.5, -3), true, true);
            c.LineTo(L(s, at - half * 1.5, 0), true, false);
            c.BezierTo(L(s, at - half * .8, 0), L(s, at - half * .6, h), L(s, at, h), true, true);
            c.BezierTo(L(s, at + half * .6, h), L(s, at + half * .8, 0), L(s, at + half * 1.5, 0), true, true);
            c.LineTo(L(s, at + half * 1.5, -3), true, false);
        }
        g.Freeze();
        return g;
    }
}

/// Click-through, never takes focus.
abstract class GhostWindow : Window
{
    protected IntPtr Hwnd { get; private set; }
    protected GhostWindow(string title)
    {
        Title = title;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = null; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; IsHitTestVisible = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(Hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT, true);
    }
}

/// Lies along a whole edge and stays put, so what is stuck to the edge can never be carried off it.
sealed class EdgeWindow : GhostWindow
{
    const double Deep = 200;
    readonly Painter _painter;
    readonly Side _side;
    double _ox, _oy;
    Bubble? _bubble;

    sealed class Painter(EdgeWindow w) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc) => w._bubble?.DrawEdge(dc, w._side, w._ox, w._oy);
    }

    public EdgeWindow(Side side) : base("Noch edge") { _side = side; Content = _painter = new Painter(this); }

    public void Cover(Rect area)
    {
        var tall = _side is Side.Left or Side.Right;
        Width = tall ? Deep : area.Width; Height = tall ? area.Height : Deep;
        _ox = _side == Side.Right ? area.Width - Deep : 0; _oy = _side == Side.Bottom ? area.Height - Deep : 0;
        Left = area.Left + _ox; Top = area.Top + _oy;
    }

    public void Draw(Bubble? b) { _bubble = b; _painter.InvalidateVisual(); }
}

/// The round part only. This one follows the pointer.
sealed class BallWindow : GhostWindow
{
    const double Span = 160;
    readonly DotMatrix _dots = new(Dots.Idle, 1.35);
    readonly Painter _painter;
    readonly Bubble _bubble;
    readonly Rect _area;
    readonly double _scale;
    double _ox, _oy;

    sealed class Painter(BallWindow w) : Canvas
    {
        protected override void OnRender(DrawingContext dc) => w._bubble.DrawBall(dc, w._ox, w._oy);
    }

    public BallWindow(Bubble bubble, Rect area, double scale, Dots dots, Color tint) : base("Noch drag")
    {
        _bubble = bubble; _area = area; _scale = scale;
        Width = Height = Span;
        _dots.Kind = dots; _dots.Tint = tint; _dots.Opacity = 0;
        _painter = new Painter(this);
        _painter.Children.Add(_dots);
        Content = _painter;
        Left = area.Left + bubble.X.Value - Span / 2; Top = area.Top + bubble.Y.Value - Span / 2;
    }

    public Dots Kind { set => _dots.Kind = value; }

    public void Follow(double age)
    {
        // Whole pixels for the window; the fraction is drawn inside it.
        var px = Math.Round((_area.Left + _bubble.X.Value - Span / 2) * _scale); var py = Math.Round((_area.Top + _bubble.Y.Value - Span / 2) * _scale);
        Native.Move(Hwnd, (int)px, (int)py);
        _ox = px / _scale - _area.Left; _oy = py / _scale - _area.Top;
        _dots.Opacity = Math.Clamp((age - .08) / .14, 0, 1) * Math.Clamp((_bubble.R.Value - 19) / 6, 0, 1);
        Canvas.SetLeft(_dots, _bubble.X.Value - _ox - _dots.Width / 2); Canvas.SetTop(_dots, _bubble.Y.Value - _oy - _dots.Height / 2);
        _painter.InvalidateVisual();
    }
}

/// All the windows of one drag. The four edge windows are kept between drags.
sealed class DragScene
{
    static EdgeWindow[]? _edges;
    static int _shown;
    readonly BallWindow _ball;
    public Bubble Bubble { get; }
    public Dots Kind { set => _ball.Kind = value; }

    public DragScene(Bubble bubble, Rect area, double scale, Dots dots, Color tint)
    {
        Bubble = bubble;
        _edges ??= [new(Side.Top), new(Side.Bottom), new(Side.Left), new(Side.Right)];
        _shown++;
        foreach (var e in _edges) { e.Cover(area); e.Draw(bubble); e.Show(); }
        _ball = new BallWindow(bubble, area, scale, dots, tint);
        _ball.Show();
        _ball.Follow(0);
    }

    public void Draw(double age)
    {
        foreach (var e in _edges!) e.Draw(Bubble);
        _ball.Follow(age);
    }

    /// Takes everything away, once the bar underneath has had time to be drawn.
    public void Close()
    {
        int frames = 0, mine = _shown;
        void Away(object? s, EventArgs e)
        {
            frames++;
            if (frames == 4) { _ball.Close(); if (mine == _shown) foreach (var w in _edges!) w.Draw(null); }
            if (frames < 8) return;
            CompositionTarget.Rendering -= Away;
            // Hidden only after the empty picture is up, so an old one cannot flash next time.
            if (mine == _shown) foreach (var w in _edges!) w.Hide();
        }
        CompositionTarget.Rendering += Away;
    }
}
