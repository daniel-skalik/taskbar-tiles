using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class FitTilesTests
{
    [Fact]
    public void All_fit() => Assert.Equal(3, FitTiles.Count([100, 100, 100], available: 312, gap: 6));

    [Fact]
    public void Gap_is_counted_between_tiles() => Assert.Equal(2, FitTiles.Count([100, 100, 100], available: 311, gap: 6));

    [Fact]
    public void None_fit() => Assert.Equal(0, FitTiles.Count([268], available: 200, gap: 6));

    [Fact]
    public void Empty_list() => Assert.Equal(0, FitTiles.Count([], available: 500, gap: 6));
}
