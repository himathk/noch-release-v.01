using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Noch;

/// Colours, type and small building blocks, matching the Figma file.
static class Theme
{
    static Color C(string h) => (Color)ColorConverter.ConvertFromString(h);
    public static SolidColorBrush Brush(Color c, double opacity = 1) { var b = new SolidColorBrush(c) { Opacity = opacity }; b.Freeze(); return b; }

    public static readonly Color MediaColor = C("#FF8F6B"), TextColor = C("#F4F5F5"), NeedColor = C("#FFB020"), DoneColor = C("#4FD08A"), FailColor = C("#FF6A5C");
    public static readonly Brush Surface = Brush(Colors.Black), Raised = Brush(C("#171818")), Hover = Brush(C("#222425")), Pressed = Brush(C("#2C2E30")), Line = Brush(C("#2A2B2C")),
        Text = Brush(TextColor), Text2 = Brush(C("#A6A9AB")), Text3 = Brush(C("#7E8183")), Need = Brush(NeedColor), NeedHover = Brush(C("#FFBF4A")), NeedPressed = Brush(C("#E59B12")), Done = Brush(DoneColor), Fail = Brush(FailColor);

    // This build of Host Grotesk names its family "Light" whatever the weight.
    public static readonly FontFamily Sans = new(new Uri("pack://application:,,,/"), "./Fonts/#Host Grotesk Light, Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Mono = new(new Uri("pack://application:,,,/"), "./Fonts/#Geist Mono, Cascadia Mono, Consolas");

    static TextBlock Make(string s, double size, double line, FontWeight w, Brush c, FontFamily f) => new()
    {
        Text = s, FontSize = size, LineHeight = line, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, FontWeight = w, Foreground = c, FontFamily = f,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
    };
    public static TextBlock Title(string s) => Make(s, 15, 20, FontWeights.SemiBold, Text, Sans);
    public static TextBlock Strong(string s, Brush? c = null) => Make(s, 13, 18, FontWeights.SemiBold, c ?? Text, Sans);
    public static TextBlock Label(string s, Brush? c = null) => Make(s, 13, 18, FontWeights.Medium, c ?? Text, Sans);
    public static TextBlock Caption(string s, Brush? c = null) => Make(s, 12, 16, FontWeights.Normal, c ?? Text2, Sans);
    public static TextBlock MonoText(string s, Brush? c = null, double size = 12) => Make(s, size, size < 12 ? 16 : 18, FontWeights.Normal, c ?? Text3, Mono);

    static readonly ControlTemplate Template = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Border Background="{TemplateBinding Background}" CornerRadius="{TemplateBinding Tag}" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
          </Border>
        </ControlTemplate>
        """);

    public static Button Button(UIElement content, string name, Action click, double radius = 10, Thickness? pad = null, Brush? back = null, Brush? hover = null, Brush? pressed = null, bool stretch = false)
    {
        back ??= Raised; hover ??= Hover; pressed ??= Pressed;
        var b = new Button
        {
            Template = Template, Content = content, Tag = new CornerRadius(radius), Padding = pad ?? new Thickness(12, 9, 12, 9), Background = back, Focusable = false, Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Center
        };
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = back;
        b.PreviewMouseLeftButtonDown += (_, _) => b.Background = pressed;
        b.PreviewMouseLeftButtonUp += (_, _) => b.Background = b.IsMouseOver ? hover : back;
        return b;
    }

    public static Button TextButton(string text, Action click, string? shortcut = null, bool primary = false)
    {
        var p = Row(8);
        p.Children.Add(Strong(text, primary ? Surface : Text));
        if (shortcut != null) p.Children.Add(MonoText(shortcut, primary ? Surface : Text2, 11));
        return primary ? Button(p, text, click, back: Need, hover: NeedHover, pressed: NeedPressed) : Button(p, text, click);
    }

    public static Button Icon(string data, string name, Action click, double size = 20)
    {
        var path = new Path { Data = Geometry.Parse(data), Fill = Text, Width = 20, Height = 20 };
        return Button(new Viewbox { Width = size, Height = size, Child = path }, name, click, 10, new Thickness(8), Brushes.Transparent);
    }

    public const string Prev = "M16,4.5 V15.5 L7.5,10 Z M4,4.5 H6 V15.5 H4 Z", Next = "M4,4.5 V15.5 L12.5,10 Z M14,4.5 H16 V15.5 H14 Z",
        Pause = "M5.5,4 H8.5 V16 H5.5 Z M11.5,4 H14.5 V16 H11.5 Z", Play = "M6,3.5 V16.5 L16.5,10 Z";

    public static GapPanel Row(double gap) => new() { Gap = gap, Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    public static GapPanel Col(double gap) => new() { Gap = gap, Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };

    /// Left part, flexible middle, right part.
    public static Grid Bar(UIElement? left, UIElement? middle, UIElement? right, double gap = 12)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        void Put(UIElement? e, int c, Thickness m) { if (e is null) return; if (e is FrameworkElement f) f.Margin = m; Grid.SetColumn(e, c); g.Children.Add(e); }
        Put(left, 0, default);
        Put(middle, 1, new Thickness(left is null ? 0 : gap, 0, right is null ? 0 : gap, 0));
        Put(right, 2, default);
        return g;
    }

    public static Border Cover(ImageSource? art, double w, double h, double r) => new()
    {
        Width = w, Height = h, CornerRadius = new CornerRadius(r), VerticalAlignment = VerticalAlignment.Center,
        Background = art is null ? Raised : new ImageBrush(art) { Stretch = Stretch.UniformToFill }
    };
}

/// A stack with even spacing between its visible children.
sealed class GapPanel : Panel
{
    public Orientation Orientation { get; set; }
    public double Gap { get; set; }

    protected override Size MeasureOverride(Size a)
    {
        double main = 0, cross = 0; var n = 0;
        var h = Orientation == Orientation.Horizontal;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(h ? new Size(double.PositiveInfinity, a.Height) : new Size(a.Width, double.PositiveInfinity));
            if (c.Visibility == Visibility.Collapsed) continue;
            main += (h ? c.DesiredSize.Width : c.DesiredSize.Height) + (n++ > 0 ? Gap : 0);
            cross = Math.Max(cross, h ? c.DesiredSize.Height : c.DesiredSize.Width);
        }
        return h ? new Size(main, cross) : new Size(cross, main);
    }

    protected override Size ArrangeOverride(Size f)
    {
        double at = 0;
        var h = Orientation == Orientation.Horizontal;
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            var s = h ? c.DesiredSize.Width : c.DesiredSize.Height;
            c.Arrange(h ? new Rect(at, 0, s, f.Height) : new Rect(0, at, f.Width, s));
            at += s + Gap;
        }
        return f;
    }
}
