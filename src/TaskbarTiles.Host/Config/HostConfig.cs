using System.Text.Json;

namespace TaskbarTiles.Host.Config;

public sealed record HostConfig(
    IReadOnlyDictionary<string, TileConfig> Tiles,
    IReadOnlyDictionary<string, DisplayLayout> Displays)
{
    public static HostConfig Empty { get; } = new(new Dictionary<string, TileConfig>(), new Dictionary<string, DisplayLayout>());
}

/// <summary>Settings is a cloned element, safe to keep after parsing.</summary>
public sealed record TileConfig(string Connector, JsonElement Settings);

public sealed record DisplayLayout(IReadOnlyList<Slot> Slots)
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
