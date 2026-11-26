using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Noch.Core;
using static Noch.Theme;
using Phase = Noch.VoiceService.Phase;

namespace Noch;

/// Talking to Noch: the bar while it listens, then what it heard.
sealed class VoiceView : View
{
    readonly VoiceService _v;
    readonly TextBlock? _clock, _percent;

    public VoiceView(IHost h) : base(h)
    {
        _v = h.S.Voice;
        switch (_v.Now)
        {
            case Phase.Listening or Phase.Working:
            {
                var aurora = new Aurora(() => _v.Level, h.Side) { Busy = _v.Now == Phase.Working };
                if (h.Vertical) { W = 44; Height = 120; Radius = 16; Ear = 8; Content = aurora; return; }
                // The same size as the everyday bar, so it does not jump about.
                W = 300; Height = 36; Radius = 14; Ear = 8;
                _clock = MonoText("", Text2);
                var bar = Bar(null, Label(_v.Now == Phase.Listening ? "Listening" : "Writing it down"), _v.Now == Phase.Listening ? _clock : null);
                bar.Margin = new Thickness(14, 0, 14, 0); bar.VerticalAlignment = VerticalAlignment.Center;
                var g = new Grid { Height = 36 };
                g.Children.Add(aurora); g.Children.Add(bar);
                Content = g;
                Tick();
                break;
            }
            case Phase.Said:
            {
                W = 440;
                var said = Label(_v.Text);
                said.TextWrapping = TextWrapping.Wrap; said.TextTrimming = TextTrimming.None;
                var col = Col(3);
                col.Children.Add(Caption("You said")); col.Children.Add(said);
                col.Margin = new Thickness(16, 11, 16, 12);
                Content = NoticeView.Tappable(h, col, "Clear", _v.Dismiss);
                break;
            }
            case Phase.NeedsModel:
            {
                W = 420;
                var col = Col(1);
                col.Children.Add(Label("Talking to Noch needs a speech file"));
                col.Children.Add(Caption($"{VoiceService.ModelMegabytes} MB, once. What you say stays on this PC."));
                var bar = Bar(null, col, TextButton("Get it", _v.Download, primary: true));
                bar.Margin = new Thickness(16, 11, 12, 11);
                Content = NoticeView.Tappable(h, bar, "Clear", _v.Dismiss);
                break;
            }
            case Phase.Downloading:
            {
                W = 300; Height = 36; Radius = 14; Ear = 8;
                _percent = MonoText("", Text2);
                var bar = Bar(null, Label("Getting the speech file"), _percent);
                bar.Margin = new Thickness(14, 0, 14, 0); bar.VerticalAlignment = VerticalAlignment.Center;
                Content = new Border { Background = Brushes.Transparent, Height = 36, Child = bar };
                Update();
                break;
            }
            default:
            {
                W = 340; Height = 36; Radius = 14; Ear = 8;
                var bar = Bar(null, Label(_v.Text), null);
                bar.Margin = new Thickness(14, 0, 14, 0); bar.VerticalAlignment = VerticalAlignment.Center;
                Content = NoticeView.Tappable(h, new Border { Height = 36, Child = bar }, "Clear", _v.Dismiss);
                break;
            }
        }
    }

    public override bool ClickThrough => _v.Now is Phase.Listening or Phase.Working;
    public override void Update() { if (_percent != null) _percent.Text = (int)(_v.Progress * 100) + "%"; }
    public override void Tick() { if (_clock != null) _clock.Text = Clock(DateTime.UtcNow - _v.Started); }
}

/// Soft coloured light rising from the open side of the bar, swelling with the voice.
sealed class Aurora : FrameworkElement
{
    static readonly Brush[] Glows = [.. new[] { "#36E2C8", "#4D7CFF", "#A15CFF", "#FF5FA8" }.Select(Glow)];
    readonly Func<double> _level;
    readonly Side _side;
    readonly DispatcherTimer _clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DateTime _born = DateTime.UtcNow;
    double _smooth;

    /// Writing it down: no voice to follow, so it drifts.
    public bool Busy { get; set; }

    public Aurora(Func<double> level, Side side)
    {
        _level = level; _side = side;
        IsHitTestVisible = false;
        _clock.Tick += (_, _) => InvalidateVisual();
        // Layered windows pay for every redraw, so it only moves while it is on screen.
        Loaded += (_, _) => { if (SystemParameters.ClientAreaAnimation) _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
    }

    static Brush Glow(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var b = new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(235, c.R, c.G, c.B), 0), new(Color.FromArgb(110, c.R, c.G, c.B), .5), new(Color.FromArgb(0, c.R, c.G, c.B), 1)
        });
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var t = (DateTime.UtcNow - _born).TotalSeconds;
        var want = Busy ? .18 + .1 * Math.Sin(t * 2.6) : _level();
        // Quick to rise with a word, slower to settle after it.
        _smooth += (want - _smooth) * (want > _smooth ? .55 : .14);
        var lv = Math.Clamp(_smooth, 0, 1);
        var pace = Busy ? 1.8 : 1;
        bool upright = _side is Side.Left or Side.Right;
        double along = upright ? h : w, depth = upright ? w : h;
        for (var i = 0; i < Glows.Length; i++)
        {
            var at = along * (.5 + .42 * Math.Sin(t * (.35 + .13 * i) * pace + i * 1.7));
            var spread = along * (.26 + .06 * Math.Sin(t * .9 + i * 2.1));
            var reach = depth * (.45 + 1.25 * lv * (.75 + .25 * Math.Sin(t * 4.3 + i * 1.3)));
            // Centred on the side away from the screen edge, so the light comes up out of it.
            var centre = _side switch
            {
                Side.Bottom => new Point(at, 0), Side.Left => new Point(w, at), Side.Right => new Point(0, at), _ => new Point(at, h)
            };
            dc.PushOpacity(.3 + .7 * lv);
            dc.DrawEllipse(Glows[i], null, centre, upright ? reach : spread, upright ? spread : reach);
            dc.Pop();
        }
    }
}
