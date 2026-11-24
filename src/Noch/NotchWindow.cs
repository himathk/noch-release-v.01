using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Noch.Core;
using Dock = Noch.Core.Dock;

namespace Noch;

sealed class NotchWindow : Window, IHost
{
    readonly Shell _shell = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) }, _poll = new() { Interval = TimeSpan.FromMilliseconds(80) }, _openSoon = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly Settings _settings;
    IntPtr _hwnd;
    View? _view;
    string _key = "";
    bool _peek, _player, _covered, _fullscreen, _hotkeys, _typing, _growing, _quiet, _bounce;
    DateTime _dwell = DateTime.MaxValue, _outside = DateTime.MaxValue, _armAt = DateTime.MaxValue, _lastFrame, _lastProbe, _lastSweep, _pinnedUntil;
    Rect _area;

    public AgentHub Hub { get; }
    public MediaService Media { get; }
    public Func<IReadOnlyList<SetupItem>> SetupItems { get; set; } = () => [];
    public IReadOnlyList<SetupItem> Setup => SetupItems();
    public bool Armed => DateTime.UtcNow >= _armAt;
    public bool Vertical => _shell.Dock.Vertical;
    public NotchState State { get; private set; } = NotchState.Rest;

    public NotchWindow(AgentHub hub, MediaService media, Settings settings)
    {
        Hub = hub; Media = media; _settings = settings;
        Title = "Noch"; Width = 620; Height = 540;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = null; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; UseLayoutRounding = true;
        _shell.Width = Width; _shell.Height = Height;
        _shell.Dock = Dock.Parse(settings.Dock);
        Content = _shell;

        Hub.Changed += () => Dispatcher.BeginInvoke(Sync);
        Media.Changed += () => { DotMatrix.MusicPlaying = Media.Current?.Playing == true; Sync(); };
        Hub.Busy = () => _fullscreen;
        _tick.Tick += (_, _) => OnTick();
        _poll.Tick += (_, _) => Poll();
        _openSoon.Tick += (_, _) => { _openSoon.Stop(); if (_shell.IsMouseOver && Hoverable && _press is null) OpenPeek(); };
        _shell.MouseEnter += (_, _) => { if (Hoverable) _openSoon.Start(); };
        _shell.MouseLeave += (_, _) => _openSoon.Stop();
        _shell.PreviewMouseLeftButtonDown += OnPress;
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
    }

    bool Hoverable => State is NotchState.Agent or NotchState.Both or NotchState.Music;

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

    /// Puts the window where everything the notch can grow into fits inside it.
    void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        _area = SystemParameters.WorkArea;
        var d = _shell.Dock;
        var x = d.Side switch { Side.Left => 0, Side.Right => _area.Width - Width, _ => Math.Clamp(d.Along * _area.Width - Width / 2, 0, Math.Max(0, _area.Width - Width)) };
        var y = d.Side switch { Side.Top => 0, Side.Bottom => _area.Height - Height, _ => Math.Clamp(d.Along * _area.Height - Height / 2, 0, Math.Max(0, _area.Height - Height)) };
        x = Math.Round(x); y = Math.Round(y);
        Left = _area.Left + x; Top = _area.Top + y;
        _shell.Area = new Size(_area.Width, _area.Height);
        _shell.Origin = new Point(x, y);
        Retarget();
        _shell.At.Snap(_shell.At.Target);
        Native.KeepOnTop(_hwnd);
        _shell.Refresh();
    }

    /// Where the centre should be for the size it is growing to.
    void Retarget()
    {
        var b = _shell.Dock.Place(_shell.W.Target, _shell.H.Target, _area.Width, _area.Height);
        _shell.At.Target = Vertical ? b.CenterY : b.CenterX;
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE && !_typing) { handled = true; return Native.MA_NOACTIVATE; }
        if (msg == Native.WM_HOTKEY && Armed && _drag is null && Hub.Pending.FirstOrDefault() is { IsQuestion: false } p && State == NotchState.Approval)
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

    bool MusicVisible => Media.Current is { } m && (m.Playing || DateTime.UtcNow - Media.LastPlaying < TimeSpan.FromSeconds(10));

    public void Sync()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_player && Media.Current is null) _player = false;
        State = Presenter.Choose(Hub, new Scene(_fullscreen, _covered && !_settings.AlwaysFull, MusicVisible, _peek, _player));
        if (_drag != null) { _drag.Kind = BubbleDots; return; }
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
        next.Width = next.W;
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
        if (_view is StripView strip) { if (Vertical) _view.Height = strip.Wanted; else _view.Width = strip.Wanted; }
        _view.Measure(new Size(_view.Width, double.PositiveInfinity));
        var w = _view.Width; var h = Math.Min(_view.DesiredSize.Height, Height - 20);
        if (w == _shell.W.Target && h == _shell.H.Target && _view.Radius == _shell.R.Target && _view.Ear == _shell.Ear.Target && !_bounce) { _shell.Refresh(); return; }
        _growing = w * h > _shell.W.Target * _shell.H.Target;
        (_shell.W.Target, _shell.H.Target, _shell.R.Target, _shell.Ear.Target) = (w, h, _view.Radius, _view.Ear);
        Retarget();
        if (!Motion || _quiet) { foreach (var s in Springs) s.Snap(s.Target); _bounce = false; _shell.Refresh(); return; }
        CompositionTarget.Rendering -= Frame;
        _lastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering += Frame;
    }

    IEnumerable<Spring> Springs => [_shell.W, _shell.H, _shell.R, _shell.Ear, _shell.At];

    void Frame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min((now - _lastFrame).TotalSeconds, .05);
        if (dt <= 0) return;
        _lastFrame = now;
        foreach (var s in Springs) s.Step(dt, _growing ? .42 : .28, _bounce ? .52 : _growing ? .78 : 1);
        _shell.Refresh();
        if (Springs.All(s => s.Settled)) { _bounce = false; CompositionTarget.Rendering -= Frame; }
    }

    // ---- dragging ----

    DragWindow? _drag;
    Native.POINT? _press;
    DateTime _dragStart, _lastDrag;
    int _dragFrames;

