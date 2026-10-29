using TaskbarTiles.Host.Layout;
using static TaskbarTiles.Host.Layout.SlotMode;

namespace TaskbarTiles.Tests;

public class LayoutEngineTests
{
    const double Gap = 6;

    static LayoutItem Tile(double width = 100, bool pinned = false, bool canCompact = true) => new(width, pinned, canCompact);

    static LayoutResult Run(double available, params LayoutItem[] items) => LayoutEngine.Compute(items, available, Gap);

    [Fact]
    public void Everything_fits_at_full_width()
    {
        var result = Run(312, Tile(), Tile(), Tile());

        Assert.Equal(new[] { Full, Full, Full }, result.Modes);
        Assert.False(result.ShowOverflow);
        Assert.Equal(312, result.Width);
    }

    [Fact]
    public void Unpinned_tiles_become_compact_from_the_right_end()
    {
        // 100 + 6 + 100 + 6 + 40 = 252
        var result = Run(260, Tile(), Tile(), Tile());

        Assert.Equal(new[] { Full, Full, Compact }, result.Modes);
        Assert.Equal(252, result.Width);
    }

    [Fact]
    public void Compacting_continues_leftwards_until_it_fits()
    {
        // 100 + 6 + 40 + 6 + 40 = 192
        var result = Run(200, Tile(), Tile(), Tile());

        Assert.Equal(new[] { Full, Compact, Compact }, result.Modes);
    }

    [Fact]
    public void Tiles_that_cannot_compact_stay_full_and_overflow_before_pinned_ones()
    {
        // 100 + 6 + 40 = 146: the right tile compacts, the one that cannot stays full
        Assert.Equal(new[] { Full, Compact }, Run(150, Tile(canCompact: false), Tile()).Modes);

        // 268 + 6 + 40(») = 314: both unpinned tiles move into », the pinned one stays
        Assert.Equal(new[] { Full, Overflow, Overflow }, Run(360, Tile(268, pinned: true), Tile(268, canCompact: false), Tile()).Modes);
    }

    [Fact]
    public void Pinned_tiles_are_never_compacted()
    {
        // pinned 268 + 6 + 40 + 6 + 40 = 360
        var result = Run(360, Tile(268, pinned: true), Tile(), Tile());

        Assert.Equal(new[] { Full, Compact, Compact }, result.Modes);
        Assert.Equal(360, result.Width);
    }

    [Fact]
    public void Narrow_tiles_are_not_compacted_because_it_gains_nothing()
    {
        // 30 + 6 + 40 = 76 fits once the 100 is compact; the 30 stays as it is
        var result = Run(80, Tile(30), Tile(100));

        Assert.Equal(new[] { Full, Compact }, result.Modes);
    }

    [Fact]
    public void Then_tiles_overflow_from_the_right_end_into_the_overflow_tile()
    {
        // all compact: 40*4 + 6*3 = 178 > 140; overflow the last: 40*3 + » 40 + 6*3 = 178 still; overflow two: 40*2 + 40 + 6*2 = 132
        var result = Run(140, Tile(), Tile(), Tile(), Tile());

        Assert.Equal(new[] { Compact, Compact, Overflow, Overflow }, result.Modes);
        Assert.True(result.ShowOverflow);
        Assert.Equal(132, result.Width);
        Assert.Equal(2, result.Shown);
    }

    [Fact]
    public void Pinned_tiles_overflow_last()
    {
        // pinned 100 + 6 + » 40 = 146
        var result = Run(150, Tile(), Tile(100, pinned: true), Tile());

        Assert.Equal(new[] { Overflow, Full, Overflow }, result.Modes);
        Assert.Equal(146, result.Width);
    }

    [Fact]
    public void Only_the_overflow_tile_when_nothing_else_fits()
    {
        var result = Run(45, Tile(100, pinned: true), Tile());

        Assert.Equal(new[] { Overflow, Overflow }, result.Modes);
        Assert.True(result.ShowOverflow);
        Assert.Equal(40, result.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(39)]
    [InlineData(-10)]
    [InlineData(double.NaN)]
    public void Nothing_is_shown_when_not_even_the_overflow_tile_fits(double available)
    {
        var result = Run(available, Tile(), Tile());

        Assert.Equal(new[] { Overflow, Overflow }, result.Modes);
        Assert.False(result.ShowOverflow);
        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Shown);
    }

    [Fact]
    public void No_slots_means_nothing_to_show()
    {
        var result = Run(500);

        Assert.Empty(result.Modes);
        Assert.False(result.ShowOverflow);
        Assert.Equal(0, result.Width);
    }

    [Fact]
    public void A_stack_is_one_item_with_its_widest_member_width()
    {
        // media 268 pinned, a stack of 132 and 268 counts as one 268 item: 268 + 6 + 268 = 542
        var result = Run(542, Tile(268, pinned: true), Tile(268));

        Assert.Equal(new[] { Full, Full }, result.Modes);
        var tighter = Run(541, Tile(268, pinned: true), Tile(268));
        Assert.Equal(new[] { Full, Compact }, tighter.Modes);
    }
}
