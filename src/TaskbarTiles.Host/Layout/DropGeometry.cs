using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Host.Layout;

/// <summary>Where a slot sits on a bar: DIP from the left of the bar's tile row, and its width.</summary>
public readonly record struct SlotSpan(double Left, double Width);

/// <summary>
/// A drop spot among a bar's slots: into the slot at <see cref="Index"/>, or (Into false) the gap before the slot at
/// Index. Index equal to the slot count is the gap after the last slot.
/// </summary>
public readonly record struct DropSpot(int Index, bool Into);

/// <summary>Pure drag-and-drop geometry for a bar's slots, in the order they are shown.</summary>
public static class DropGeometry
{
    /// <summary>
    /// The spot under <paramref name="x"/>: the middle 40 % of a slot is "into"; its outer parts and the space between
    /// slots are the gap on that side; left of the first slot is gap 0, right of the last the gap after it.
    /// </summary>
    public static DropSpot SpotAt(IReadOnlyList<SlotSpan> slots, double x)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (x < s.Left) return new DropSpot(i, false);
            if (x >= s.Left + s.Width) continue;
            return LayoutEdits.DropPositionAt((x - s.Left) / s.Width) switch
            {
                DropPosition.Into => new DropSpot(i, true),
                DropPosition.Before => new DropSpot(i, false),
                _ => new DropSpot(i + 1, false),
            };
        }
        return new DropSpot(slots.Count, false);
    }

    /// <summary>
    /// The layout drop for a spot, given the config indexes of the slots: into a slot, before the slot after a gap, after
    /// the last slot for the gap behind it, or (null, After) to append when the bar shows no slot.
    /// </summary>
    public static (int? Target, DropPosition Position) ToDrop(DropSpot spot, IReadOnlyList<int> slotIndexes)
    {
        if (spot.Index >= 0 && spot.Index < slotIndexes.Count)
            return (slotIndexes[spot.Index], spot.Into ? DropPosition.Into : DropPosition.Before);
        return slotIndexes.Count > 0 ? (slotIndexes[^1], DropPosition.After) : (null, DropPosition.After);
    }

    /// <summary>
    /// True when the spot opens a gap: it is not "into", and not the dragged slot's own place on its own bar.
    /// <paramref name="dragged"/> is the dragged slot's position among the bar's shown slots, or -1 on another bar.
    /// </summary>
    public static bool OpensGap(DropSpot? spot, int dragged) => spot is { Into: false } s && s.Index != dragged;

    /// <summary>
    /// How far each of a bar's <paramref name="count"/> shown slots slides right to open the gap: <paramref name="gap"/>
    /// for the slots after it, 0 for the ones before. Spot indexes skip the dragged slot (<paramref name="dragged"/>, or -1
    /// when the drag started on another bar); the dragged slot itself slides when the gap is left of it.
    /// </summary>
    public static IReadOnlyList<double> Shifts(int count, int dragged, DropSpot? spot, double gap)
    {
        var shifts = new double[count];
        if (!OpensGap(spot, dragged)) return shifts;
        var at = spot!.Value.Index;
        for (var j = 0; j < count; j++)
        {
            var position = dragged >= 0 && j > dragged ? j - 1 : j;
            if (position >= at) shifts[j] = gap;
        }
        return shifts;
    }
}
