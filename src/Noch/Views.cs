using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Noch.Core;
using static Noch.Theme;

namespace Noch;

/// What a view needs from the rest of the app.
interface IHost
{
    AgentHub Hub { get; }
    MediaService Media { get; }
    IReadOnlyList<SetupItem> Setup { get; }
    /// False for a moment after a card drops, so a click meant for something else cannot approve anything.
    bool Armed { get; }
    void OpenPeek();
    void OpenPlayer();
    void Dismiss();
    void Focus(string sessionKey);
    void TakeKeyboard(TextBox box);
}

sealed record SetupItem(string Name, string Detail, string Action, Action Run);

/// Base for everything that can appear inside the black shape.
abstract class View : ContentControl
{
    protected readonly IHost Host;
    public double W { get; protected init; }
    public double Radius { get; protected init; } = 24;
    public double Ear { get; protected init; } = 10;
    public virtual bool ClickThrough => false;
    protected View(IHost host)
    {
        Host = host; Focusable = false; HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }
    public virtual void Update() { }
    /// Called a few times a second while on screen, for clocks and progress.
    public virtual void Tick() { }
    protected static string Elapsed(AgentSession s) => AgentHub.Clock(DateTime.UtcNow - s.TurnStarted);
    protected static string Clock(TimeSpan t) => AgentHub.Clock(t);
}

sealed class RestView : View
{
    public RestView(IHost h, bool hidden) : base(h) { W = 120; Height = hidden ? 0 : 6; Radius = 6; Ear = hidden ? 0 : 4; }
    public override bool ClickThrough => true;
}

sealed class StripView : View
{
    readonly GapPanel _bars = Row(5);
    public StripView(IHost h) : base(h) { W = 60; Height = 9; Radius = 8; Ear = 5; Content = _bars; _bars.HorizontalAlignment = HorizontalAlignment.Center; Update(); }
    public override bool ClickThrough => true;
    public double Wanted { get; private set; }

    public override void Update()
    {
        _bars.Children.Clear();
        void Add(Brush b) => _bars.Children.Add(new Rectangle { Width = 28, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = b });
        foreach (var s in Presenter.Ordered(Host.Hub.Sessions.Where(Presenter.Active)).Take(6)) Add(Presenter.NeedsYou(s) ? Need : Text);
        if (Host.Media.Current is { Playing: true } m) Add(Theme.Brush(m.Tint));
        Wanted = Math.Max(1, _bars.Children.Count) * 33 + 41;
    }
}

/// One line: what the most important agent is doing, and what is playing.
sealed class CompactView : View
{
    readonly bool _agent, _music;
    readonly DotMatrix _dots = new(Dots.Think), _eq = new(Dots.Music);
    readonly TextBlock _label = Label(""), _more = MonoText(""), _time = MonoText(""), _title = Label(""), _artist = Label("", Text3);
    readonly Border _cover = Cover(null, 22, 22, 6);

    public CompactView(IHost h, bool agent, bool music) : base(h)
    {
        (_agent, _music) = (agent, music);
        W = agent && music ? 364 : 300; Height = 36; Radius = 14; Ear = 8;
        UIElement? left, right; UIElement mid;
        if (agent)
        {
            var text = new DockPanel { LastChildFill = false };
            _label.MaxWidth = music ? 206 : 190;
            _more.Margin = new Thickness(8, 0, 0, 0);
            text.Children.Add(_label); text.Children.Add(_more);
            mid = text; left = _dots;
            if (music) { var r = Row(10); r.Children.Add(_eq); r.Children.Add(Tap(_cover, h.OpenPlayer)); right = r; } else right = _time;
        }
        else
        {
            var text = new DockPanel { LastChildFill = true };
            _title.MaxWidth = 140; _artist.Margin = new Thickness(10, 0, 0, 0);
            text.Children.Add(_title); text.Children.Add(_artist);
            left = _cover; mid = text; right = _eq;
        }
        var bar = Bar(left, mid, right, 10);
        bar.Margin = new Thickness(12, 0, 12, 0); bar.VerticalAlignment = VerticalAlignment.Center;
        Content = new Border { Background = Brushes.Transparent, Height = 36, Child = bar };
        MouseLeftButtonUp += (_, e) => { if (!e.Handled) { if (_agent) h.OpenPeek(); else h.OpenPlayer(); } };
        Update(); Tick();
    }

