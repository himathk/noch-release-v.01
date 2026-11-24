using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Noch.Core;
using Panel = Noch.Core.Panel;
using static Noch.Theme;
using Dock = System.Windows.Controls.Dock;
using Path = System.Windows.Shapes.Path;

namespace Noch;

/// A row of dots that fills up.
sealed class DotLevel : FrameworkElement
{
    readonly int _n;
    double _value;
    Color _tint = TextColor;
    public event Action<double>? Picked;
    public DotLevel(int n, double width) { _n = n; Width = width; Height = 12; VerticalAlignment = VerticalAlignment.Center; MouseLeftButtonUp += (_, e) => { if (Picked is null) return; e.Handled = true; Picked(Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1)); }; }
    public double Value { get => _value; set { if (Math.Abs(_value - value) < .0005) return; _value = value; InvalidateVisual(); } }
    public Color Tint { get => _tint; set { if (_tint == value) return; _tint = value; InvalidateVisual(); } }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var on = Theme.Brush(_tint);
        for (var i = 0; i < _n; i++)
            dc.DrawEllipse(_value > 0 && i < Math.Max(1, Math.Round(_value * _n)) ? on : Theme.Line, null, new Point(1.5 + i * (ActualWidth - 3) / (_n - 1), ActualHeight / 2), 1.5, 1.5);
    }
}

/// A message from something other than an agent.
sealed class NoticeView : View
{
    public NoticeView(IHost h, Notice n) : base(h)
    {
        if (n.Kind == NoticeKind.Volume || n.Kind == NoticeKind.Power && n.Level != null) { W = 300; Height = 36; Radius = 14; Ear = 8; Level(n); return; }
        W = 400; Radius = 22;
        UIElement left = n.Icon is BitmapSource icon
            ? new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = new ImageBrush(icon) { Stretch = Stretch.Uniform }, VerticalAlignment = VerticalAlignment.Center }
            : new DotMatrix(n.Kind switch { NoticeKind.Meeting => Dots.Meeting, NoticeKind.Warning => Dots.Need, NoticeKind.Timer => Dots.Done, NoticeKind.App => Dots.Bell, _ => Dots.Idle });
        var text = Col(1);
        text.Children.Add(Strong(n.Title));
        if (n.Detail.Length > 0) text.Children.Add(Caption(n.Detail));
        if (!string.IsNullOrEmpty(n.Body)) text.Children.Add(Wrapped(n.Body, 3));
        var right = Row(6);
        void Done() => h.S.Notices.Dismiss(n.Id);
        if (n.SideLabel != null) right.Children.Add(Small(n.SideLabel, () => { n.Side?.Invoke(); Done(); }, quiet: true));
        if (n.ActionLabel != null) right.Children.Add(Small(n.ActionLabel, () => { n.Action?.Invoke(); Done(); }));
        var bar = Bar(left, text, right.Children.Count > 0 ? right : null);
        bar.Margin = new Thickness(14, 11, 14, 11);
        Content = new Border { Background = Brushes.Transparent, Child = bar };
    }

    DotLevel? _level;
    TextBlock? _percent;

    public void Show(Notice n)
    {
        if (_level is null || _percent is null || n.Level is not { } l) return;
        _level.Value = l;
        if (n.Kind == NoticeKind.Volume) _percent.Text = $"{l * 100:0}%";
    }

    void Level(Notice n)
    {
        var dots = _level = new DotLevel(25, 150) { HorizontalAlignment = HorizontalAlignment.Left, Value = n.Level ?? 0, Tint = n.Kind == NoticeKind.Volume ? Host.Media.Current?.Tint ?? TextColor : DoneColor };
        _percent = MonoText(n.Kind == NoticeKind.Volume ? $"{(n.Level ?? 0) * 100:0}%" : n.Detail, Text2);
        var bar = Bar(new DotMatrix(n.Kind == NoticeKind.Volume ? Dots.Speaker : Dots.Bolt), n.Kind == NoticeKind.Volume ? dots : Two(n.Title, dots), _percent, 10);
        bar.Margin = new Thickness(12, 0, 12, 0); bar.VerticalAlignment = VerticalAlignment.Center;
        Content = new Border { Height = 36, Child = bar };
    }

    static UIElement Two(string label, DotLevel dots) { dots.Width = 90; return Bar(Label(label), null, dots, 8); }

    public static TextBlock Wrapped(string s, int lines)
    {
        var t = Caption(s);
        t.TextWrapping = TextWrapping.Wrap; t.MaxHeight = lines * 16; t.Margin = new Thickness(0, 4, 0, 0);
        return t;
    }

    public static Button Small(string text, Action run, bool quiet = false)
    {
        var b = quiet ? Theme.Button(Caption(text), text, run, 8, new Thickness(8, 7, 8, 7), Brushes.Transparent, Raised, Hover) : TextButton(text, run);
        if (!quiet) b.Padding = new Thickness(12, 7, 12, 7);
        return b;
    }
}

