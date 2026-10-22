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

    /// <summary>Tiles shown in milestone 1: plain slots plus the first item of each stack.</summary>
    public static IReadOnlyList<string> VisibleTileIds(DisplayLayout layout) =>
        layout.Slots
            .Select(s => s switch
            {
                TileSlot t => t.TileId,
                StackSlot { TileIds.Count: > 0 } st => st.TileIds[0],
                _ => null,
            })
            .OfType<string>()
            .ToList();
}