    static UIElement Tap(UIElement e, Action a)
    {
        e.MouseLeftButtonUp += (_, x) => { x.Handled = true; a(); };
        ((FrameworkElement)e).Cursor = Cursors.Hand;
        return e;
    }

    AgentSession? Top => Presenter.Ordered(Host.Hub.Sessions.Where(Presenter.Active)).FirstOrDefault();

    public override void Update()
    {
        if (_agent && Top is { } s)
        {
            _dots.Kind = DotMatrix.For(s.Activity);
            _label.Text = s.Label;
            var n = Host.Hub.Sessions.Count(Presenter.Active) - 1 + s.Subagents.Count;
            _more.Text = n > 0 ? "+" + n : "";
        }
        if (_music && Host.Media.Current is { } m)
        {
            _eq.Tint = m.Tint; _title.Text = m.Title; _artist.Text = m.Artist;
            _cover.Background = m.Art is null ? Raised : new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill };
        }
    }
    public override void Tick() { if (Top is { } s) _time.Text = Elapsed(s); }
}

sealed class ApprovalView : View
{
    readonly TextBlock _queue = MonoText("");
    readonly PendingRequest _p;

    public ApprovalView(IHost h, PendingRequest p) : base(h)
    {
        _p = p; W = 480;
        var s = h.Hub.Find(p.SessionKey);
        var text = Col(1);
        text.Children.Add(Strong($"{s?.Agent ?? "Claude Code"} {p.Title}"));
        text.Children.Add(Caption(Desc(p, s)));
        var header = Bar(new DotMatrix(Dots.Need), text, _queue);
        // Clicking the header hands the decision back to the terminal.
        var open = Theme.Button(header, "Answer in the terminal instead", () => { h.Focus(p.SessionKey); h.Hub.PassOn(p); }, 12, new Thickness(4, 4, 4, 4), Brushes.Transparent, stretch: true);
        open.ToolTip = "Answer in the terminal instead";

        var code = MonoText("", Text);
        code.TextTrimming = TextTrimming.None; code.TextWrapping = TextWrapping.Wrap; code.VerticalAlignment = VerticalAlignment.Top;
        Fill(code, p);
        var body = Col(2);
        body.Children.Add(new ScrollViewer { Content = code, MaxHeight = 144, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        var cwd = MonoText(s?.Cwd ?? "", Text3, 11); cwd.ToolTip = s?.Cwd;
        if (p.Tool is not ("Edit" or "Write" or "Read")) body.Children.Add(cwd);

        var deny = TextButton("Deny", () => Do(() => h.Hub.Deny(p)), "Ctrl Alt N");
        var allow = TextButton("Allow", () => Do(() => h.Hub.Allow(p)), "Ctrl Alt Y", primary: true);
        var row = new Grid();
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(deny);
        if (p.Suggestions != null)
        {
            var always = TextButton("Always", () => Do(() => h.Hub.AllowAlways(p)));
            always.Margin = new Thickness(8, 0, 0, 0); always.ToolTip = "Always allow: " + Rules(p);
            Grid.SetColumn(always, 1); row.Children.Add(always);
        }
        Grid.SetColumn(allow, 3); row.Children.Add(allow);

        var all = Col(12);
        all.Children.Add(open);
        all.Children.Add(new Border { Background = Raised, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Child = body });
        all.Children.Add(row);
        all.Margin = new Thickness(14, 12, 14, 14);
        Content = all;
        Update();
    }

    void Do(Action a) { if (Host.Armed) a(); }

    static string Desc(PendingRequest p, AgentSession? s) =>
        p.Event.Input["description"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var d) && d.Length > 0 ? $"{d} in {s?.Project}" : "in " + s?.Project;

    static string Rules(PendingRequest p)
    {
        var list = p.Suggestions!.OfType<System.Text.Json.Nodes.JsonObject>().Select(o => (string?)o["type"] switch
        {
            "addRules" => string.Join(", ", (o["rules"] as System.Text.Json.Nodes.JsonArray ?? []).Select(r => $"{(string?)r?["toolName"]}({(string?)r?["ruleContent"] ?? "anything"})")),
            "addDirectories" => "folder access",
            "setMode" => "mode " + (string?)o["mode"],
            var t => t ?? ""
        });
        return string.Join("; ", list);
    }

    static void Fill(TextBlock t, PendingRequest p)
    {
        var lines = p.Command.Split('\n');
        const int max = 400;
        var diff = p.Tool is "Edit" or "Write";
        for (var i = 0; i < Math.Min(lines.Length, max); i++)
        {
            var run = new Run(lines[i] + (i < lines.Length - 1 ? "\n" : ""));
            if (diff && lines[i].StartsWith("+ ", StringComparison.Ordinal)) run.Foreground = Done;
            if (diff && lines[i].StartsWith("- ", StringComparison.Ordinal)) run.Foreground = Fail;
            t.Inlines.Add(run);
        }
        if (lines.Length > max) t.Inlines.Add(new Run($"{lines.Length - max} more lines. Click the title to see them in the terminal.") { Foreground = Need });
    }

    public override void Update()
    {
        var n = Host.Hub.Pending.Count;
        _queue.Text = n > 1 ? $"1 of {n}" : "";
    }
}

sealed class QuestionView : View
{
    readonly PendingRequest _p;
    readonly IReadOnlyList<Question> _qs;
    readonly Dictionary<string, string> _answers = [];
    readonly HashSet<string> _picked = [];
    int _at;

    public QuestionView(IHost h, PendingRequest p) : base(h) { _p = p; _qs = p.Questions; W = 480; Show(); }

    void Show()
    {
        if (_at >= _qs.Count) { Host.Hub.AnswerQuestions(_p, _answers); return; }
        var q = _qs[_at];
        _picked.Clear();
        var s = Host.Hub.Find(_p.SessionKey);
        var text = Col(1);
        var title = Strong(q.Text); title.TextWrapping = TextWrapping.Wrap; title.TextTrimming = TextTrimming.None;
        text.Children.Add(title);
        text.Children.Add(Caption($"{s?.Agent ?? "Claude Code"} in {s?.Project}"));
        var all = Col(12);
        var head = Bar(new DotMatrix(Dots.Ask), text, MonoText(_qs.Count > 1 ? $"{_at + 1} of {_qs.Count}" : ""));
        head.Margin = new Thickness(4, 0, 4, 0);
        all.Children.Add(head);

        var list = Col(6);
        for (var i = 0; i < q.Options.Count; i++)
        {
            var (label, detail) = q.Options[i];
            var num = MonoText((i + 1).ToString());
            var name = Label(label);
            var line = Col(1);
            line.Children.Add(name);
            if (detail.Length > 0) { var d = Caption(detail); d.TextWrapping = TextWrapping.Wrap; d.TextTrimming = TextTrimming.None; line.Children.Add(d); }
            Button b = null!;
            b = Theme.Button(Bar(num, line, null, 10), label, () => { if (Host.Armed) Pick(q, label, num); }, stretch: true);
            list.Children.Add(b);
        }
        all.Children.Add(new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        var box = new TextBox
        {
            Background = Raised, Foreground = Text, CaretBrush = Text, BorderThickness = new Thickness(0), FontFamily = Sans, FontSize = 13, Padding = new Thickness(10, 8, 10, 8), Visibility = Visibility.Collapsed
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Your answer");
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && box.Text.Trim().Length > 0) Next(q, box.Text.Trim()); };
        all.Children.Add(box);

        var foot = new DockPanel { LastChildFill = false };
        var own = Link("Type your own answer", () => { box.Visibility = Visibility.Visible; Host.TakeKeyboard(box); });
        var term = Link("Answer in the terminal", () => { Host.Focus(_p.SessionKey); Host.Hub.PassOn(_p); });
        foot.Children.Add(own);
        if (q.Multi) { var ok = TextButton("Send", () => { if (Host.Armed && _picked.Count > 0) Next(q, string.Join(",", q.Options.Where(o => _picked.Contains(o.Label)).Select(o => o.Label))); }, primary: true); DockPanel.SetDock(ok, Dock.Right); foot.Children.Add(ok); }
        DockPanel.SetDock(term, Dock.Right); foot.Children.Add(term);
        all.Children.Add(foot);
        all.Margin = new Thickness(14, 16, 14, 10);
        Content = all;
    }

    static Button Link(string t, Action a) => Theme.Button(Caption(t), t, a, 8, new Thickness(4, 4, 4, 4), Brushes.Transparent);

    void Pick(Question q, string label, TextBlock mark)
    {
        if (!q.Multi) { Next(q, label); return; }
        if (!_picked.Remove(label)) _picked.Add(label);
        mark.Foreground = _picked.Contains(label) ? Need : Text3;
        mark.FontWeight = _picked.Contains(label) ? FontWeights.Bold : FontWeights.Normal;
    }

    void Next(Question q, string answer) { _answers[q.Text] = answer; _at++; Show(); }
}

sealed class ToastView : View
{
    public ToastView(IHost h, Toast t) : base(h)
    {
        W = 400; Radius = 22;
        var text = Col(1);
        text.Children.Add(Strong(t.Title)); text.Children.Add(Caption(t.Detail));
        var open = TextButton("Open", () => { h.Focus(t.SessionKey); h.Hub.DismissToast(); });
        open.Padding = new Thickness(12, 7, 12, 7);
        var bar = Bar(new DotMatrix(t.Failed ? Dots.Fail : Dots.Done), text, open);
        bar.Margin = new Thickness(14, 11, 14, 11);
        Content = new Border { Background = Brushes.Transparent, Child = bar };
        // A failure stays until it has been looked at.
        MouseLeave += (_, _) => h.Hub.DismissToast();
    }
}

/// Every agent, plus what is playing.
sealed class PeekView : View
{
    string _shape = "";
    readonly List<Action> _clocks = [];
    readonly List<Action> _updates = [];

    public PeekView(IHost h) : base(h) { W = 480; Update(); }

    public override void Update()
    {
        var sessions = Presenter.Ordered(Host.Hub.Sessions).Take(5).ToList();
        var m = Host.Media.Current;
        var setup = Host.Setup;
        // Rebuild only when rows come or go; otherwise change the text in place so nothing flickers.
        var shape = string.Join("|", sessions.Select(s => s.Key)) + $"#{m != null}#{string.Join(",", setup.Select(x => x.Name + x.Action))}";
        if (shape != _shape) { _shape = shape; Build(sessions, m, setup); }
        foreach (var u in _updates) u();
        Tick();
    }

    public override void Tick() { foreach (var c in _clocks) c(); }

    void Build(List<AgentSession> sessions, NowPlaying? media, IReadOnlyList<SetupItem> setup)
    {
        _clocks.Clear(); _updates.Clear();
        var all = Col(2);
        foreach (var s in sessions) all.Children.Add(AgentRow(s));
        foreach (var i in setup) all.Children.Add(SetupRow(i));
        if (sessions.Count == 0 && setup.Count == 0)
        {
            var t = Caption("No agents running"); t.Margin = new Thickness(12, 10, 12, 10);
            all.Children.Add(Bar(new Border { Child = new DotMatrix(Dots.Idle), Margin = new Thickness(10, 10, 0, 10) }, t, null, 2));
        }
        if (media != null)
        {
            all.Children.Add(new Rectangle { Height = 1, Fill = Theme.Line, Margin = new Thickness(10, 8, 10, 8) });
            all.Children.Add(MediaRow());
        }
        all.Margin = new Thickness(10, 12, 10, 10);
        Content = all;
    }

    UIElement AgentRow(AgentSession s)
    {
        var dots = new DotMatrix(DotMatrix.For(s.Activity));
        var act = Caption("");
        var time = MonoText("");
        var title = Row(6);
        title.Children.Add(Strong(s.Project)); title.Children.Add(Label(s.Agent, Text3));
        var text = Col(1); text.Children.Add(title); text.Children.Add(act);
        _updates.Add(() =>
        {
            dots.Kind = DotMatrix.For(s.Activity);
            var sub = s.Subagents.Count;
            act.Text = (s.Label.Length > 0 ? s.Label : "Waiting for your next message") + (sub > 0 ? sub == 1 ? ", 1 subagent" : $", {sub} subagents" : "");
            act.Foreground = Presenter.NeedsYou(s) ? Need : s.Activity == Activity.Failed ? Fail : Text2;
        });
        _clocks.Add(() => time.Text = Presenter.Active(s) ? Elapsed(s) : "");
        return Theme.Button(Bar(dots, text, time), $"{s.Project}, {s.Agent}", () =>
        {
            // If it is waiting on an answer, show the card; otherwise go to its window.
            if (Host.Hub.Pending.Any(p => p.SessionKey == s.Key)) Host.Dismiss(); else { Host.Focus(s.Key); Host.Dismiss(); }
        }, 14, new Thickness(10, 9, 10, 9), Presenter.NeedsYou(s) ? Raised : Brushes.Transparent, Raised, Hover, stretch: true);
    }

    static UIElement SetupRow(SetupItem i)
    {
        var text = Col(1); text.Children.Add(Strong(i.Name)); text.Children.Add(Caption(i.Detail));
        var b = TextButton(i.Action, i.Run); b.Padding = new Thickness(12, 7, 12, 7);
        var bar = Bar(new DotMatrix(Dots.Idle), text, b); bar.Margin = new Thickness(10, 9, 10, 9);
        return bar;
    }

    UIElement MediaRow()
    {
        var cover = Cover(null, 36, 36, 8);
        var title = Strong(""); var artist = Caption("");
        var text = Col(1); text.Children.Add(title); text.Children.Add(artist);
        var controls = new Controls(Host.Media, 4);
        _updates.Add(() =>
        {
            if (Host.Media.Current is not { } m) return;
            title.Text = m.Title; artist.Text = m.Artist.Length > 0 ? m.Artist : m.Source;
            cover.Background = m.Art is null ? Raised : new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill };
            controls.Update(m);
        });
        var bar = Bar(Bar(cover, null, null), text, null);
        var tap = Theme.Button(bar, "Open player", Host.OpenPlayer, 12, new Thickness(10, 4, 4, 4), Brushes.Transparent, Raised, Hover, stretch: true);
        var row = new DockPanel();
        DockPanel.SetDock(controls, Dock.Right);
        row.Children.Add(controls); row.Children.Add(tap);
        return row;
    }
}

sealed class Controls : StackPanel
{
    readonly Button _prev, _next, _play, _pause;
    public Controls(MediaService media, double gap)
    {
        Orientation = Orientation.Horizontal; VerticalAlignment = VerticalAlignment.Center; HorizontalAlignment = HorizontalAlignment.Center;
        _prev = Icon(Prev, "Previous", media.Previous); _play = Icon(Theme.Play, "Play", media.PlayPause); _pause = Icon(Pause, "Pause", media.PlayPause); _next = Icon(Theme.Next, "Next", media.Next);
        foreach (var b in new[] { _prev, _play, _pause, _next }) { b.Margin = new Thickness(gap / 2, 0, gap / 2, 0); Children.Add(b); }
    }
    public void Update(NowPlaying m)
    {
        _play.Visibility = m.Playing ? Visibility.Collapsed : Visibility.Visible;
        _pause.Visibility = m.Playing ? Visibility.Visible : Visibility.Collapsed;
        _prev.Opacity = m.CanPrevious ? 1 : .3; _prev.IsEnabled = m.CanPrevious;
        _next.Opacity = m.CanNext ? 1 : .3; _next.IsEnabled = m.CanNext;
    }
}

/// Progress drawn as a row of dots. Click anywhere on it to seek.
sealed class DotTimeline : FrameworkElement
{
    const int N = 50;
    public double Value { get; set; }
    public Color Tint { get; set; } = MediaColor;
    public bool CanSeek { get; set; }
    public event Action<double>? Seek;
    double? _hover;

