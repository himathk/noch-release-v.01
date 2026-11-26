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
using Panel = Noch.Core.Panel;
using Dock = Noch.Core.Dock;

namespace Noch;

sealed class NotchWindow : Window, IHost
{
    readonly Shell _shell = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) }, _poll = new() { Interval = TimeSpan.FromMilliseconds(100) }, _openSoon = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly Settings _settings;
    IntPtr _hwnd;
    View? _view;
    string _key = "";
    Panel _panel;
    bool _covered, _fullscreen, _hotkeys, _typing, _growing, _quiet, _bounce;
    DateTime _dwell = DateTime.MaxValue, _outside = DateTime.MaxValue, _armAt = DateTime.MaxValue, _lastFrame, _lastProbe, _lastSweep, _pinnedUntil;
    Size _area;
    List<ScreenInfo> _screens;
    ScreenInfo _screen;

    public AppServices S { get; }
    public AgentHub Hub => S.Hub;
    public MediaService Media => S.Media;
    public bool Carrying { get; set; }
    public Func<IReadOnlyList<SetupItem>> SetupItems { get; set; } = () => [];
    public IReadOnlyList<SetupItem> Setup => SetupItems();
    public bool Armed => DateTime.UtcNow >= _armAt;
    public bool Vertical => _shell.Dock.Vertical;
    public NotchState State { get; private set; } = NotchState.Rest;

    public NotchWindow(AppServices services)
    {
        S = services; _settings = services.Settings;
        Title = "Noch"; Width = 620; Height = 700;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = null; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; UseLayoutRounding = true;
        Resources.Add(typeof(ScrollBar), Theme.ScrollBar);
        _shell.Width = Width; _shell.Height = Height;
        _shell.Dock = Dock.Parse(_settings.Dock);
        _screens = Native.Screens(); _screen = Screens.Pick(_screens, _settings.Screen);
        Content = _shell;

        Hub.Changed += () => Dispatcher.BeginInvoke(Sync);
        Media.Changed += () => { DotMatrix.MusicPlaying = Media.Current?.Playing == true; Sync(); };
        Hub.Busy = () => _fullscreen;
        S.Sounds.Silent = () => _fullscreen;
        S.Notices.Changed += () => Dispatcher.BeginInvoke(Sync);
        S.Timer.Changed += () => Dispatcher.BeginInvoke(() => { _settings.Save(); Sync(); });
        S.Shelf.Changed += () => Dispatcher.BeginInvoke(Sync);
        S.Calendar.Changed += () => Dispatcher.BeginInvoke(Sync);
        S.Usage.Changed += () => Dispatcher.BeginInvoke(() => { if (_panel == Panel.Peek) Sync(); });
        _tick.Tick += (_, _) => OnTick();
        _poll.Tick += (_, _) => Poll();
        _openSoon.Tick += (_, _) => { _openSoon.Stop(); if (_shell.IsMouseOver && Hoverable && _press is null) OpenPeek(); };
        _shell.MouseEnter += (_, _) => { if (Hoverable) _openSoon.Start(); };
        _shell.MouseLeave += (_, _) => _openSoon.Stop();
        _shell.PreviewMouseLeftButtonDown += OnPress;
        _shell.PreviewMouseWheel += OnWheel;
        _shell.AllowDrop = true;
        _shell.DragEnter += OnDragOver; _shell.DragOver += OnDragOver;
        _shell.DragLeave += (_, _) => { if (_view is ShelfView s) s.Over = false; };
        _shell.Drop += OnDrop;
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (_drag is null) { Place(); Sync(); } });
        // Landing on a screen with other scaling resizes the window, so it is put back in its place.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => Place());
    }

    bool Hoverable => State is NotchState.Compact;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, true);
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        Place();
        Shortcut();
        _tick.Start(); _poll.Start();
        Sync();
    }

    /// Puts the window where everything the notch can grow into fits inside it, on the screen it was left on.
    void Place(List<ScreenInfo>? screens = null)
    {
        if (_hwnd == IntPtr.Zero) return;
        _screens = screens ?? Native.Screens();
        _screen = Screens.Pick(_screens, _settings.Screen);
        _area = new Size(_screen.Width, _screen.Height);
        var d = _shell.Dock;
        var x = d.Side switch { Side.Left => 0, Side.Right => _area.Width - Width, _ => Math.Clamp(d.Along * _area.Width - Width / 2, 0, Math.Max(0, _area.Width - Width)) };
        var y = d.Side switch { Side.Top => 0, Side.Bottom => _area.Height - Height, _ => Math.Clamp(d.Along * _area.Height - Height / 2, 0, Math.Max(0, _area.Height - Height)) };
        // In real pixels: a window's own Left and Top go wrong between screens with different scaling.
        var (px, py) = _screen.ToPx(Math.Round(x), Math.Round(y));
        Native.Move(_hwnd, px, py);
        _shell.Area = _area;
        var (ox, oy) = _screen.ToArea(px, py);
        _shell.Origin = new Point(ox, oy);
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
        if (msg == Native.WM_HOTKEY && w == 3)
        {
            handled = true;
            if (_drag is null) { if (_panel == Panel.None) Welcome(); else Dismiss(); }
        }
        else if (msg == Native.WM_HOTKEY && Armed && _drag is null && Hub.Pending.FirstOrDefault() is { IsQuestion: false } p && State == NotchState.Approval)
        {
            handled = true;
            if (w == 1) Hub.Allow(p); else Hub.Deny(p);
        }
        return IntPtr.Zero;
    }

    // ---- IHost ----

    public void OpenPeek() => OpenPanel(Panel.Peek);
    public void OpenPlayer() => OpenPanel(Panel.Player);
    public void OpenPanel(Panel p) { _panel = p; Sync(); }
    public void Dismiss() { _panel = Panel.None; _pinnedUntil = default; StopTyping(); Sync(); }
    public void Focus(string key) { if (Hub.Find(key) is { } s) Native.Focus(s.Pids); }
    public void Welcome() => OpenPinned(Panel.Peek);
    /// Opened from away, so it waits a while for the pointer to arrive.
    public void OpenPinned(Panel p) { _pinnedUntil = DateTime.UtcNow.AddSeconds(20); OpenPanel(p); }

    bool _shortcut;
    /// Ctrl Alt . from anywhere.
    public void Shortcut()
    {
        if (_hwnd == IntPtr.Zero || _shortcut == _settings.Shortcut) return;
        _shortcut = _settings.Shortcut;
        if (_shortcut) Native.RegisterHotKey(_hwnd, 3, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0xBE); else Native.UnregisterHotKey(_hwnd, 3);
    }

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
        if (_hwnd == IntPtr.Zero || _quiet) return;
        if (_panel == Panel.Player && Media.Current is null) _panel = Panel.None;
        var notice = S.Notices.Current;
        var scene = new Scene(_fullscreen, _covered && !_settings.AlwaysFull, _panel, MusicVisible, S.Timer.Active, S.Calendar.Soon(DateTime.UtcNow) != null, notice, S.Timer.Quiet);
        State = Presenter.Choose(Hub, scene);
        var show = scene.Bar(Hub.Sessions.Any(Presenter.Active));
        if (_drag != null) { _drag.Kind = BubbleDots; return; }
        var pending = Hub.Pending.FirstOrDefault();
        var key = State switch
        {
            NotchState.Approval or NotchState.Question => State + ":" + pending?.GetHashCode(),
            NotchState.Done or NotchState.Failed => State + ":" + Hub.Toast?.At.Ticks,
            NotchState.Compact => State + ":" + (int)show,
            // A level moving up or down is the same message.
            NotchState.Notice => State + ":" + notice?.Id + (notice?.Kind == NoticeKind.Volume ? "" : notice?.At.Ticks),
            _ => State.ToString()
        };
        if (key != _key)
        {
            View? next;
            try { next = Make(); }
            catch (Exception e)
            {
                // Something in the request could not be drawn. The terminal can still ask.
                Program.Log(e);
                if (State is NotchState.Approval or NotchState.Question && pending != null) Hub.PassOn(pending);
                else { _panel = Panel.None; if (notice != null) S.Notices.Dismiss(notice.Id); }
                return;
            }
            View? Make() => State switch
            {
                NotchState.Hidden => new RestView(this, true),
                NotchState.Rest => new RestView(this, false),
                NotchState.Strip => new StripView(this),
                NotchState.Compact => new CompactView(this, show),
                NotchState.Notice when notice != null => new NoticeView(this, notice),
                NotchState.Approval when pending != null => new ApprovalView(this, pending),
                NotchState.Question when pending != null => new QuestionView(this, pending),
                NotchState.Done or NotchState.Failed when Hub.Toast is { } t => new ToastView(this, t),
                NotchState.Peek => new PeekView(this),
                NotchState.Player => new PlayerView(this),
                NotchState.Focus => new FocusView(this),
                NotchState.Shelf => new ShelfView(this),
                _ => null
            };
            if (next is null) return; // it was answered while we were looking; the next change brings us back
            _key = key;
            _armAt = DateTime.UtcNow.AddMilliseconds(400);
            if (State != NotchState.Question) StopTyping();
            Swap(next);
        }
        else if (_view is NoticeView nv && notice != null) nv.Show(notice);
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
        // Changes deep inside only reach the top through a layout pass; measuring alone would see the old size.
        _view.UpdateLayout();
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

    DragScene? _drag;
    Native.POINT? _press;
    DateTime _dragStart, _lastDrag;

