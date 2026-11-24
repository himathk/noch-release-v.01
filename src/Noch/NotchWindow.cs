using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
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

/// The black shape. Draws itself and clips whatever is inside it.
sealed class Shell : Grid
{
    public readonly Spring W = new(120), H = new(6), R = new(6), Ear = new(4);

    public Geometry Shape(double ox)
    {
        double w = Math.Max(1, W.Value), h = Math.Max(0, H.Value), ear = Math.Max(0, Math.Min(Ear.Value, h)), r = Math.Max(0, Math.Min(R.Value, Math.Min(h - ear, w / 2)));
        var l = ox - w / 2; var rt = ox + w / 2;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(l - ear, 0), true, false);
            c.ArcTo(new Point(l, ear), new Size(ear, ear), 90, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(l, h - r), true, false);
            Corner(c, new Point(l, h - r), new Point(l, h), new Point(l + r, h));
            c.LineTo(new Point(rt - r, h), true, false);
            Corner(c, new Point(rt - r, h), new Point(rt, h), new Point(rt, h - r));
            c.LineTo(new Point(rt, ear), true, false);
            c.ArcTo(new Point(rt + ear, 0), new Size(ear, ear), 90, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    // Slightly fuller than a circle, like the smoothed corners in the design.
    static void Corner(StreamGeometryContext c, Point a, Point corner, Point b)
    {
        const double k = .64;
        c.BezierTo(a + (corner - a) * k, b + (corner - b) * k, b, true, true);
    }

    public void Refresh()
    {
        Clip = Shape(ActualWidth / 2);
        InvalidateVisual();
    }

    // A faint outline so the shape still reads on dark windows. Half of it is clipped away, leaving 1 px.
    static readonly Pen Edge = MakeEdge();
    static Pen MakeEdge() { var p = new Pen(Theme.Brush(Colors.White, .12), 2); p.Freeze(); return p; }

    protected override void OnRender(DrawingContext dc) { if (H.Value > .2) dc.DrawGeometry(Theme.Surface, Edge, Shape(ActualWidth / 2)); }
}

sealed class NotchWindow : Window, IHost
{
    readonly Shell _shell = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) }, _poll = new() { Interval = TimeSpan.FromMilliseconds(80) }, _openSoon = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly Settings _settings;
    IntPtr _hwnd;
    View? _view;
    string _key = "";
    bool _peek, _player, _covered, _fullscreen, _hotkeys, _typing, _growing, _quiet;
    DateTime _dwell = DateTime.MaxValue, _outside = DateTime.MaxValue, _armAt = DateTime.MaxValue, _lastFrame, _lastProbe, _lastSweep, _pinnedUntil;

    public AgentHub Hub { get; }
    public MediaService Media { get; }
    public Func<IReadOnlyList<SetupItem>> SetupItems { get; set; } = () => [];
    public IReadOnlyList<SetupItem> Setup => SetupItems();
    public bool Armed => DateTime.UtcNow >= _armAt;
    public NotchState State { get; private set; } = NotchState.Rest;

    public NotchWindow(AgentHub hub, MediaService media, Settings settings)
    {
        Hub = hub; Media = media; _settings = settings;
        Title = "Noch"; Width = 620; Height = 540;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = null; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; UseLayoutRounding = true;
        _shell.HorizontalAlignment = HorizontalAlignment.Stretch; _shell.VerticalAlignment = VerticalAlignment.Stretch;
        Content = _shell;

        Hub.Changed += () => Dispatcher.BeginInvoke(Sync);
        Media.Changed += () => { DotMatrix.MusicPlaying = Media.Current?.Playing == true; Sync(); };
        Hub.Busy = () => _fullscreen;
        _tick.Tick += (_, _) => OnTick();
        _poll.Tick += (_, _) => Poll();
        _openSoon.Tick += (_, _) => { _openSoon.Stop(); if (_shell.IsMouseOver && Hoverable) OpenPeek(); };
        // Wait a little longer over a window, where the pointer may only be passing on its way to a tab.
        _shell.MouseEnter += (_, _) => { if (Hoverable) { _openSoon.Interval = TimeSpan.FromMilliseconds(_covered ? 350 : 150); _openSoon.Start(); } };
        _shell.MouseLeave += (_, _) => _openSoon.Stop();
        SizeChanged += (_, _) => _shell.Refresh();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
    }

    bool Hoverable => State is NotchState.Idle or NotchState.Agent or NotchState.Both or NotchState.Music;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, true);
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        Place();
        _tick.Start(); _poll.Start();
        Sync();
    }

    void Place()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
        Native.KeepOnTop(_hwnd);
        _shell.Refresh();
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE && !_typing) { handled = true; return Native.MA_NOACTIVATE; }
        if (msg == Native.WM_HOTKEY && Armed && Hub.Pending.FirstOrDefault() is { IsQuestion: false } p && State == NotchState.Approval)
        {
            handled = true;
            if (w == 1) Hub.Allow(p); else Hub.Deny(p);
        }
        return IntPtr.Zero;
    }

    // ---- IHost ----

    public void OpenPeek() { _peek = true; _player = false; Sync(); }
    public void OpenPlayer() { _player = true; _peek = false; Sync(); }
    public void Dismiss() { _peek = _player = false; _pinnedUntil = default; StopTyping(); Sync(); }
    public void Focus(string key) { if (Hub.Find(key) is { } s) Native.Focus(s.Pids); }
    public void Welcome() { _pinnedUntil = DateTime.UtcNow.AddSeconds(20); OpenPeek(); }

    public void TakeKeyboard(TextBox box)
    {
        _typing = true;
        Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, false);
        Activate();
        Resize();
        Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); }, DispatcherPriority.Input);
    }

    void StopTyping()
    {
        if (!_typing) return;
        _typing = false;
        Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE, true);
    }

    // ---- what to show ----

    bool MusicVisible => Media.Current is { } m && (!_settings.Shrink || m.Playing || DateTime.UtcNow - Media.LastPlaying < TimeSpan.FromSeconds(10));

    public void Sync()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_player && Media.Current is null) _player = false;
        State = Presenter.Choose(Hub, new Scene(_fullscreen, _covered, MusicVisible, _peek, _player, _settings.Shrink));
        var pending = Hub.Pending.FirstOrDefault();
        var key = State switch
        {
            NotchState.Approval or NotchState.Question => State + ":" + pending?.GetHashCode(),
            NotchState.Done or NotchState.Failed => State + ":" + Hub.Toast?.At.Ticks,
            _ => State.ToString()
        };
        if (key != _key)
        {
            View? next = State switch
            {
                NotchState.Hidden => new RestView(this, true),
                NotchState.Rest => new RestView(this, false),
                NotchState.Strip => new StripView(this),
                NotchState.Idle => new CompactView(this, false, false),
                NotchState.Music => new CompactView(this, false, true),
                NotchState.Agent => new CompactView(this, true, false),
                NotchState.Both => new CompactView(this, true, true),
                NotchState.Approval when pending != null => new ApprovalView(this, pending),
                NotchState.Question when pending != null => new QuestionView(this, pending),
                NotchState.Done or NotchState.Failed when Hub.Toast is { } t => new ToastView(this, t),
                NotchState.Peek => new PeekView(this),
                NotchState.Player => new PlayerView(this),
                _ => null
            };
            if (next is null) return; // it was answered while we were looking; the next change brings us back
            _key = key;
            _armAt = DateTime.UtcNow.AddMilliseconds(400);
            if (State != NotchState.Question) StopTyping();
            Swap(next);
        }
        else _view?.Update();
        Resize();
        HotKeys(State == NotchState.Approval);
        Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT, _view?.ClickThrough == true);
    }

    void HotKeys(bool on)
    {
        if (on == _hotkeys) return;
        _hotkeys = on;
        if (on)
        {
            Native.RegisterHotKey(_hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 'Y');
            Native.RegisterHotKey(_hwnd, 2, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 'N');
        }
        else { Native.UnregisterHotKey(_hwnd, 1); Native.UnregisterHotKey(_hwnd, 2); }
    }

    // ---- motion ----

    static bool Motion => SystemParameters.ClientAreaAnimation;

    void Swap(View next)
    {
        var old = _view;
        _view = next;
        next.Width = next.W; next.HorizontalAlignment = HorizontalAlignment.Center; next.VerticalAlignment = VerticalAlignment.Top;
        // Leftovers from a swap that was itself interrupted.
        foreach (var stale in _shell.Children.OfType<View>().Where(v => v != old).ToList()) _shell.Children.Remove(stale);
        _shell.Children.Add(next);
        if (old != null)
        {
            old.IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(60));
            fade.Completed += (_, _) => _shell.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, fade);
        }
        var wait = TimeSpan.FromMilliseconds(Motion ? 90 : 0);
        next.Opacity = 0;
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { BeginTime = wait });
        if (!Motion) return;
        var blur = new BlurEffect { Radius = 8, RenderingBias = RenderingBias.Performance };
        next.Effect = blur;
        var clear = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(140)) { BeginTime = wait };
        clear.Completed += (_, _) => next.Effect = null;
        blur.BeginAnimation(BlurEffect.RadiusProperty, clear);
    }

    void Resize()
    {
        if (_view is null) return;
        if (_view is StripView strip) _view.Width = strip.Wanted;
        _view.Measure(new Size(_view.Width, double.PositiveInfinity));
        var w = _view.Width; var h = Math.Min(_view.DesiredSize.Height, Height - 20);
        if (w == _shell.W.Target && h == _shell.H.Target && _view.Radius == _shell.R.Target && _view.Ear == _shell.Ear.Target) return;
        _growing = w * h > _shell.W.Target * _shell.H.Target;
        (_shell.W.Target, _shell.H.Target, _shell.R.Target, _shell.Ear.Target) = (w, h, _view.Radius, _view.Ear);
        if (!Motion || _quiet) { foreach (var s in Springs) s.Snap(s.Target); _shell.Refresh(); return; }
        CompositionTarget.Rendering -= Frame;
        _lastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering += Frame;
    }

    IEnumerable<Spring> Springs => [_shell.W, _shell.H, _shell.R, _shell.Ear];

    void Frame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min((now - _lastFrame).TotalSeconds, .05);
        if (dt <= 0) return;
        _lastFrame = now;
        foreach (var s in Springs) s.Step(dt, _growing ? .42 : .28, _growing ? .78 : 1);
        _shell.Refresh();
        if (Springs.All(s => s.Settled)) CompositionTarget.Rendering -= Frame;
    }

    // ---- watching the screen ----

    void OnTick()
    {
        var now = DateTime.UtcNow;
        _view?.Tick();
        if (now - _lastProbe > TimeSpan.FromMilliseconds(450))
        {
            _lastProbe = now;
            var (covered, full) = Native.ProbeTop(_hwnd);
            if (covered != _covered || full != _fullscreen) { _covered = covered; _fullscreen = full; Sync(); Native.KeepOnTop(_hwnd); }
        }
        if (now - _lastSweep > TimeSpan.FromSeconds(1))
        {
            _lastSweep = now;
            Hub.Sweep(Native.ProcessAlive, now);
            if (State is NotchState.Music or NotchState.Both or NotchState.Strip && !MusicVisible) Sync();
        }
    }

    /// Uses the cursor position rather than mouse events: the strip lets clicks through, so it never gets
    /// any, and a card that opens under a still mouse would not notice it leaving.
    void Poll()
    {
        if (_peek || _player) { CloseWhenLeft(); return; }
        _outside = DateTime.MaxValue;
        // Resting against the top edge opens the strip.
        if (State is not (NotchState.Rest or NotchState.Strip) || _fullscreen) { _dwell = DateTime.MaxValue; return; }
        Native.GetCursorPos(out var p);
        var m = Native.PrimaryMonitor();
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var near = p.Y <= m.Top + 1 && Math.Abs(p.X - (m.Left + m.Right) / 2.0) < _shell.W.Target * scale / 2 && (Native.GetAsyncKeyState(1) & 0x8000) == 0;
        if (!near) { _dwell = DateTime.MaxValue; return; }
        if (_dwell == DateTime.MaxValue) _dwell = DateTime.UtcNow.AddMilliseconds(250);
        else if (DateTime.UtcNow >= _dwell) { _dwell = DateTime.MaxValue; OpenPeek(); }
    }

    void CloseWhenLeft()
    {
        Native.GetCursorPos(out var p);
        var m = Native.PrimaryMonitor();
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var inside = Math.Abs(p.X - (m.Left + m.Right) / 2.0) <= (_shell.W.Target / 2 + 12) * scale && p.Y >= m.Top && p.Y <= m.Top + (_shell.H.Target + 8) * scale;
        var now = DateTime.UtcNow;
        if (inside) _pinnedUntil = default;
        if (inside || _typing || now < _pinnedUntil) { _outside = DateTime.MaxValue; return; }
        if (_outside == DateTime.MaxValue) _outside = now.AddMilliseconds(300);
        else if (now >= _outside) { _outside = DateTime.MaxValue; Dismiss(); }
    }

    // ---- pictures for the README (see Shots.cs) ----

    internal void ShowForPicture(View v)
    {
        _quiet = true; _view = v; _shell.Children.Clear();
        v.Width = v.W; v.HorizontalAlignment = HorizontalAlignment.Center; v.VerticalAlignment = VerticalAlignment.Top;
        _shell.Children.Add(v);
        Resize();
        UpdateLayout(); _shell.Refresh(); UpdateLayout();
    }
    internal FrameworkElement ShellElement => _shell;
    internal Size ShellSize => new(_shell.W.Value + 2 * _shell.Ear.Value, _shell.H.Value);
}
