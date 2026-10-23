using System.Windows;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class TileBarHitTestTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(99.9, 39.9, true)]
    [InlineData(50, 20, true)]
    [InlineData(-1, 20, false)]
    [InlineData(100, 20, false)]
    [InlineData(50, -1, false)]   // released above the bar
    [InlineData(50, 40, false)]   // released below the bar
    [InlineData(50, 500, false)]
    public void Inside_requires_both_axes(double x, double y, bool expected) =>
        Assert.Equal(expected, TileBar.Inside(new Point(x, y), 100, 40));
}
