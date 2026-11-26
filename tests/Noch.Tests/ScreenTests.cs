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

public class HoldKeysTests
{
    static readonly DateTime T0 = new(2026, 1, 1);

    [Fact]
    public void Held_alone_for_a_moment_starts_and_letting_go_finishes()
    {
        var k = new HoldKeys();
        Assert.Equal(KeyAction.None, k.Step(true, true, false, false, T0));
        Assert.Equal(KeyAction.None, k.Step(true, true, false, false, T0.AddMilliseconds(200)));
        Assert.Equal(KeyAction.Start, k.Step(true, true, false, false, T0.AddMilliseconds(400)));
        Assert.Equal(KeyAction.None, k.Step(true, true, false, true, T0.AddSeconds(3)));
        Assert.Equal(KeyAction.Finish, k.Step(true, false, false, true, T0.AddSeconds(4)));
    }

    [Fact]
    public void A_shortcut_like_ctrl_alt_y_never_starts_it()
    {
        var k = new HoldKeys();
        k.Step(true, true, false, false, T0);
        Assert.Equal(KeyAction.None, k.Step(true, true, true, false, T0.AddMilliseconds(100)));
        // Still holding Ctrl Alt after letting go of Y: nothing until both are let go.
        Assert.Equal(KeyAction.None, k.Step(true, true, false, false, T0.AddSeconds(2)));
        k.Step(false, false, false, false, T0.AddSeconds(3));
        k.Step(true, true, false, false, T0.AddSeconds(4));
        Assert.Equal(KeyAction.Start, k.Step(true, true, false, false, T0.AddSeconds(5)));
    }

    [Fact]
    public void Another_key_while_listening_throws_it_away()
    {
        var k = new HoldKeys();
        k.Step(true, true, false, false, T0);
        k.Step(true, true, false, false, T0.AddSeconds(1));
        Assert.Equal(KeyAction.Cancel, k.Step(true, true, true, true, T0.AddSeconds(2)));
        Assert.Equal(KeyAction.None, k.Step(true, true, false, false, T0.AddSeconds(3)));
        Assert.Equal(KeyAction.None, k.Step(false, true, false, false, T0.AddSeconds(4)));
    }
}
