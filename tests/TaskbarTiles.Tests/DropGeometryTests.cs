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
    public void Past_the_last_shown_slot_appends_to_the_end_of_the_layout() =>
        Assert.Equal(((int?)null, DropPosition.After), DropGeometry.ToDrop(new DropSpot(2, false), [2, 5]));

    [Fact]
    public void A_bar_without_slots_appends() =>
        Assert.Equal(((int?)null, DropPosition.After), DropGeometry.ToDrop(new DropSpot(0, false), []));

    [Fact]
    public void On_another_bar_the_slots_after_the_gap_slide_right()
    {
        Assert.True(DropGeometry.OpensGap(new DropSpot(1, false), dragged: -1));
        Assert.Equal(new double[] { 0, 40, 40 }, DropGeometry.Shifts(3, dragged: -1, new DropSpot(1, false), 40));
    }

    [Fact]
    public void Stacking_or_no_spot_opens_no_gap()
    {
        Assert.False(DropGeometry.OpensGap(new DropSpot(1, true), -1));
        Assert.False(DropGeometry.OpensGap(null, -1));
        Assert.Equal(new double[] { 0, 0 }, DropGeometry.Shifts(2, -1, new DropSpot(1, true), 40));
        Assert.Equal(new double[] { 0, 0 }, DropGeometry.Shifts(2, -1, null, 40));
    }

    [Fact]
    public void The_dragged_slots_own_place_opens_no_gap()
    {
        Assert.False(DropGeometry.OpensGap(new DropSpot(1, false), dragged: 1));
        Assert.Equal(new double[] { 0, 0, 0 }, DropGeometry.Shifts(3, dragged: 1, new DropSpot(1, false), 40));
    }

    [Fact]
    public void On_the_source_bar_the_dragged_slot_slides_with_the_slots_after_the_gap()
    {
        Assert.Equal(new double[] { 40, 40, 40 }, DropGeometry.Shifts(3, dragged: 1, new DropSpot(0, false), 40));
        Assert.Equal(new double[] { 0, 0, 40 }, DropGeometry.Shifts(3, dragged: 0, new DropSpot(1, false), 40));
    }

    [Fact]
    public void A_gap_after_the_last_slot_moves_no_slot()
    {
        Assert.True(DropGeometry.OpensGap(new DropSpot(2, false), dragged: 1)); // », + and Done still slide
        Assert.Equal(new double[] { 0, 0, 0 }, DropGeometry.Shifts(3, dragged: 1, new DropSpot(2, false), 40));
        Assert.Equal(new double[] { 0, 0 }, DropGeometry.Shifts(2, dragged: -1, new DropSpot(2, false), 40));
    }
}
