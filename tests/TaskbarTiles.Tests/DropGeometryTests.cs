using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;

namespace TaskbarTiles.Tests;

public class DropGeometryTests
{
    static readonly SlotSpan[] TwoSlots = [new(0, 100), new(106, 100)];

    [Theory]
    [InlineData(-5, 0, false)]  // left of the first slot
    [InlineData(10, 0, false)]  // left part of the first slot
    [InlineData(50, 0, true)]   // middle of the first slot
    [InlineData(90, 1, false)]  // right part of the first slot
    [InlineData(103, 1, false)] // the 6 DIP between the slots
    [InlineData(150, 1, true)]  // middle of the second slot
    [InlineData(300, 2, false)] // past the last slot: », + and Done
    public void Spot_at_a_point(double x, int index, bool into) =>
        Assert.Equal(new DropSpot(index, into), DropGeometry.SpotAt(TwoSlots, x));

    [Fact]
    public void A_bar_without_slots_has_only_the_first_gap() =>
        Assert.Equal(new DropSpot(0, false), DropGeometry.SpotAt([], 25));

    [Fact]
    public void Into_a_slot_drops_into_its_config_index() =>
        Assert.Equal(((int?)5, DropPosition.Into), DropGeometry.ToDrop(new DropSpot(1, true), [2, 5]));

    [Fact]
    public void A_gap_drops_before_the_slot_after_it()
    {
        Assert.Equal(((int?)2, DropPosition.Before), DropGeometry.ToDrop(new DropSpot(0, false), [2, 5]));
        Assert.Equal(((int?)5, DropPosition.Before), DropGeometry.ToDrop(new DropSpot(1, false), [2, 5]));
    }

    [Fact]
    public void The_gap_after_the_last_slot_drops_after_it() =>
        Assert.Equal(((int?)5, DropPosition.After), DropGeometry.ToDrop(new DropSpot(2, false), [2, 5]));

    [Fact]
    public void A_bar_without_slots_appends() =>
        Assert.Equal(((int?)null, DropPosition.After), DropGeometry.ToDrop(new DropSpot(0, false), []));
}
