using System.Text.Json;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Host.Layout;

/// <summary>
/// One choice in edit mode's "+" gallery. <see cref="NewTile"/> null: place the existing tile <see cref="TileId"/>.
/// Otherwise: add a new tile with that definition under a free id based on <see cref="TileId"/>.
/// </summary>
public sealed record GalleryItem(string Title, string Subtitle, string TileId, TileConfig? NewTile);

/// <summary>A ready-made script tile for one of the sample scripts shipped in scripts\.</summary>
public sealed record ScriptTemplate(string Id, string Title, string Script, string SettingsJson);

public static class ScriptTemplates
{
    /// <summary>The samples in the repo's scripts folder, copied to %LOCALAPPDATA%\TaskbarTiles\scripts on first run.</summary>
    public static IReadOnlyList<ScriptTemplate> Samples { get; } =
    [
        new("uptime", "Clock and uptime", "scripts/uptime.ps1", """
            { "hooks": {
                "interval": { "every": "30s", "run": "scripts/uptime.ps1" },
                "flyout":   { "run": "scripts/uptime.ps1" } } }
            """),
        new("ping", "Ping a host", "scripts/ping.ps1", """
            { "host": "1.1.1.1", "hooks": {
                "interval":    { "every": "15s", "run": "scripts/ping.ps1" },
                "flyout":      { "run": "scripts/ping.ps1" },
                "action:ping": { "run": "scripts/ping.ps1" } } }
            """),
    ];
}

public static class TileGallery
{
    /// <summary>The script connector needs hooks, so it is offered through the script templates instead.</summary>
    public const string ScriptConnectorId = "script";

    /// <summary>
    /// Existing tiles this display does not place yet (in config order), then a new tile of each loaded connector
    /// (by id), then the script templates.
    /// </summary>
    public static IReadOnlyList<GalleryItem> Build(HostConfig config, DisplayLayout layout,
        IEnumerable<string> connectorIds, IReadOnlyList<ScriptTemplate> templates)
    {
        var placed = DisplayResolver.PlacedTileIds(layout).ToHashSet(StringComparer.Ordinal);
        var items = new List<GalleryItem>();

        foreach (var (id, tile) in config.Tiles)
            if (!placed.Contains(id))
                items.Add(new GalleryItem(id, $"Your tile · {tile.Connector}", id, null));

        foreach (var connector in connectorIds.Where(c => c != ScriptConnectorId).Order(StringComparer.Ordinal))
            items.Add(new GalleryItem(connector, "New tile from the plugin", connector, new TileConfig(connector, Json("{}"))));

        foreach (var t in templates)
            items.Add(new GalleryItem(t.Title, $"Script · {t.Script}", t.Id, new TileConfig(ScriptConnectorId, Json(t.SettingsJson))));

        return items;
    }

    /// <summary><paramref name="baseId"/> if it is free, else baseId-2, baseId-3, …</summary>
    public static string UniqueId(string baseId, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(baseId)) return baseId;
        for (var n = 2; ; n++)
            if (!used.Contains($"{baseId}-{n}")) return $"{baseId}-{n}";
    }

    static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