/// Twenty-five dots, going out one by one.
sealed class FocusView : View
{
    readonly DotMatrix _dots = new(Dots.Timer, 5.4);
    readonly TextBlock _time, _sub = Caption("");
    readonly GapPanel _buttons = Row(8);
    string _shape = "";

    public FocusView(IHost h) : base(h)
    {
        W = 400;
        _time = MonoText("", Text, 34); _time.LineHeight = 40;
        var text = Col(4); text.Children.Add(_time); text.Children.Add(_sub); text.Children.Add(_buttons);
        _buttons.Margin = new Thickness(0, 10, 0, 0);
        var bar = Bar(_dots, text, null, 22);
        bar.Margin = new Thickness(22, 20, 22, 20);
        Content = new Border { Background = Brushes.Transparent, Child = bar };
        Update(); Tick();
    }

    public override void Update()
    {
        var t = Host.S.Timer; var s = Host.S.Settings;
        var shape = t.Running ? "run" : t.Paused ? "pause" : "idle";
        if (shape == _shape) return;
        _shape = shape;
        _buttons.Children.Clear();
        void Add(string text, Action run, bool quiet = false) => _buttons.Children.Add(NoticeView.Small(text, run, quiet));
        if (t.Running) { Add("Pause", () => t.Pause(DateTime.UtcNow)); Add("+5 min", () => t.Add(TimeSpan.FromMinutes(5), DateTime.UtcNow)); Add("Stop", t.Stop, true); }
        else if (t.Paused) { Add("Resume", () => t.Resume(DateTime.UtcNow)); Add("Stop", t.Stop, true); }
        else
            foreach (var m in new[] { s.FocusMinutes, 15, 25, 50 }.Distinct().Take(3)) Add($"{m} min", () => t.Start(TimeSpan.FromMinutes(m), false, DateTime.UtcNow));
    }

    public override void Tick()
    {
        var t = Host.S.Timer; var now = DateTime.UtcNow;
        _dots.Level = t.Active ? t.Fraction(now) : 1;
        _dots.Tint = t.IsBreak ? DoneColor : TextColor;
        _dots.Still = !t.Running;
        _time.Text = t.Active ? Clock(Up(t.Left(now))) : Clock(TimeSpan.FromMinutes(Host.S.Settings.FocusMinutes));
        _sub.Text = t.Paused ? "Paused" : t.IsBreak ? "Break" : t.Running ? "Quiet until it ends" : "Focus for";
    }

    /// Shows 25:00 for the whole first second.
    public static TimeSpan Up(TimeSpan t) => TimeSpan.FromSeconds(Math.Ceiling(t.TotalSeconds));
}

/// Files set down for a moment.
sealed class ShelfView : View
{
    readonly WrapPanel _tiles = new() { Margin = new Thickness(6, 0, 0, 6) };
    readonly TextBlock _hint = Caption("");
    readonly Button _clear;
    string _shape = "";
    public const string DropHint = "Drop it here";
    bool _over;

