namespace TaskbarTiles.Host.Config;

/// <summary>Where a dragged slot lands relative to the slot under the pointer.</summary>
public enum DropPosition { Before, Into, After }

/// <summary>
/// Pure edits of one display's slots, used by edit mode and the tile menu. Indexes are positions in
/// <see cref="DisplayLayout.Slots"/>. An index that does not fit leaves the layout unchanged, so a stale request is harmless.
/// A stack an edit leaves with one member becomes a plain tile; a stack it leaves empty disappears.
/// </summary>
public static class LayoutEdits
{
    /// <summary>The middle 40 % of a slot means "into"; the outer parts mean before or after it.</summary>
    public static DropPosition DropPositionAt(double relativeX) =>
        relativeX <= 0.3 ? DropPosition.Before : relativeX >= 0.7 ? DropPosition.After : DropPosition.Into;

    /// <summary>Drag and drop: Before/After reorders, Into stacks the dragged slot with the target.</summary>
    public static DisplayLayout Drop(DisplayLayout layout, int from, int target, DropPosition position)
    {
        if (!Valid(layout, from) || !Valid(layout, target) || from == target) return layout;
        if (position == DropPosition.Into) return StackInto(layout, from, target);

        var slots = layout.Slots.ToList();
        var moving = slots[from];
        slots.RemoveAt(from);
        var at = target > from ? target - 1 : target;
        slots.Insert(position == DropPosition.After ? at + 1 : at, moving);
        return new DisplayLayout(slots);
    }

    /// <summary>
    /// Puts the tiles of slot <paramref name="from"/> into slot <paramref name="target"/>: appended to a target stack,
    /// or a new stack [target, dragged…] where the target tile was. Tiles already in the target stack are not added twice.
    /// </summary>
    public static DisplayLayout StackInto(DisplayLayout layout, int from, int target)
    {
        if (!Valid(layout, from) || !Valid(layout, target) || from == target) return layout;
        var dragged = layout.Slots[from];
        var draggedIds = Members(dragged);

        Slot merged = layout.Slots[target] switch
        {
            StackSlot s => new StackSlot(s.TileIds.Concat(draggedIds.Where(id => !s.TileIds.Contains(id))).ToList(), s.Rotate),
            TileSlot t => new StackSlot(new[] { t.TileId }.Concat(draggedIds.Where(id => id != t.TileId)).ToList(),
                dragged is StackSlot ds ? ds.Rotate : ConfigParser.DefaultRotate),
            var other => other,
        };

        var slots = layout.Slots.ToList();
        slots[target] = Collapse(merged)!;
        slots.RemoveAt(from);
        return new DisplayLayout(slots);
    }

    /// <summary>"Remove from stack": the member becomes a plain tile right after the stack.</summary>
    public static DisplayLayout RemoveFromStack(DisplayLayout layout, int index, int member)
    {
        if (!Valid(layout, index) || layout.Slots[index] is not StackSlot s || member < 0 || member >= s.TileIds.Count) return layout;
        var slots = layout.Slots.ToList();
        var rest = Collapse(new StackSlot(s.TileIds.Where((_, i) => i != member).ToList(), s.Rotate));
        slots.Insert(index + 1, new TileSlot(s.TileIds[member], false));
        if (rest is null) slots.RemoveAt(index);
        else slots[index] = rest;
        return new DisplayLayout(slots);
    }

    /// <summary>Removes the slot, or with <paramref name="member"/> one member of a stack.</summary>
    public static DisplayLayout Remove(DisplayLayout layout, int index, int? member = null)
    {
        if (!Valid(layout, index)) return layout;
        var slots = layout.Slots.ToList();
        if (member is { } m && slots[index] is StackSlot s)
        {
            if (m < 0 || m >= s.TileIds.Count) return layout;
            var rest = Collapse(new StackSlot(s.TileIds.Where((_, i) => i != m).ToList(), s.Rotate));
            if (rest is null) slots.RemoveAt(index);
            else slots[index] = rest;
        }
        else
        {
            slots.RemoveAt(index);
        }
        return new DisplayLayout(slots);
    }

    /// <summary>
    /// A slot dragged here from another display. Tiles this layout already places are left out (a stack keeps the rest
    /// and becomes a plain tile with one left; with none left nothing changes). With a <paramref name="target"/> that
    /// fits it lands before, into or after that slot like <see cref="Drop"/>; otherwise it is appended. A plain tile keeps
    /// its pin, a stack its rotate.
    /// </summary>
    public static DisplayLayout DropIncoming(DisplayLayout layout, Slot slot, int? target, DropPosition position)
    {
        var placed = DisplayResolver.PlacedTileIds(layout).ToHashSet(StringComparer.Ordinal);
        Slot? incoming = slot switch
        {
            TileSlot t => placed.Contains(t.TileId) ? null : t,
            StackSlot s => Collapse(new StackSlot(s.TileIds.Where(id => !placed.Contains(id)).ToList(), s.Rotate)),
            _ => null,
        };
        if (incoming is null) return layout;

        var appended = new DisplayLayout(layout.Slots.Append(incoming).ToList());
        return target is { } t2 && Valid(layout, t2) ? Drop(appended, appended.Slots.Count - 1, t2, position) : appended;
    }

    /// <summary>Adds a plain tile at the end.</summary>
    public static DisplayLayout Append(DisplayLayout layout, string tileId) =>
        new(layout.Slots.Append(new TileSlot(tileId, false)).ToList());

    /// <summary>Tile ids <paramref name="before"/> places that <paramref name="after"/> no longer places, in order.</summary>
    public static IReadOnlyList<string> RemovedTileIds(DisplayLayout before, DisplayLayout after) =>
        DisplayResolver.PlacedTileIds(before).Except(DisplayResolver.PlacedTileIds(after), StringComparer.Ordinal).ToList();

    /// <summary>True when both layouts hold equal slots in the same order.</summary>
    public static bool SameLayout(DisplayLayout a, DisplayLayout b) => a.Side == b.Side && a.Slots.SequenceEqual(b.Slots);

    static bool Valid(DisplayLayout layout, int index) => index >= 0 && index < layout.Slots.Count;

    static IReadOnlyList<string> Members(Slot slot) => slot switch
    {
        TileSlot t => [t.TileId],
        StackSlot s => s.TileIds,
        _ => [],
    };

    static Slot? Collapse(Slot slot) => slot switch
    {
        StackSlot { TileIds.Count: 0 } => null,
        StackSlot { TileIds.Count: 1 } s => new TileSlot(s.TileIds[0], false),
        _ => slot,
    };
}
