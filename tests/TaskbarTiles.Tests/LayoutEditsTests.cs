using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class LayoutEditsTests
{
    static readonly TimeSpan Five = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Eight = TimeSpan.FromSeconds(8);

    static TileSlot T(string id, bool pin = false) => new(id, pin);
    static StackSlot S(TimeSpan rotate, params string[] ids) => new(ids, rotate);
    static DisplayLayout L(params Slot[] slots) => new(slots);

    static void Same(DisplayLayout expected, DisplayLayout actual) =>
        Assert.True(LayoutEdits.SameSlots(expected, actual), $"expected [{string.Join(", ", expected.Slots)}] but got [{string.Join(", ", actual.Slots)}]");

    [Theory]
    [InlineData(0.0, DropPosition.Before)]
    [InlineData(0.3, DropPosition.Before)]
    [InlineData(0.31, DropPosition.Into)]
    [InlineData(0.5, DropPosition.Into)]
    [InlineData(0.69, DropPosition.Into)]
    [InlineData(0.7, DropPosition.After)]
    [InlineData(1.0, DropPosition.After)]
    public void Middle_of_a_slot_means_into(double relativeX, DropPosition expected) =>
        Assert.Equal(expected, LayoutEdits.DropPositionAt(relativeX));

    [Fact]
    public void Drop_after_a_later_slot_moves_right()
    {
        var result = LayoutEdits.Drop(L(T("a"), T("b"), T("c")), from: 0, target: 2, DropPosition.After);
        Same(L(T("b"), T("c"), T("a")), result);
    }

    [Fact]
    public void Drop_before_an_earlier_slot_moves_left_and_keeps_pins()
    {
        var result = LayoutEdits.Drop(L(T("a"), T("b"), T("c", pin: true)), from: 2, target: 0, DropPosition.Before);
        Same(L(T("c", pin: true), T("a"), T("b")), result);
    }

    [Fact]
    public void Drop_before_the_next_slot_changes_nothing()
    {
        var layout = L(T("a"), T("b"), T("c"));
        Same(layout, LayoutEdits.Drop(layout, from: 0, target: 1, DropPosition.Before));
    }

    [Fact]
    public void Drop_into_a_tile_makes_a_stack_where_the_target_was()
    {
        var result = LayoutEdits.Drop(L(T("a"), T("b", pin: true), T("c")), from: 2, target: 0, DropPosition.Into);
        Same(L(S(Five, "a", "c"), T("b", pin: true)), result);
    }

    [Fact]
    public void Drop_into_a_stack_appends_and_keeps_its_rotate()
    {
        var result = LayoutEdits.Drop(L(T("a"), S(Eight, "b", "c")), from: 0, target: 1, DropPosition.Into);
        Same(L(S(Eight, "b", "c", "a")), result);
    }

    [Fact]
    public void Drop_a_stack_into_a_tile_merges_and_keeps_the_dragged_rotate()
    {
        var result = LayoutEdits.Drop(L(T("a"), S(Eight, "b", "c")), from: 1, target: 0, DropPosition.Into);
        Same(L(S(Eight, "a", "b", "c")), result);
    }

    [Fact]
    public void Drop_into_a_stack_that_already_has_the_tile_only_removes_the_dragged_slot()
    {
        var result = LayoutEdits.Drop(L(S(Five, "a", "b"), T("a")), from: 1, target: 0, DropPosition.Into);
        Same(L(S(Five, "a", "b")), result);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 5)]
    [InlineData(1, 1)]
    public void Drop_with_bad_or_equal_indexes_changes_nothing(int from, int target)
    {
        var layout = L(T("a"), T("b"));
        Same(layout, LayoutEdits.Drop(layout, from, target, DropPosition.After));
    }

    [Fact]
    public void Drops_before_and_after_a_neighbor_move_a_stack_as_one_slot()
    {
        var layout = L(T("a"), S(Five, "b", "c"), T("d"));
        Same(L(S(Five, "b", "c"), T("a"), T("d")), LayoutEdits.Drop(layout, 1, 0, DropPosition.Before));
        Same(L(T("a"), T("d"), S(Five, "b", "c")), LayoutEdits.Drop(layout, 1, 2, DropPosition.After));
    }

    [Fact]
    public void Stacking_a_chosen_tile_into_the_clicked_one_keeps_the_clicked_place()
    {
        // "Create stack with ▸": the clicked tile (index 2) stays where it is and comes first.
        Same(L(T("b"), S(Five, "c", "a")), LayoutEdits.StackInto(L(T("a"), T("b"), T("c")), from: 0, target: 2));
        // "Add to stack ▸": the chosen tile goes to the end of the clicked stack, which keeps its rotate.
        Same(L(S(Eight, "s1", "s2", "x")), LayoutEdits.StackInto(L(S(Eight, "s1", "s2"), T("x")), from: 1, target: 0));
    }

    [Fact]
    public void Remove_from_stack_puts_the_member_right_after_the_stack()
    {
        var result = LayoutEdits.RemoveFromStack(L(S(Eight, "a", "b", "c"), T("d")), 0, 1);
        Same(L(S(Eight, "a", "c"), T("b"), T("d")), result);
    }

    [Fact]
    public void Remove_from_a_two_member_stack_leaves_two_plain_tiles()
    {
        var result = LayoutEdits.RemoveFromStack(L(S(Five, "a", "b")), 0, 0);
        Same(L(T("b"), T("a")), result);
    }

    [Fact]
    public void Remove_from_stack_on_a_plain_tile_changes_nothing()
    {
        var layout = L(T("a"), T("b"));
        Same(layout, LayoutEdits.RemoveFromStack(layout, 0, 0));
    }

    [Fact]
    public void Remove_drops_the_slot()
    {
        Same(L(T("b")), LayoutEdits.Remove(L(T("a"), T("b")), 0));
        Same(L(T("a")), LayoutEdits.Remove(L(T("a"), S(Five, "b", "c")), 1));
    }

    [Fact]
    public void Remove_with_a_member_drops_only_that_member_and_collapses_the_stack()
    {
        Same(L(S(Eight, "a", "c")), LayoutEdits.Remove(L(S(Eight, "a", "b", "c")), 0, member: 1));
        Same(L(T("b")), LayoutEdits.Remove(L(S(Five, "a", "b")), 0, member: 0));
        Same(L(T("x")), LayoutEdits.Remove(L(S(Five, "a"), T("x")), 0, member: 0));
    }

    [Fact]
    public void Removed_tile_ids_are_the_ones_the_edit_took_off_the_display()
    {
        var before = L(T("a"), S(Five, "b", "c"), T("c"));

        Assert.Equal(new[] { "a" }, LayoutEdits.RemovedTileIds(before, LayoutEdits.Remove(before, 0)));
        Assert.Empty(LayoutEdits.RemovedTileIds(before, LayoutEdits.Remove(before, 1, member: 1))); // c is still placed
        Assert.Equal(new[] { "b", "c" }, LayoutEdits.RemovedTileIds(before, L(T("a"))));
    }

    [Fact]
    public void Append_adds_a_plain_tile_at_the_end() =>
        Same(L(T("a"), T("b")), LayoutEdits.Append(L(T("a")), "b"));

    [Fact]
    public void Edits_never_change_the_input()
    {
        var layout = L(T("a"), T("b"), T("c"));
        LayoutEdits.Drop(layout, 0, 2, DropPosition.Into);
        LayoutEdits.Drop(layout, 0, 1, DropPosition.After);
        LayoutEdits.Remove(layout, 1);
        Same(L(T("a"), T("b"), T("c")), layout);
    }
}
