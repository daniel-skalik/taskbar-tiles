using TaskbarTiles.Host.Taskbar;

namespace TaskbarTiles.Tests;

public class PixelRectTests
{
    [Theory]
    [InlineData(10, 20, true)]   // top-left pixel
    [InlineData(109, 59, true)]  // bottom-right pixel
    [InlineData(110, 30, false)] // right edge is outside
    [InlineData(50, 60, false)]  // bottom edge is outside (released below the bar)
    [InlineData(9, 30, false)]
    [InlineData(50, 19, false)]  // released above the bar
    public void Contains_includes_left_and_top_but_not_right_and_bottom(int x, int y, bool expected) =>
        Assert.Equal(expected, new PixelRect(10, 20, 110, 60).Contains(x, y));
}
