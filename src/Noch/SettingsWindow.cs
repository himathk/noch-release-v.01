using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Noch.Core;
using static Noch.Theme;

namespace Noch;

/// An ordinary window for everything that can be changed.
sealed class SettingsWindow : Window
{
    readonly AppServices _s;
    readonly ISettingsActions _do;
    readonly StackPanel _body = new() { Margin = new Thickness(28, 8, 28, 28) };
    readonly List<Action> _live = [];

    public interface ISettingsActions
    {
        IReadOnlyList<(string Name, Connector Link)> Agents { get; }
        ClaudeConnector Claude { get; }
        /// Returns what went wrong, or null.
        string? Try(Action a);
        void Refresh();
        string Version { get; }
        void CheckForUpdates(Action<string> tell);
        string? Repo { get; }
    }

    public SettingsWindow(AppServices s, ISettingsActions actions)
    {
        _s = s; _do = actions;
        Title = "Noch settings"; Width = 560; Height = 680; MinWidth = 460; MinHeight = 400;
        Background = Brush(Color.FromRgb(0x0C, 0x0D, 0x0D)); FontFamily = Sans; Foreground = Text;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; UseLayoutRounding = true;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/noch.ico")); } catch { }
        Resources.Add(typeof(ScrollBar), Theme.ScrollBar);
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        SourceInitialized += (_, _) => { var on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4); };
        Build();
        void Live() => Dispatcher.BeginInvoke(() => { foreach (var a in _live) a(); });
        s.Calendar.Changed += Live; s.Usage.Changed += Live;
        Activated += (_, _) => Live();
        // Reachable with Tab, unlike the buttons in the notch, which never takes focus.
        Loaded += (_, _) => { foreach (var b in All<Button>(this)) b.Focusable = true; };
        Closed += (_, _) => { s.Calendar.Changed -= Live; s.Usage.Changed -= Live; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    static IEnumerable<T> All<T>(DependencyObject o) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            if (c is T t) yield return t;
            foreach (var x in All<T>(c)) yield return x;
        }
    }

    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    void Save() { _s.Settings.Save(); _do.Refresh(); }

    void Build()
    {
        _body.Children.Clear(); _live.Clear();
        var st = _s.Settings;

        Head("Agents");
        foreach (var (name, link) in _do.Agents)
        {
            var row = Row(name, "", out var detail, out var right);
            var err = "";
            Button b = null!;
            b = NoticeView.Small("", () => { err = _do.Try(() => { if (link.Connected) link.Disconnect(); else link.Connect(); }) ?? ""; _do.Refresh(); Live(); });
            right.Child = b;
            void Live()
            {
                var on = link.Connected;
                ((TextBlock)((GapPanel)b.Content).Children[0]).Text = on ? "Disconnect" : "Connect";
                AutomationProperties.SetName(b, (on ? "Disconnect " : "Connect ") + name);
                b.IsEnabled = link.AgentInstalled; b.Opacity = b.IsEnabled ? 1 : .4;
                detail.Text = err.Length > 0 ? err : !link.AgentInstalled ? "Not found on this PC" : on ? name == "Codex" ? "Connected. Type /hooks in Codex once and trust them." : "Connected" : "Not connected";
            }
            _live.Add(Live); Live();
        }
        {
            var err = "";
            Toggle("Usage limits", "", () => _do.Claude.StatusLineOn, on => { err = _do.Try(() => _do.Claude.SetStatusLine(on)) ?? ""; }, d =>
                d.Text = err.Length > 0 ? err : _do.Claude.HasOtherStatusLine ? "You already have your own status line, so this is left off."
                    : "Reads how much of your Claude plan is used. Claude Code only shares this in a terminal, not in the desktop app.");
        }

        Head("Sound");
        Toggle("Sounds", "When an agent needs you, finishes or stops", () => st.Sounds, on => { st.Sounds = on; Save(); });
        {
            var row = Row("Volume", "Click the dots to set it and hear it", out _, out var right);
            var level = new DotLevel(20, 120) { Value = st.SoundVolume, Cursor = Cursors.Hand, Height = 24 };
            level.Picked += v => { st.SoundVolume = Math.Max(.05, Math.Round(v * 20) / 20); level.Value = st.SoundVolume; Save(); _s.Sounds.Play(Sounds.Tone.Finished, preview: true); };
            AutomationProperties.SetName(level, "Sound volume");
            right.Child = level;
        }

        Head("Focus");
        Choice("Focus length", "", [15, 20, 25, 30, 45, 50, 60, 90], st.FocusMinutes, v => { st.FocusMinutes = v; Save(); }, "min");
        Choice("Break length", "Offered when a focus block ends", [3, 5, 10, 15, 20], st.BreakMinutes, v => { st.BreakMinutes = v; Save(); }, "min");

        Head("Calendar");
        {
            var err = "";
            var row = Row("Meeting countdown", "", out var detail, out var right);
            var box = Box("https://calendar.google.com/…/basic.ics", "Calendar link"); box.Margin = new Thickness(0, 0, 0, 12);
            var help = Caption("Paste your calendar's private iCal link. In Google Calendar: Settings, your calendar, Integrate calendar, Secret address in iCal format. In Outlook: Settings, Calendar, Shared calendars, Publish, ICS. The link is stored encrypted on this PC.", Text3);
            Button act = null!;
            act = NoticeView.Small("", () =>
            {
                if (_s.Calendar.On) { _s.Calendar.Forget(); err = ""; }
                else { err = _s.Calendar.SetLink(box.Text) ?? ""; if (err.Length == 0) box.Text = ""; }
                _do.Refresh(); Live();
            });
            right.Child = act;
            _body.Children.Add(box);
            help.TextWrapping = TextWrapping.Wrap; help.TextTrimming = TextTrimming.None; help.Margin = new Thickness(0, 0, 0, 14);
            _body.Children.Add(help);
            void Live()
            {
                var on = _s.Calendar.On;
                box.Visibility = help.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
                ((TextBlock)((GapPanel)act.Content).Children[0]).Text = on ? "Remove" : "Connect";
                AutomationProperties.SetName(act, on ? "Remove calendar" : "Connect calendar");
                detail.Text = err.Length > 0 ? err : on ? _s.Calendar.Status : "Shows the next meeting, with a Join button";
            }
            _live.Add(Live); Live();
        }

        Head("Notifications");
        Toggle("Show other apps' notifications", "", () => st.Notifications, on => { st.Notifications = on; Save(); _ = _s.Notifications.Start().ContinueWith(_ => Dispatcher.BeginInvoke(() => { foreach (var a in _live) a(); })); }, d =>
            d.Text = _s.Notifications.Status.Length > 0 ? _s.Notifications.Status : "Windows still shows its own unless Do not disturb is on. Nothing is stored.");
        Toggle("Hide what they say", "Shows only which app it was", () => st.HideNotificationText, on => { st.HideNotificationText = on; Save(); });
        {
            var row = Row("Muted apps", "", out var detail, out var right);
            right.Child = NoticeView.Small("Unmute all", () => { st.MutedApps.Clear(); Save(); Live(); });
            void Live() { detail.Text = st.MutedApps.Count == 0 ? "None. Use Mute on a notification to add one." : string.Join(", ", st.MutedApps.Order()); right.Visibility = st.MutedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
            _live.Add(Live); Live();
        }

        Head("Notch");
        Toggle("Start with Windows", "", () => Settings.StartsWithWindows, on => Settings.StartsWithWindows = on);
        Toggle("Show the full bar over windows", "Off: it shrinks to a thin strip over a maximised window", () => st.AlwaysFull, on => { st.AlwaysFull = on; Save(); });
        Toggle("Scroll over music to change volume", "", () => st.WheelVolume, on => { st.WheelVolume = on; Save(); });
        Toggle("Battery messages", "When you plug in, and when it runs low", () => st.PowerAlerts, on => { st.PowerAlerts = on; Save(); });
        Toggle("Open with Ctrl Alt .", "Opens and closes the notch from anywhere", () => st.Shortcut, on => { st.Shortcut = on; Save(); });
        Toggle("Hold Ctrl Alt to talk", "It listens only while you hold both keys", () => st.Voice, on => { st.Voice = on; Save(); });

        Head("Your week");
        {
            var days = _s.Stats.Last(7, DateTime.Now);
            var row = Row(days.Sum(d => d.Turns) is var t && t == 1 ? "1 turn" : $"{t} turns", "", out var detail, out var right);
            var parts = new List<string>();
            if (days.Sum(d => d.Seconds) is var sec and >= 60) parts.Add(Stats.Span(TimeSpan.FromSeconds(sec)) + " of agent work");
            if (days.Sum(d => d.Answers) is var ans and > 0) parts.Add(ans == 1 ? "1 answer from the notch" : $"{ans} answers from the notch");
            if (days.Sum(d => d.FocusMinutes) is var foc and > 0) parts.Add(Stats.Span(TimeSpan.FromMinutes(foc)) + " focused");
            detail.Text = parts.Count > 0 ? string.Join(", ", parts) : "Counted here as you work. Kept on this PC only.";
            right.Child = new Week(days);
        }

        Head("About");
        {
            var row = Row("Noch " + _do.Version, "", out var detail, out var right);
            right.Child = NoticeView.Small("Check for updates", () => { detail.Text = "Checking…"; _do.CheckForUpdates(t => Dispatcher.BeginInvoke(() => detail.Text = t)); });
            if (_do.Repo is { } repo) { var l = NoticeView.Small("Open on GitHub", () => AppServices.Open(repo), true); l.HorizontalAlignment = HorizontalAlignment.Left; l.Margin = new Thickness(-8, 0, 0, 0); _body.Children.Add(l); }
        }
    }

    /// Seven columns of five dots, the tallest day reaching the top.
    sealed class Week : FrameworkElement
    {
        readonly double[] _v;
        public Week(IReadOnlyList<Day> days)
        {
            _v = [.. Enumerable.Range(0, 7).Select(i => DateTime.Now.AddDays(i - 6).ToString("yyyy-MM-dd")).Select(k => days.FirstOrDefault(d => d.Date == k)?.Seconds ?? 0)];
            Width = 7 * 14 - 6; Height = 5 * 9 - 3;
            AutomationProperties.SetName(this, "Agent work over the last seven days");
        }
        protected override void OnRender(DrawingContext dc)
        {
            var top = Math.Max(1, _v.Max());
            for (var d = 0; d < 7; d++)
                for (var r = 0; r < 5; r++)
                    dc.DrawEllipse(_v[d] > 0 && 5 - r <= Math.Max(1, Math.Round(_v[d] / top * 5)) ? d == 6 ? Text : Text3 : Theme.Line, null, new Point(4 + d * 14, 3 + r * 9), 3, 3);
        }
    }

    void Head(string t)
    {
        var h = Title(t);
        h.Margin = new Thickness(0, _body.Children.Count == 0 ? 12 : 22, 0, 6);
        _body.Children.Add(h);
    }

    Grid Row(string title, string detail, out TextBlock detailText, out Border right)
    {
        var text = Col(2);
        text.Children.Add(Label(title));
        detailText = Caption(detail); detailText.TextWrapping = TextWrapping.Wrap; detailText.TextTrimming = TextTrimming.None;
        text.Children.Add(detailText);
        right = new Border { VerticalAlignment = VerticalAlignment.Center };
        var g = new Grid { Margin = new Thickness(0, 8, 0, 8), MinHeight = 36 };
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        text.Margin = new Thickness(0, 0, 20, 0);
        g.Children.Add(text); Grid.SetColumn(right, 1); g.Children.Add(right);
        _body.Children.Add(g);
        return g;
    }

    static readonly ControlTemplate Switch = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
          <Border x:Name="Track" Width="40" Height="22" CornerRadius="11" Background="#2A2B2C" BorderThickness="2" BorderBrush="Transparent">
            <Ellipse x:Name="Knob" Width="14" Height="14" Fill="#A6A9AB" HorizontalAlignment="Left" Margin="2,0,0,0"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Track" Property="Background" Value="#F4F5F5"/>
              <Setter TargetName="Knob" Property="Fill" Value="#000000"/>
              <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
              <Setter TargetName="Knob" Property="Margin" Value="0,0,2,0"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Track" Property="BorderBrush" Value="#FFB020"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    /// Reports every way of flipping it: mouse, keyboard and screen readers.
    sealed class Flip : ToggleButton
    {
        public Action<bool> Flipped = _ => { };
        protected override void OnToggle() { base.OnToggle(); Flipped(IsChecked == true); }
    }

    void Toggle(string title, string detail, Func<bool> get, Action<bool> set, Action<TextBlock>? describe = null)
    {
        Row(title, detail, out var d, out var right);
        var t = new Flip { Template = Switch, Cursor = Cursors.Hand, IsChecked = get(), FocusVisualStyle = null };
        AutomationProperties.SetName(t, title);
        void Live() { t.IsChecked = get(); describe?.Invoke(d); if (d.Text.Length == 0) d.Visibility = Visibility.Collapsed; else d.Visibility = Visibility.Visible; }
        t.Flipped = on => { set(on); foreach (var a in _live) a(); };
        right.Child = t;
        _live.Add(Live); Live();
    }

    void Choice(string title, string detail, int[] values, int now, Action<int> set, string unit)
    {
        Row(title, detail, out var d, out var right);
        if (detail.Length == 0) d.Visibility = Visibility.Collapsed;
        var list = values.Append(now).Distinct().Order().ToList();
        var at = list.IndexOf(now);
        var text = MonoText($"{now} {unit}", Text); text.Width = 64; text.TextAlignment = TextAlignment.Center;
        void Step(int by) { at = Math.Clamp(at + by, 0, list.Count - 1); text.Text = $"{list[at]} {unit}"; set(list[at]); }
        var row = Theme.Row(4);
        Button Arrow(string data, string name, int by) => Theme.Button(new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Stroke = Text, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }, name, () => Step(by), 8, new Thickness(9, 8, 9, 8));
        row.Children.Add(Arrow("M5,0 L0,5 L5,10", $"Shorter {title}", -1)); row.Children.Add(text); row.Children.Add(Arrow("M0,0 L5,5 L0,10", $"Longer {title}", 1));
        foreach (var b in row.Children.OfType<Button>()) b.Focusable = true;
        right.Child = row;
    }
}