    public ShelfView(IHost h) : base(h)
    {
        W = 480;
        _clear = NoticeView.Small("Clear", h.S.Shelf.Clear, true);
        var head = Bar(null, Strong("Shelf"), _clear);
        head.Margin = new Thickness(18, 14, 12, 8);
        _hint.Margin = new Thickness(18, 10, 18, 26); _hint.HorizontalAlignment = HorizontalAlignment.Center;
        var all = new StackPanel();
        all.Children.Add(head); all.Children.Add(_hint);
        all.Children.Add(new ScrollViewer { Content = _tiles, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Content = new Border { Background = Brushes.Transparent, Child = all };
        h.S.Shelf.Prune(p => File.Exists(p) || Directory.Exists(p));
        Update();
    }

    /// Something is being dragged over it.
    public bool Over { set { if (_over == value) return; _over = value; Hint(); } }

    void Hint()
    {
        var empty = Host.S.Shelf.Items.Count == 0;
        _hint.Text = _over ? DropHint : empty ? "Drag files, links or text here. Drag them out again when you need them." : "";
        _hint.Foreground = _over ? Text : Text2;
        _hint.Visibility = _hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _clear.Visibility = empty ? Visibility.Hidden : Visibility.Visible;
    }

    public override void Update()
    {
        var items = Host.S.Shelf.Items;
        var shape = string.Join("|", items.Select(i => i.Kind + i.Value));
        if (shape != _shape || items.Count == 0)
        {
            _shape = shape;
            _tiles.Children.Clear();
            foreach (var i in items) _tiles.Children.Add(new ShelfTile(Host, i));
        }
        Hint();
    }
}

sealed class ShelfTile : Border
{
    const string LinkIcon = "M8,12 L12,8 M7,9 L5.5,10.5 A2.8,2.8 0 0 0 9.5,14.5 L11,13 M9,7 L10.5,5.5 A2.8,2.8 0 0 1 14.5,9.5 L13,11", TextIcon = "M4,5 H16 M4,9 H16 M4,13 H11";
    Point? _down;

    public ShelfTile(IHost h, ShelfItem i)
    {
        Width = 108; Margin = new Thickness(4); Padding = new Thickness(8, 10, 8, 8); CornerRadius = new CornerRadius(12); Background = Brushes.Transparent; Cursor = Cursors.Hand;
        ToolTip = i.Value.Length > 300 ? i.Value[..300] + "…" : i.Value;
        System.Windows.Automation.AutomationProperties.SetName(this, i.Name);
        UIElement pic;
        if (i.Kind == ShelfKind.File)
        {
            var img = new Image { Width = 48, Height = 48, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            // Thumbnails can be slow, so the tile appears first.
            Task.Run(() => FileIcon.For(i.Value, 96)).ContinueWith(t => img.Dispatcher.BeginInvoke(() => img.Source = t.Result), TaskContinuationOptions.OnlyOnRanToCompletion);
            pic = img;
        }
        else pic = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(12), Background = Raised, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Viewbox { Width = 22, Height = 22, Child = new Path { Data = Geometry.Parse(i.Kind == ShelfKind.Link ? LinkIcon : TextIcon), Stroke = Text2, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 20, Height = 20 } }
        };
        var name = Caption(Short(i), Text); name.TextAlignment = TextAlignment.Center; name.HorizontalAlignment = HorizontalAlignment.Stretch;
        var col = Col(8); col.Children.Add(pic); col.Children.Add(name);
        var x = Theme.Button(new Path { Data = Geometry.Parse("M0,0 L7,7 M7,0 L0,7"), Stroke = Text2, StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }, "Remove " + i.Name, () => h.S.Shelf.Remove(i), 9, new Thickness(6));
        x.HorizontalAlignment = HorizontalAlignment.Right; x.VerticalAlignment = VerticalAlignment.Top; x.Visibility = Visibility.Collapsed; x.Margin = new Thickness(0, -4, -2, 0);
        var g = new Grid(); g.Children.Add(col); g.Children.Add(x);
        Child = g;
        MouseEnter += (_, _) => { Background = Raised; x.Visibility = Visibility.Visible; };
        MouseLeave += (_, _) => { Background = Brushes.Transparent; x.Visibility = Visibility.Collapsed; };
        MouseLeftButtonDown += (_, e) => { _down = e.GetPosition(null); e.Handled = true; };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_down is null) return;
            _down = null;
            if (i.Kind == ShelfKind.Text) Copy(h, i.Value); else AppServices.Open(i.Value);
        };
        MouseMove += (_, e) =>
        {
            if (_down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - d.X) + Math.Abs(p.Y - d.Y) < 8) return;
            _down = null;
            Carry(h, i);
        };
    }

    /// Shortens in the middle, so the kind of file stays readable.
    static string Short(ShelfItem i)
    {
        var n = i.Name;
        if (i.Kind != ShelfKind.File || n.Length <= 14) return n;
        var ext = System.IO.Path.GetExtension(n);
        return ext.Length is 0 or > 6 ? n : n[..Math.Max(3, 12 - ext.Length)].TrimEnd() + "…" + ext;
    }

    static void Copy(IHost h, string text)
    {
        try { Clipboard.SetText(text); h.S.Notices.Post(new Notice("copied", NoticeKind.Info, "Copied") { Life = TimeSpan.FromSeconds(1.5) }); } catch { /* another app is holding the clipboard */ }
    }

    void Carry(IHost h, ShelfItem i)
    {
        var data = new DataObject();
        if (i.Kind == ShelfKind.File) data.SetData(DataFormats.FileDrop, new[] { i.Value }); else data.SetText(i.Value);
        h.Carrying = true;
        try { DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch { /* the place it was dropped on refused it */ }
        finally { h.Carrying = false; h.S.Shelf.Prune(p => File.Exists(p) || Directory.Exists(p)); }
    }
}

