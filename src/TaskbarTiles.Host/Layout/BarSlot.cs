using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.Layout;

/// <summary>
/// One slot as a bar shows it: a single tile (one member, <see cref="Rotate"/> null) or a stack.
/// <see cref="Index"/> is the slot's position in the display's config slots, which layout edits refer to;
/// member i of a stack is the stack's i-th tile id.
/// </summary>
public sealed record BarSlot(int Index, IReadOnlyList<TileRuntime> Members, bool Pinned, TimeSpan? Rotate)
{
    public bool IsStack => Rotate is not null;

    /// <summary>A stack is as wide as its widest member, so it never changes width while it rotates.</summary>
    public double FullWidth => Members.Count == 0 ? 0 : Members.Max(m => m.Width);

    /// <summary>Same tiles, pin and rotation, wherever the slot sits.</summary>
    public bool SameContent(BarSlot other) =>
        Pinned == other.Pinned && Rotate == other.Rotate && Members.SequenceEqual(other.Members);

    // Records compare lists by reference; compare members so equal slots are equal.
    public bool Equals(BarSlot? other) => other is not null && Index == other.Index && SameContent(other);

    public override int GetHashCode() => HashCode.Combine(Index, Members.Count, Pinned, Rotate);
}
