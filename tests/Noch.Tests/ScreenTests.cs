using Noch.Core;
using Xunit;

namespace Noch.Tests;

public class ScreenTests
{
    // A laptop at 125% with its taskbar at the bottom, and a 100% screen to its right.
    static readonly ScreenInfo Laptop = new(@"\\.\DISPLAY1", new(0, 0, 1920, 1080), new(0, 0, 1920, 1020), 1.25, true);
    static readonly ScreenInfo Monitor = new(@"\\.\DISPLAY2", new(1920, 0, 3840, 1080), new(1920, 0, 3840, 1080), 1, false);
    static readonly ScreenInfo[] Both = [Laptop, Monitor];

    [Fact]
    public void Each_screen_is_measured_in_its_own_units()
    {
        Assert.Equal(1536, Laptop.Width); Assert.Equal(816, Laptop.Height);
        Assert.Equal(1920, Monitor.Width);
        Assert.Equal((2496, 50), Monitor.ToPx(576, 50));
        Assert.Equal((576.0, 50.0), Monitor.ToArea(2496, 50));
        Assert.Equal((1920, 0), Laptop.ToPx(1536, 0));
    }

    [Fact]
    public void Goes_back_to_the_screen_it_was_left_on_or_the_main_one()
    {
        Assert.Same(Monitor, Screens.Pick(Both, @"\\.\DISPLAY2"));
        Assert.Same(Laptop, Screens.Pick(Both, null));
        // Unplugged: the main screen, until it comes back.
        Assert.Same(Laptop, Screens.Pick([Laptop], @"\\.\DISPLAY2"));
        // Nothing marked main, for a moment while displays switch over.
        var only = Monitor with { Primary = false };
        Assert.Same(only, Screens.Pick([only], "gone"));
    }

    [Fact]
    public void Finds_the_screen_under_a_point()
    {
        Assert.Same(Laptop, Screens.At(Both, 1919, 500));
        Assert.Same(Monitor, Screens.At(Both, 1920, 500));
        Assert.Null(Screens.At(Both, 5000, 500));
    }

    [Fact]
    public void Sides_that_lead_onto_another_screen_are_open()
    {
        Assert.Equal([Side.Right], Screens.Open(Laptop, Both));
        Assert.Equal([Side.Left], Screens.Open(Monitor, Both));
        Assert.Empty(Screens.Open(Laptop, [Laptop]));
        // Stacked, and only touching at a corner.
        var above = new ScreenInfo("up", new(0, -1080, 1920, 0), new(0, -1080, 1920, 0), 1, false);
        var corner = new ScreenInfo("corner", new(1920, -1080, 3840, 0), new(1920, -1080, 3840, 0), 1, false);
        Assert.Equal([Side.Top], Screens.Open(Laptop, [Laptop, above, corner]));
    }

    [Fact]
    public void The_pointer_stops_only_at_a_true_edge()
    {
        Assert.True(Screens.Hard(Laptop, Side.Top, Both));
        Assert.True(Screens.Hard(Laptop, Side.Left, Both));
        Assert.False(Screens.Hard(Laptop, Side.Bottom, Both)); // the taskbar
        Assert.False(Screens.Hard(Laptop, Side.Right, Both)); // carries on to the other screen
        Assert.True(Screens.Hard(Laptop, Side.Right, [Laptop]));
        Assert.True(Screens.Hard(Monitor, Side.Bottom, Both));
    }
}