/// The row of tools along the bottom of the peek.
sealed class Toolbar : DockPanel
{
    readonly TextBlock _focus = Caption("", Text), _shelf = Caption("", Text), _next = Caption("");
    readonly DotMatrix _dots = new(Dots.Timer, .8) { Still = true };
    readonly IHost _host;
    const string Gear = "M10,6.6 A3.4,3.4 0 1 0 10,13.4 A3.4,3.4 0 1 0 10,6.6 Z M10,2.4 V4.4 M10,15.6 V17.6 M2.4,10 H4.4 M15.6,10 H17.6 M4.6,4.6 L6,6 M14,14 L15.4,15.4 M15.4,4.6 L14,6 M6,14 L4.6,15.4";

    public Toolbar(IHost h)
    {
        _host = h;
        LastChildFill = true;
        Margin = new Thickness(6, 2, 4, 0);
        Button Tool(UIElement content, string name, Action run) => Theme.Button(content, name, run, 10, new Thickness(10, 7, 10, 7), Brushes.Transparent, Raised, Hover);
        var f = Row(8); f.Children.Add(_dots); f.Children.Add(_focus);
        var gear = Tool(new Viewbox { Width = 16, Height = 16, Child = new Path { Data = Geometry.Parse(Gear), Stroke = Text2, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 20, Height = 20 } }, "Settings", () => { h.Dismiss(); h.S.OpenSettings(); });
        SetDock(gear, Dock.Right);
        Children.Add(Tool(f, "Focus timer", () => h.OpenPanel(Panel.Focus)));
        Children.Add(Tool(_shelf, "Shelf", () => h.OpenPanel(Panel.Shelf)));
        Children.Add(gear);
        _next.HorizontalAlignment = HorizontalAlignment.Right; _next.Margin = new Thickness(8, 0, 6, 0);
        Children.Add(_next);
    }

    public void Update()
    {
        var t = _host.S.Timer; var now = DateTime.UtcNow;
        _focus.Text = t.Active ? Noch.Core.AgentHub.Clock(FocusView.Up(t.Left(now))) : "Focus";
        _dots.Level = t.Active ? t.Fraction(now) : 1; _dots.Tint = t.IsBreak ? DoneColor : TextColor;
        var n = _host.S.Shelf.Items.Count;
        _shelf.Text = n > 0 ? $"Shelf {n}" : "Shelf";
        _next.Text = _host.S.Calendar.Next(now) is { } m ? $"{Describe.OneLine(m.Title, 34)} {m.When(now)}" : "";
    }
}
