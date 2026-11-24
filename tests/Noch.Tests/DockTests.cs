using Noch.Core;
using Xunit;

namespace Noch.Tests;

public class DockTests
{
    const double W = 2560, H = 1392;

    [Theory]
    [InlineData(1300, 40, Side.Top, .5)]      // near the middle of the top: pulled to the centre
    [InlineData(20, 60, Side.Top, 0)]         // corners, whichever edge is nearer
    [InlineData(60, 20, Side.Top, 0)]
    [InlineData(2550, 1300, Side.Bottom, 1)]
    [InlineData(10, 1380, Side.Bottom, 0)]
    [InlineData(30, 700, Side.Left, .5)]
    [InlineData(2540, 400, Side.Right, 400 / H)]
    [InlineData(700, 1370, Side.Bottom, 700 / W)]
    public void Drops_settle_where_expected(double x, double y, Side side, double along)
    {
        var d = Dock.Nearest(x, y, W, H);
        Assert.Equal(side, d.Side);
        Assert.Equal(along, d.Along, 6);
    }

    [Fact]
    public void Nothing_ever_leaves_the_screen()
    {
        for (var x = 0.0; x <= W; x += 40)
            for (var y = 0.0; y <= H; y += 40)
                foreach (var (w, h) in new[] { (300.0, 36.0), (44, 140), (480, 500), (120, 6) })
                {
                    var b = Dock.Nearest(x, y, W, H).Place(w, h, W, H);
                    Assert.True(b.X >= 0 && b.Y >= 0 && b.X + b.W <= W + .001 && b.Y + b.H <= H + .001, $"{x},{y} {b}");
                }
    }

    [Fact]
    public void Touches_its_edge_and_both_edges_in_a_corner()
    {
        Assert.Equal(H, new Dock(Side.Bottom, .3).Place(300, 36, W, H) is var b ? b.Y + b.H : 0);
        Assert.Equal(W, new Dock(Side.Right, .3).Place(44, 100, W, H) is var r ? r.X + r.W : 0);
        var c = new Dock(Side.Top, 1).Place(300, 36, W, H);
        Assert.Equal((W, 0.0), (c.X + c.W, c.Y));
    }

    [Theory]
    [InlineData("Left:0.25", Side.Left, .25)]
    [InlineData("Top:7", Side.Top, .5)]
    [InlineData("nonsense", Side.Top, .5)]
    [InlineData(null, Side.Top, .5)]
    public void Saved_position_survives_and_bad_ones_fall_back(string? s, Side side, double along) => Assert.Equal(new Dock(side, along), Dock.Parse(s));

    [Fact]
    public void Round_trips() => Assert.Equal(new Dock(Side.Right, .1234), Dock.Parse(new Dock(Side.Right, .1234).ToString()));
}