#if DEBUG
    /// Stands in for the mouse in debug demos.
    Func<(double X, double Y, bool Down)>? PointerScript;

    /// Carries the notch along a path, then lets go, without touching the real mouse.
    internal void DemoDrag(params (double T, double X, double Y)[] path)
    {
        var start = DateTime.UtcNow;
        PointerScript = () =>
        {
            var t = (DateTime.UtcNow - start).TotalSeconds;
            for (var i = 1; i < path.Length; i++)
                if (t < path[i].T)
                {
                    var k = (t - path[i - 1].T) / (path[i].T - path[i - 1].T);
                    return (path[i - 1].X + (path[i].X - path[i - 1].X) * k, path[i - 1].Y + (path[i].Y - path[i - 1].Y) * k, true);
                }
            return (path[^1].X, path[^1].Y, false);
        };
        BeginDrag();
    }
#else
    const Func<(double X, double Y, bool Down)>? PointerScript = null;
#endif

    double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// Where the pointer is, in screen units, and whether the button is down.
    (double X, double Y, bool Down) Pointer()
    {
        if (PointerScript != null) return PointerScript();
        Native.GetCursorPos(out var p);
        return (p.X / Scale - _area.Left, p.Y / Scale - _area.Top, (Native.GetAsyncKeyState(Native.PrimaryButton) & 0x8000) != 0);
    }

    void OnPress(object sender, MouseButtonEventArgs e)
    {
        // Leave alone anything that has its own use for dragging.
        for (var o = e.OriginalSource as DependencyObject; o != null && o != _shell; o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is TextBoxBase or ScrollBar or DotTimeline) return;
        if (_drag != null) return;
        Native.GetCursorPos(out var p);
        _press = p;
        _openSoon.Stop();
        CompositionTarget.Rendering -= WatchPress;
        CompositionTarget.Rendering += WatchPress;
    }

    /// Watches the pointer itself, because a quick flick leaves the window before any mouse event arrives.
    void WatchPress(object? sender, EventArgs e)
    {
        if (_press is not { } from) { CompositionTarget.Rendering -= WatchPress; return; }
        var down = (Native.GetAsyncKeyState(Native.PrimaryButton) & 0x8000) != 0;
        Native.GetCursorPos(out var p);
        var moved = Math.Sqrt(Math.Pow(p.X - from.X, 2) + Math.Pow(p.Y - from.Y, 2)) > 7 * Scale;
        if (down && !moved) return;
        CompositionTarget.Rendering -= WatchPress;
        _press = null;
        if (down && moved) BeginDrag();
    }

    Dots BubbleDots => Hub.Pending.Count > 0 ? Hub.Pending[0].IsQuestion ? Dots.Ask : Dots.Need
        : Presenter.Ordered(Hub.Sessions.Where(Presenter.Active)).FirstOrDefault() is { } s ? DotMatrix.For(s.Activity)
        : MusicVisible ? Dots.Music : Dots.Think;

    internal void BeginDrag()
    {
        if (_drag != null) return;
        // Taking the mouse also cancels any button that was being pressed.
        if (PointerScript == null) Mouse.Capture(_shell);
        HotKeys(false);
        StopTyping();
        _peek = _player = false; _pinnedUntil = default;
        var p = Pointer();
        var bubble = new Bubble(_shell.Dock, _shell.Body(), _shell.Area, p.X, p.Y) { Motion = Motion };
        _drag = new DragWindow(bubble, _area, Scale, BubbleDots, Media.Current?.Tint ?? Theme.MediaColor);
        _drag.Show();
        _dragFrames = 0; _dragStart = _lastDrag = DateTime.UtcNow;
        CompositionTarget.Rendering -= Frame;
        CompositionTarget.Rendering += DragFrame;
    }

    void DragFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min((now - _lastDrag).TotalSeconds, .05);
        if (dt <= 0 || _drag is null) return;
        _lastDrag = now;
        // The bubble is drawn over the bar before the bar is hidden, so nothing flickers.
        if (++_dragFrames == 3) _shell.Opacity = 0;
        var p = Pointer();
        if (!p.Down) _drag.Bubble.Release();
        _drag.Bubble.Step(dt, p.X, p.Y);
        _drag.Follow((now - _dragStart).TotalSeconds);
        if (_drag.Bubble.Landed) EndDrag(_drag.Bubble.Target!.Value);
    }

    void EndDrag(Dock dock)
    {
        CompositionTarget.Rendering -= DragFrame;
        if (Mouse.Captured == _shell) Mouse.Capture(null);
#if DEBUG
        PointerScript = null;
#endif
        var ghost = _drag!;
        _drag = null;
        _settings.Dock = dock.ToString();
        _settings.Save();
        MoveTo(dock);
        // Hide the bubble only once the bar underneath has been drawn.
        var left = 4;
        void Away(object? s, EventArgs e) { if (--left > 0) return; CompositionTarget.Rendering -= Away; ghost.Close(); }
        CompositionTarget.Rendering += Away;
    }

    /// Springs back open from bubble size at a new place.
    void MoveTo(Dock dock)
    {
        _shell.Children.Clear();
        _view = null; _key = "";
        _shell.Dock = dock;
        _shell.W.Snap(dock.Vertical ? 36 : 56); _shell.H.Snap(dock.Vertical ? 56 : 36); _shell.R.Snap(16); _shell.Ear.Snap(8);
        Place();
        _shell.Opacity = 1;
        _bounce = true;
        _lastProbe = default;
        Sync();
    }

    /// From the tray menu, in case it has been put somewhere awkward.
    public void BackToTop()
    {
        if (_drag != null) return;
        _settings.Dock = Dock.TopCentre.ToString();
        _settings.Save();
        MoveTo(Dock.TopCentre);
    }
    public bool Moved => _shell.Dock != Dock.TopCentre;

    // ---- watching the screen ----

    void OnTick()
    {
        var now = DateTime.UtcNow;
        _view?.Tick();
        if (_drag is null && SystemParameters.WorkArea != _area) { Place(); Sync(); }
        if (now - _lastProbe > TimeSpan.FromMilliseconds(450))
        {
            _lastProbe = now;
            var b = _shell.Body();
            // A point just inside the edge, under the middle of the notch.
            var x = _shell.Dock.Side switch { Side.Left => 12, Side.Right => _area.Width - 12, _ => b.CenterX };
            var y = _shell.Dock.Side switch { Side.Top => 12, Side.Bottom => _area.Height - 12, _ => b.CenterY };
            var (covered, full) = Native.Probe(_hwnd, (int)((_area.Left + x) * Scale), (int)((_area.Top + y) * Scale));
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
        if (_drag != null) return;
        if (_peek || _player) { CloseWhenLeft(); return; }
        _outside = DateTime.MaxValue;
        // Resting against the edge opens the strip.
        if (State is not (NotchState.Rest or NotchState.Strip) || _fullscreen) { _dwell = DateTime.MaxValue; return; }
        var p = Pointer();
        var b = _shell.Dock.Place(_shell.W.Target, _shell.H.Target, _area.Width, _area.Height);
        // Against the taskbar there is no screen edge to push into, so the whole strip counts.
        var limit = _shell.Dock.Side switch
        {
            Side.Top => _area.Top <= 0, Side.Left => _area.Left <= 0, Side.Right => _area.Right >= SystemParameters.PrimaryScreenWidth, _ => _area.Bottom >= SystemParameters.PrimaryScreenHeight
        } ? 1.5 : 10;
        var near = !p.Down && b.Contains(p.X, p.Y, 2) && Dock.Depth(_shell.Dock.Side, p.X, p.Y, _area.Width, _area.Height) <= limit;
        if (!near) { _dwell = DateTime.MaxValue; return; }
        if (_dwell == DateTime.MaxValue) _dwell = DateTime.UtcNow.AddMilliseconds(250);
        else if (DateTime.UtcNow >= _dwell) { _dwell = DateTime.MaxValue; OpenPeek(); }
    }

    void CloseWhenLeft()
    {
        var p = Pointer();
        var inside = _shell.Dock.Place(_shell.W.Target, _shell.H.Target, _area.Width, _area.Height).Contains(p.X, p.Y, 12);
        var now = DateTime.UtcNow;
        if (inside) _pinnedUntil = default;
        if (inside || _typing || _press != null || now < _pinnedUntil) { _outside = DateTime.MaxValue; return; }
        if (_outside == DateTime.MaxValue) _outside = now.AddMilliseconds(300);
        else if (now >= _outside) { _outside = DateTime.MaxValue; Dismiss(); }
    }

    // ---- pictures for the README (see Shots.cs) ----

    internal void ShowForPicture(View v)
    {
        _quiet = true; _view = v; _shell.Children.Clear();
        v.Width = v.W;
        _shell.Children.Add(v);
        Resize(); Place();
        UpdateLayout(); _shell.Refresh(); UpdateLayout();
    }
    internal void SetDockForPicture(Dock d) { _shell.Dock = d; Place(); }
    internal FrameworkElement ShellElement => _shell;
    internal Box ShellBody => _shell.LocalBody();
    internal double ShellEar => _shell.Ear.Value;
    internal Dock ShellDock => _shell.Dock;
}