#if DEBUG
    /// Stands in for the mouse in debug demos.
    Func<(double X, double Y, bool Down)>? PointerScript;

    /// Carries the notch along a path in real pixels, then lets go, without touching the real mouse.
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

    double Scale => _screen.Scale;

    /// Where the pointer is in real pixels, and whether the button is down.
    (int X, int Y, bool Down) PointerPx()
    {
        if (PointerScript != null) { var s = PointerScript(); return ((int)Math.Round(s.X), (int)Math.Round(s.Y), s.Down); }
        Native.GetCursorPos(out var p);
        return (p.X, p.Y, (Native.GetAsyncKeyState(Native.PrimaryButton) & 0x8000) != 0);
    }

    /// Where the pointer is, in this screen's units.
    (double X, double Y, bool Down) Pointer()
    {
        var p = PointerPx();
        var (x, y) = _screen.ToArea(p.X, p.Y);
        return (x, y, p.Down);
    }

    void OnPress(object sender, MouseButtonEventArgs e)
    {
        // Leave alone anything that has its own use for dragging.
        for (var o = e.OriginalSource as DependencyObject; o != null && o != _shell; o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            if (o is TextBoxBase or ScrollBar or DotTimeline or ShelfTile) return;
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
        : S.Timer.Active ? Dots.Timer : MusicVisible ? Dots.Music : Dots.Think;

    internal void BeginDrag()
    {
        if (_drag != null) return;
        // Taking the mouse also cancels any button that was being pressed.
        if (PointerScript == null) Mouse.Capture(_shell);
        HotKeys(false);
        StopTyping();
        _panel = Panel.None; _pinnedUntil = default;
        if (!_settings.Dragged) { _settings.Dragged = true; _settings.Save(); }
        var p = Pointer();
        var bubble = new Bubble(_shell.Dock, _shell.Body(), _shell.Area, p.X, p.Y) { Motion = Motion, Open = Screens.Open(_screen, _screens) };
        _drag = new DragScene(bubble, _screen, BubbleDots, Media.Current?.Tint ?? Theme.MediaColor);
        _dragStart = _lastDrag = DateTime.UtcNow;
        // The bar itself melts into the edge it hangs from.
        foreach (var v in _shell.Children.OfType<View>())
        {
            v.IsHitTestVisible = false;
            v.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(60)));
        }
        _growing = _bounce = false;
        (_shell.W.Target, _shell.H.Target) = Vertical ? (0.0, Bubble.Melted) : (Bubble.Melted, 0.0);
        CompositionTarget.Rendering -= Frame;
        _lastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering += Frame;
        CompositionTarget.Rendering += DragFrame;
    }

    void DragFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min((now - _lastDrag).TotalSeconds, .05);
        if (dt <= 0 || _drag is null) return;
        _lastDrag = now;
        var p = PointerPx();
        if (!p.Down) _drag.Bubble.Release();
        // Carried onto another screen: the bubble goes with it.
        if (_drag.Bubble.Target is null && Screens.At(_screens, p.X, p.Y) is { } there && there != _drag.Screen) Cross(there);
        var (x, y) = _drag.Screen.ToArea(p.X, p.Y);
        _drag.Bubble.Step(dt, x, y);
        _drag.Draw((now - _dragStart).TotalSeconds);
        if (_drag.Bubble.Landed) EndDrag(_drag.Bubble.Target!.Value);
    }

    void Cross(ScreenInfo there)
    {
        var from = _drag!.Screen; var b = _drag.Bubble;
        var (x, y) = there.ToArea(from.Work.Left + b.X.Value * from.Scale, from.Work.Top + b.Y.Value * from.Scale);
        b.Cross(new Size(there.Width, there.Height), Screens.Open(there, _screens), new Point(x, y), from.Scale / there.Scale);
        _drag.MoveTo(there);
    }

    void EndDrag(Dock dock)
    {
        CompositionTarget.Rendering -= DragFrame;
        if (Mouse.Captured == _shell) Mouse.Capture(null);
#if DEBUG
        PointerScript = null;
#endif
        var done = _drag!;
        _drag = null;
        _settings.Dock = dock.ToString();
        // The main screen is kept as "the main screen", so it follows if another one is made main.
        _settings.Screen = done.Screen.Primary ? null : done.Screen.Name;
        _settings.Save();
        MoveTo(dock);
        done.Close();
    }

    /// Springs back open from bubble size at a new place.
    void MoveTo(Dock dock)
    {
        _shell.Children.Clear();
        _view = null; _key = "";
        _shell.Dock = dock;
        _shell.W.Snap(dock.Vertical ? 36 : 56); _shell.H.Snap(dock.Vertical ? 56 : 36); _shell.R.Snap(16); _shell.Ear.Snap(8);
        Place();
        _bounce = true;
        _lastProbe = default;
        Sync();
    }

    /// From the tray menu, in case it has been put somewhere awkward.
    public void BackToTop()
    {
        if (_drag != null) return;
        _settings.Dock = Dock.TopCentre.ToString();
        _settings.Screen = null;
        _settings.Save();
        MoveTo(Dock.TopCentre);
    }
    public bool Moved => _shell.Dock != Dock.TopCentre || !_screen.Primary;

    // ---- watching the screen ----

    void OnTick()
    {
        var now = DateTime.UtcNow;
        _view?.Tick();
        if (_drag is null && _view is { } v && v is not StripView && (Math.Abs(v.DesiredSize.Height - _shell.H.Target) > .5 && v.DesiredSize.Height < Height - 20)) Resize();
        // A screen plugged in or out, a taskbar moved, scaling changed.
        if (_drag is null && Native.Screens() is var screens && !screens.SequenceEqual(_screens)) { Place(screens); Sync(); }
        if (now - _lastProbe > TimeSpan.FromMilliseconds(700))
        {
            _lastProbe = now;
            var b = _shell.Body();
            // A point just inside the edge, under the middle of the notch.
            var x = _shell.Dock.Side switch { Side.Left => 12, Side.Right => _area.Width - 12, _ => b.CenterX };
            var y = _shell.Dock.Side switch { Side.Top => 12, Side.Bottom => _area.Height - 12, _ => b.CenterY };
            var (px, py) = _screen.ToPx(x, y);
            var (covered, full) = Native.Probe(_hwnd, px, py, _screen.Bounds);
            if (_drag is null && (covered != _covered || full != _fullscreen)) { _covered = covered; _fullscreen = full; Sync(); Native.KeepOnTop(_hwnd); }
        }
        if (now - _lastSweep > TimeSpan.FromSeconds(1))
        {
            _lastSweep = now;
            Hub.Sweep(Native.ProcessAlive, now);
            S.Timer.Tick(now);
            S.Notices.Tick(now, State == NotchState.Notice || _panel == Panel.Player && S.Notices.Current?.Kind == NoticeKind.Volume, State == NotchState.Notice && _shell.IsMouseOver);
            // Music that stopped a while ago, a meeting that came near: the bar may need to change.
            if (_drag is null && (State is NotchState.Compact or NotchState.Strip or NotchState.Rest) && Bar != _bar) { _bar = Bar; Sync(); }
            Measure(now);
        }
    }

    Core.Show _bar;
    Core.Show Bar => (MusicVisible ? Core.Show.Music : 0) | (S.Timer.Active ? Core.Show.Timer : 0) | (S.Calendar.Soon(DateTime.UtcNow) != null ? Core.Show.Meeting : 0);

    DateTime _measured;
    bool _measuring;
    /// Looks up how long each conversation has grown, now and then and off to the side.
    async void Measure(DateTime now)
    {
        if (_measuring || now - _measured < TimeSpan.FromSeconds(_panel == Panel.Peek ? 4 : 20)) return;
        _measured = now; _measuring = true;
        try
        {
            foreach (var s in Hub.Sessions.Where(s => s.TranscriptPath != null && now - s.LastSeen < TimeSpan.FromMinutes(30)))
            {
                var path = s.TranscriptPath;
                if (await Task.Run(() => Transcript.ContextTokens(path)) is { } n) s.ContextTokens = n;
            }
        }
        finally { _measuring = false; }
    }

    // ---- scrolling and dropping ----

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_settings.WheelVolume || !(State == NotchState.Player || State == NotchState.Compact && MusicVisible || State == NotchState.Notice && S.Notices.Current?.Kind == NoticeKind.Volume)) return;
        e.Handled = true;
        if (Volume.Change(e.Delta > 0 ? .02 : -.02) is not { } level) return;
        S.Notices.Post(new Notice("volume", NoticeKind.Volume, "Volume") { Level = level, Life = TimeSpan.FromSeconds(1.4) });
    }

    static bool CanDrop(IDataObject d) => d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent(DataFormats.UnicodeText);

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // Only where it lives is kept, so never ask to move it. A link will do where copying is not offered.
        e.Effects = Carrying || _drag != null || !CanDrop(e.Data) ? DragDropEffects.None
            : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link : e.AllowedEffects & DragDropEffects.Copy;
        if (e.Effects == DragDropEffects.None) return;
        _openSoon.Stop();
        if (_panel != Panel.Shelf) { _pinnedUntil = DateTime.UtcNow.AddSeconds(2); OpenPanel(Panel.Shelf); }
        if (_view is ShelfView s) { s.Over = true; Resize(); }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Carrying) return;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) S.Shelf.Add(Enumerable.Reverse(files).Select(f => new ShelfItem(ShelfKind.File, f)));
            else if (Shelf.FromText(e.Data.GetData(DataFormats.UnicodeText) as string) is { } item) S.Shelf.Add([item]);
        }
        catch (Exception ex) { Program.Log(ex); }
        if (_view is ShelfView s) s.Over = false;
        Sync();
    }

    /// Uses the cursor position rather than mouse events: the strip lets clicks through, so it never gets
    /// any, and a card that opens under a still mouse would not notice it leaving.
    void Poll()
    {
        if (_drag != null) return;
        if (_panel != Panel.None) { CloseWhenLeft(); return; }
        _outside = DateTime.MaxValue;
        // Resting against the edge opens the strip.
        if (State is not (NotchState.Rest or NotchState.Strip) || _fullscreen || Carrying) { _dwell = DateTime.MaxValue; return; }
        var p = Pointer();
        var b = _shell.Dock.Place(_shell.W.Target, _shell.H.Target, _area.Width, _area.Height);
        // Against the taskbar there is no screen edge to push into, so the whole strip counts.
        // So does an edge the pointer carries on past, onto another screen.
        var limit = Screens.Hard(_screen, _shell.Dock.Side, _screens) ? 1.5 : 10;
        // Holding something over the edge opens the shelf for it, unless it is a window being moved or resized.
        var carrying = p.Down && !Native.MovingWindow();
        var near = (!p.Down || carrying) && b.Contains(p.X, p.Y, 2) && Dock.Depth(_shell.Dock.Side, p.X, p.Y, _area.Width, _area.Height) <= limit;
        if (!near) { _dwell = DateTime.MaxValue; return; }
        if (_dwell == DateTime.MaxValue) _dwell = DateTime.UtcNow.AddMilliseconds(carrying ? 450 : 250);
        else if (DateTime.UtcNow >= _dwell) { _dwell = DateTime.MaxValue; _pinnedUntil = carrying ? DateTime.UtcNow.AddSeconds(1.5) : default; OpenPanel(carrying ? Panel.Shelf : Panel.Peek); }
    }

    void CloseWhenLeft()
    {
        var p = Pointer();
        var inside = _shell.Dock.Place(_shell.W.Target, _shell.H.Target, _area.Width, _area.Height).Contains(p.X, p.Y, 12);
        var now = DateTime.UtcNow;
        if (inside) _pinnedUntil = default;
        if (inside || _typing || Carrying || _press != null || now < _pinnedUntil) { _outside = DateTime.MaxValue; return; }
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
