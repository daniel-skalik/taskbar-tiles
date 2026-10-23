namespace TaskbarTiles.Host.Config;

public static class DisplayResolver
{
    public const string PrimaryKey = "primary";
    public const string FallbackKey = "*";

    /// <summary>Exact device id, then "primary" for the primary display, then "*".</summary>
    public static DisplayLayout Resolve(HostConfig config, string displayId, bool isPrimary)
    {
        if (config.Displays.TryGetValue(displayId, out var exact)) return exact;
        if (isPrimary && config.Displays.TryGetValue(PrimaryKey, out var primary)) return primary;
        return config.Displays.TryGetValue(FallbackKey, out var any) ? any : DisplayLayout.Empty;
    }

    /// <summary>
    /// The "displays" key edit mode writes for this display: the device id when it has its own entry;
    /// otherwise "primary" on the primary display (that key only ever applies to the primary display);
    /// otherwise the device id, so editing one display never changes the other displays that share "*".
    /// </summary>
    public static string EditKey(HostConfig config, string displayId, bool isPrimary) =>
        isPrimary && !config.Displays.ContainsKey(displayId) ? PrimaryKey : displayId;

    /// <summary>Every tile the layout places, stack members included, each once, in order of appearance.</summary>
    public static IReadOnlyList<string> PlacedTileIds(DisplayLayout layout) =>
        layout.Slots
            .SelectMany(s => s switch
            {
                TileSlot t => [t.TileId],
                StackSlot st => st.TileIds,
                _ => (IReadOnlyList<string>)[],
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

}
