using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Noch.Core;

namespace Noch;

enum Dots { Idle, Think, Read, Search, Edit, Run, Need, Ask, Done, Fail, Music, Timer, Meeting, Bell, Speaker, Bolt, Listen }

/// The 5 by 5 dot display. The pattern says what an agent is doing.
sealed class DotMatrix : FrameworkElement
{
    static readonly double[] Levels = [.13, .32, .55, .8, 1];
    static readonly Dictionary<Color, Brush[]> Brushes = [];
    static readonly DispatcherTimer Clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(125) };
    static readonly HashSet<DotMatrix> Live = [];
    static readonly double[] Bars = [1, 2, 1, 2, 1];
    static double _loudest;
    static readonly Random Rng = new();
    static int _tick;
    public static bool MusicPlaying;
    /// How loud the meeting being listened in on is, 0 to 1.
    public static Func<double> ListenLevel = () => 0;
    static readonly double[] Heard = [1, 1, 1, 1, 1];
    public static readonly Color ListenColor = (Color)ColorConverter.ConvertFromString("#36E2C8");
    /// Holds every display on one good-looking step, for pictures.
    public static void Pose() { _pose = true; foreach (var (b, i) in new[] { 3.0, 5, 2, 4, 1 }.Select((b, i) => (b, i))) Bars[i] = b; }
    static bool _pose;

    double _level = 1;
    /// How much is left, for the timer.
    public double Level { get => _level; set { if (Math.Abs(_level - value) < .0005) return; _level = value; if (_kind == Dots.Timer) InvalidateVisual(); } }
    public bool Still { get; set; }

    readonly double _scale;
    int _start;
    Dots _kind;
    Color _tint = Theme.MediaColor;

    static DotMatrix() => Clock.Tick += (_, _) =>
    {
        _tick++;
        if (!_pose && Live.Any(m => m._kind == Dots.Music)) UpdateBars();
        if (Live.Any(m => m._kind == Dots.Listen))
        {
            // Each new level starts in the middle and moves outwards, like ripples.
            Heard[0] = Heard[1]; Heard[4] = Heard[3]; Heard[1] = Heard[3] = Heard[2];
            Heard[2] = 1 + 4 * Math.Clamp(ListenLevel(), 0, 1);
        }
        foreach (var m in Live) if (m.Moving) m.InvalidateVisual();
    };

    public DotMatrix(Dots kind, double scale = 1)
    {
        _kind = kind; _scale = scale;
        Width = Height = 19 * scale;
        VerticalAlignment = VerticalAlignment.Center;
        // Layered windows pay for every redraw, so only dots that are on screen keep the clock running.
        IsVisibleChanged += (_, _) => Track(IsVisible);
        Loaded += (_, _) => Track(IsVisible);
        Unloaded += (_, _) => Track(false);
    }

    public Dots Kind { get => _kind; set { if (_kind == value) return; _kind = value; _start = _tick; InvalidateVisual(); } }
    public Color Tint { get => _tint; set { if (_tint == value) return; _tint = value; InvalidateVisual(); } }

    void Track(bool on)
    {
        if (on) { if (Live.Add(this)) _start = _tick; } else Live.Remove(this);
        if (Live.Count > 0 && SystemParameters.ClientAreaAnimation) Clock.Start(); else Clock.Stop();
    }

    bool Moving => !_pose && !Still && _kind switch { Dots.Idle or Dots.Meeting or Dots.Bell or Dots.Speaker or Dots.Bolt => false, Dots.Done => _tick - _start <= 6, Dots.Music => true, _ => true };

    static void UpdateBars()
    {
        // Measured against the loudest recent moment, so quiet music still moves the bars.
        var peak = MusicPlaying ? AudioMeter.Peak() : 0;
        _loudest = Math.Max(peak, _loudest * .99);
        var p = peak / Math.Max(_loudest, .004);
        for (var i = 0; i < 5; i++)
        {
            var want = 1 + 4 * p * p * (.35 + .65 * Rng.NextDouble());
            Bars[i] = want > Bars[i] ? want : Math.Max(want, Bars[i] - 1.3);
        }
    }

    public static Dots For(Activity a) => a switch
    {
        Activity.Thinking => Dots.Think, Activity.Reading => Dots.Read, Activity.Searching => Dots.Search, Activity.Editing => Dots.Edit, Activity.Running => Dots.Run,
        Activity.NeedsApproval => Dots.Need, Activity.Question or Activity.Attention => Dots.Ask, Activity.Done => Dots.Done, Activity.Failed => Dots.Fail, _ => Dots.Idle
    };

    static readonly int[] Rim = [0, 1, 2, 3, 4, 9, 14, 19, 24, 23, 22, 21, 20, 15, 10, 5];
    static readonly int[] Tick = [15, 21, 17, 13, 9];
    static readonly int[] Typed = [.. new[] { 5, 3, 4, 5, 2 }.SelectMany((n, r) => Enumerable.Range(0, n).Select(c => r * 5 + c))];
    static readonly int[] Breath = [4, 3, 2, 3, 4];
    static bool Bit(string s, int r, int c) => s[r * 5 + c] == '1';

    int Lit(Dots k, int r, int c, int t)
    {
        int slow = t / 2 % 5, f = t % 5;
        switch (k)
        {
            case Dots.Think: return Math.Max(0, 4 - 2 * Math.Abs(Math.Abs(r - 2) + Math.Abs(c - 2) - f));
            case Dots.Read: { var s = t % 7; return c == s ? 4 : c == s - 1 ? 2 : c == s - 2 ? 1 : 0; }
            case Dots.Search: { var i = Array.IndexOf(Rim, r * 5 + c); if (i < 0) return r == 2 && c == 2 ? 3 : 0; var d = ((t * 2 - i) % 16 + 16) % 16; return d < 5 ? 4 - d : 0; }
            case Dots.Edit: { var n = t * 2 % 24 + 1; var i = Array.IndexOf(Typed, r * 5 + c); return i < 0 || i >= n ? 0 : i == n - 1 ? 4 : 2; }
            case Dots.Run: { var x = ((c - (2 - Math.Abs(r - 2)) - t) % 5 + 5) % 5; return x == 0 ? 4 : x == 4 ? 2 : 0; }
            case Dots.Need: return Bit("0010000100001000000000100", r, c) ? Breath[slow] : 0;
            case Dots.Ask: return Bit("0111000010001000000000100", r, c) ? Breath[slow] : 0;
            case Dots.Fail: return Bit("1000101010001000101010001", r, c) ? Breath[slow] : 0;
            case Dots.Done: { var i = Array.IndexOf(Tick, r * 5 + c); return i >= 0 && i <= t ? 4 : 0; }
            case Dots.Listen: { var h = (int)Math.Round(Heard[c]); var on = 5 - r <= h; return on ? 5 - r == h ? 4 : 2 : 0; }
            case Dots.Music: { var on = 5 - r <= (int)Math.Round(Bars[c]); return on ? 5 - r == (int)Math.Round(Bars[c]) ? 4 : 3 : 0; }
            case Dots.Timer:
            {
                // One dot for every twenty-fifth. The one burning down now breathes.
                var left = _level * 25; var full = (int)Math.Floor(left - 1e-9); var i = r * 5 + c;
                return i < full ? 4 : i == full && left > 0 ? Still ? 3 : Breath[slow] : 0;
            }
            case Dots.Meeting: return Bit("1111110001101011000111111", r, c) ? r == 2 && c == 2 ? 4 : 3 : 0;
            case Dots.Bell: return Bit("0010001110011101111100100", r, c) ? 3 : 0;
            case Dots.Speaker: return Bit("0001000110111100011000010", r, c) ? 3 : 0;
            case Dots.Bolt: return Bit("0001000100011100010001000", r, c) ? 4 : 0;
            default: return r == 2 && c == 2 ? 2 : 0;
        }
    }

    static Brush[] For(Color c)
    {
        if (!Brushes.TryGetValue(c, out var b)) Brushes[c] = b = [.. Levels.Select(o => (Brush)Theme.Brush(c, o))];
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var tone = For(_kind switch { Dots.Need or Dots.Ask => Theme.NeedColor, Dots.Done => Theme.DoneColor, Dots.Fail => Theme.FailColor, Dots.Music or Dots.Timer or Dots.Listen => _tint, Dots.Bolt => Theme.DoneColor, _ => Theme.TextColor });
        var t = _pose ? _kind is Dots.Done ? 9 : _kind is Dots.Edit ? 7 : _kind is Dots.Need or Dots.Ask or Dots.Fail ? 0 : 3 : (SystemParameters.ClientAreaAnimation ? _tick - _start : _kind == Dots.Done ? 9 : 3);
        for (var r = 0; r < 5; r++)
            for (var c = 0; c < 5; c++)
                dc.DrawEllipse(tone[Math.Clamp(Lit(_kind, r, c, t), 0, 4)], null, new Point((c * 4 + 1.5) * _scale, (r * 4 + 1.5) * _scale), 1.5 * _scale, 1.5 * _scale);
    }
}
