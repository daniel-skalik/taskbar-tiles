using System.Text.Json;

namespace TaskbarTiles.Host.Config;

public sealed record HostConfig(
    IReadOnlyDictionary<string, TileConfig> Tiles,
    IReadOnlyDictionary<string, DisplayEntry> Displays)
{
    public static HostConfig Empty { get; } = new(new Dictionary<string, TileConfig>(), new Dictionary<string, DisplayEntry>());
}

/// <summary>
/// A tile instance: the kind (a folder in tiles\) and the settings that replace the kind's own, key by key.
/// Settings is a cloned element, safe to keep after parsing.
/// </summary>
public sealed record TileConfig(string Kind, JsonElement Settings);

/// <summary>
/// A bar on a taskbar: left of Start (centered icons), or between the app icons and the tray, against the tray.
/// </summary>
public enum BarSide { Left, Right }

/// <summary>One bar's slots, left to right.</summary>
public sealed record DisplayLayout(IReadOnlyList<Slot> Slots)
{
    public static DisplayLayout Empty { get; } = new([]);

    // Records compare lists by reference; compare items so layouts with equal content are equal.
    public bool Equals(DisplayLayout? other) => other is not null && Slots.SequenceEqual(other.Slots);

    public override int GetHashCode() => Slots.Count;
}

/// <summary>A display entry in config.json: its "left" and "right" bars.</summary>
public sealed record DisplayEntry(DisplayLayout Left, DisplayLayout Right)
{
    public static DisplayEntry Empty { get; } = new(DisplayLayout.Empty, DisplayLayout.Empty);

    public DisplayLayout this[BarSide side] => side == BarSide.Right ? Right : Left;

    public DisplayEntry With(BarSide side, DisplayLayout layout) =>
        side == BarSide.Right ? this with { Right = layout } : this with { Left = layout };
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
