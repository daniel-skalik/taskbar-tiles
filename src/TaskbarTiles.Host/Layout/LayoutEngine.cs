namespace TaskbarTiles.Host.Layout;

/// <summary>How a slot is shown: at full width, icon only, or inside the » overflow popup.</summary>
public enum SlotMode { Full, Compact, Overflow }

/// <summary>One slot as the layout sees it. A stack is one item. Without CanCompact it is never shown compact.</summary>
public readonly record struct LayoutItem(double FullWidth, bool Pinned, bool CanCompact = true);

/// <summary>A mode per item, whether the » tile is shown, and the total width in DIP of everything shown.</summary>
public sealed record LayoutResult(IReadOnlyList<SlotMode> Modes, bool ShowOverflow, double Width)
{
    public int Shown => Modes.Count(m => m != SlotMode.Overflow);
}

/// <summary>
/// Fits slots into the free width: everything at full width if it fits; otherwise unpinned slots that can become
/// compact do so from the right end; then slots move into a » tile from the right end, unpinned ones first and pinned
/// ones last.
/// Pure: the bar runs it on every free-zone or slot change.
/// </summary>
public static class LayoutEngine
{
    public const double CompactWidth = 40;
    public const double OverflowWidth = 40;

    public static LayoutResult Compute(IReadOnlyList<LayoutItem> items, double available, double gap)
    {
        if (!double.IsFinite(available) || available < 0) available = 0;
        var modes = Enumerable.Repeat(SlotMode.Full, items.Count).ToArray();
        if (items.Count == 0) return new LayoutResult(modes, false, 0);

        bool Fits() => WidthOf(items, modes, gap) <= available;

        if (Fits()) return Result(items, modes, gap);

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Pinned || !items[i].CanCompact || items[i].FullWidth <= CompactWidth) continue;
            modes[i] = SlotMode.Compact;
            if (Fits()) return Result(items, modes, gap);
        }

        var order = Enumerable.Range(0, items.Count).Reverse().OrderBy(i => items[i].Pinned ? 1 : 0); // stable: right to left within each group
        foreach (var i in order)
        {
            modes[i] = SlotMode.Overflow;
            if (Fits()) return Result(items, modes, gap);
        }

        return new LayoutResult(modes, false, 0); // not even the » tile fits
    }

    static LayoutResult Result(IReadOnlyList<LayoutItem> items, SlotMode[] modes, double gap) =>
        new(modes, modes.Contains(SlotMode.Overflow), WidthOf(items, modes, gap));

    static double WidthOf(IReadOnlyList<LayoutItem> items, SlotMode[] modes, double gap)
    {
        double width = 0;
        var shown = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (modes[i] == SlotMode.Overflow) continue;
            width += modes[i] == SlotMode.Compact ? CompactWidth : items[i].FullWidth;
            shown++;
        }
        if (modes.Contains(SlotMode.Overflow))
        {
            width += OverflowWidth;
            shown++;
        }
        return shown == 0 ? 0 : width + gap * (shown - 1);
    }
}