    public DotTimeline()
    {
        Height = 18; VerticalAlignment = VerticalAlignment.Center;
        MouseMove += (_, e) => { if (CanSeek) { _hover = At(e); Cursor = Cursors.Hand; InvalidateVisual(); } };
        MouseLeave += (_, _) => { _hover = null; InvalidateVisual(); };
        MouseLeftButtonUp += (_, e) => { if (CanSeek) { Value = At(e); Seek?.Invoke(Value); InvalidateVisual(); e.Handled = true; } };
    }
    double At(MouseEventArgs e) => Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var on = Theme.Brush(Tint); var soft = Theme.Brush(Tint, .5);
        for (var i = 0; i < N; i++)
        {
            var f = i / (N - 1.0);
            var b = f <= Value ? on : _hover is { } h && f <= h ? soft : Theme.Line;
            dc.DrawEllipse(b, null, new Point(1.5 + f * (ActualWidth - 3), ActualHeight / 2), 1.5, 1.5);
        }
    }
}

sealed class PlayerView : View
{
    readonly Border _cover = Cover(null, 72, 72, 10);
    readonly TextBlock _title = Title(""), _artist = Label("", Text2), _source = Caption("", Text3), _now = MonoText("", Text2, 11), _total = MonoText("", Text3, 11);
    readonly DotMatrix _eq = new(Dots.Music);
    readonly DotTimeline _line = new();
    readonly Controls _controls;
    readonly Grid _time;
    DateTime _seeked;

