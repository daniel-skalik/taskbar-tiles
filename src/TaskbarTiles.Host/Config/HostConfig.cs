using System.Text.Json;

namespace TaskbarTiles.Host.Config;

public sealed record HostConfig(
    IReadOnlyDictionary<string, TileConfig> Tiles,
    IReadOnlyDictionary<string, DisplayLayout> Displays)
{
    public static HostConfig Empty { get; } = new(new Dictionary<string, TileConfig>(), new Dictionary<string, DisplayLayout>());
}

/// <summary>
/// A tile instance: the kind (a folder in tiles\) and the settings that replace the kind's own, key by key.
/// Settings is a cloned element, safe to keep after parsing.
/// </summary>
public sealed record TileConfig(string Kind, JsonElement Settings);

/// <summary>Where a display's tiles sit: left of Start (centered icons), or between the icons and the tray.</summary>
public enum BarSide { Left, Right }

public sealed record DisplayLayout(IReadOnlyList<Slot> Slots, BarSide Side = BarSide.Left)
{
    public static DisplayLayout Empty { get; } = new([]);
}

public abstract record Slot;

public sealed record TileSlot(string TileId, bool Pin) : Slot;

public sealed record StackSlot(IReadOnlyList<string> TileIds, TimeSpan Rotate) : Slot
{
    // Records compare lists by reference; compare items so layouts with equal content are equal.
    public bool Equals(StackSlot? other) =>
        other is not null && Rotate == other.Rotate && TileIds.SequenceEqual(other.TileIds);

    public override int GetHashCode() => HashCode.Combine(Rotate, TileIds.Count);
}
