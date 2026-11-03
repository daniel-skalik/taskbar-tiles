using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.Layout;

/// <summary>
/// One slot as a bar shows it: a single tile (one member, <see cref="Rotate"/> null) or a stack.
/// <see cref="Index"/> is the slot's position in the display's config slots, which layout edits refer to;
/// member i of a stack is the stack's i-th tile id. <see cref="Width"/> is the slot's width with resized tiles' own
/// widths (null: from its members' kinds); <see cref="Resizable"/> lets edit mode offer the resize grip, which stays
/// within <see cref="MinWidth"/> and <see cref="MaxWidth"/>.
/// </summary>
public sealed record BarSlot(int Index, IReadOnlyList<TileRuntime> Members, bool Pinned, TimeSpan? Rotate, double? Width = null,
    bool Resizable = false, double MinWidth = TileJson.MinWidth, double MaxWidth = TileJson.MaxWidth)
{
    public bool IsStack => Rotate is not null;

    /// <summary>A stack is as wide as its widest member, so it never changes width while it rotates.</summary>
    public double FullWidth => Width ?? (Members.Count == 0 ? 0 : Members.Max(m => m.Width));

    /// <summary>A stack shrinks to an icon only when every member may.</summary>
    public bool CanCompact => Members.All(m => m.CanCompact);

    /// <summary>Same tiles, pin and rotation, wherever the slot sits and however wide it is.</summary>
    public bool SameContent(BarSlot other) =>
        Pinned == other.Pinned && Rotate == other.Rotate && Members.SequenceEqual(other.Members);

    // Records compare lists by reference; compare members so equal slots are equal.
    public bool Equals(BarSlot? other) =>
        other is not null && Index == other.Index && SameContent(other) && Width == other.Width && Resizable == other.Resizable
        && MinWidth == other.MinWidth && MaxWidth == other.MaxWidth;

    public override int GetHashCode() => HashCode.Combine(Index, Members.Count, Pinned, Rotate);
}