    public PlayerView(IHost h) : base(h)
    {
        W = 440;
        _title.TextWrapping = TextWrapping.Wrap; _title.MaxHeight = 40;
        var text = Col(2); text.Children.Add(_title); text.Children.Add(_artist); text.Children.Add(_source);
        _time = Bar(_now, _line, _total, 10);
        _controls = new Controls(h.Media, 20);
        _line.Seek += f => { if (h.Media.Current is { } m) { _seeked = DateTime.UtcNow; h.Media.Seek(m.Duration * f); } };
        var all = Col(8);
        all.Children.Add(Bar(_cover, text, _eq, 14)); all.Children.Add(_time); all.Children.Add(_controls);
        ((FrameworkElement)all.Children[1]).Margin = new Thickness(0, 4, 0, 0);
        all.Margin = new Thickness(16, 16, 16, 8);
        Content = all;
        Update(); Tick();
    }

    public override void Update()
    {
        if (Host.Media.Current is not { } m) return;
        _title.Text = m.Title; _artist.Text = m.Artist; _artist.Visibility = m.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed; _source.Text = m.Source;
        _cover.Background = m.Art is null ? Raised : new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill };
        // Video thumbnails are wide, album covers are square.
        _cover.Width = m.Art is { } a && (double)a.PixelWidth / a.PixelHeight > 1.3 ? 128 : 72;
        _eq.Tint = _line.Tint = m.Tint; _line.CanSeek = m.CanSeek;
        _time.Visibility = m.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        _controls.Update(m);
    }

    public override void Tick()
    {
        if (Host.Media.Current is not { } m || m.Duration <= TimeSpan.Zero) return;
        if (DateTime.UtcNow - _seeked < TimeSpan.FromSeconds(1)) return;
        var now = m.Now();
        _now.Text = Clock(now); _total.Text = Clock(m.Duration);
        _line.Value = now / m.Duration; _line.InvalidateVisual();
    }
}
