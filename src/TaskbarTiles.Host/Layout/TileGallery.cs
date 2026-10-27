using System.Text.Json;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Host.Layout;

/// <summary>
/// One choice in the "+" / stack picker. <see cref="NewTile"/> null: place the existing tile <see cref="TileId"/>.
/// Otherwise: add an instance of the kind <c>NewTile.Kind</c> under a free id based on <see cref="TileId"/>.
/// </summary>
public sealed record GalleryItem(string Title, string Subtitle, string TileId, TileConfig? NewTile);

public static class TileGallery
{
    /// <summary>
    /// Existing tiles this display does not place yet (in config order), then one new tile per kind in tiles\, by name
    /// (broken kinds too: titled with their folder name, the error as subtitle). A new tile is just { "kind": id }.
    /// </summary>
    public static IReadOnlyList<GalleryItem> Build(HostConfig config, DisplayLayout layout, KindTable kinds)
    {
        var placed = DisplayResolver.PlacedTileIds(layout).ToHashSet(StringComparer.Ordinal);
        var items = new List<GalleryItem>();

        foreach (var (id, tile) in config.Tiles)
            if (!placed.Contains(id))
                items.Add(new GalleryItem(id, $"Your tile · {kinds.Find(tile.Kind)?.Name ?? tile.Kind}", id, null));

        foreach (var kind in kinds.Kinds.Values
                     .OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(k => k.Id, StringComparer.OrdinalIgnoreCase))
            items.Add(new GalleryItem(kind.Name, kind.Description, kind.Id, new TileConfig(kind.Id, EmptySettings())));

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

    static JsonElement EmptySettings()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
