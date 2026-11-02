using System.Text.Json;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Host.Layout;

/// <summary>
/// One choice in the "+" / stack picker. <see cref="NewTile"/> null: place the existing tile <see cref="TileId"/>.
/// Otherwise: add an instance of the kind <c>NewTile.Kind</c> under a free id based on <see cref="TileId"/>.
/// <see cref="Group"/> is the folder of the picker it is shown in ("" for the top level), a path such as "Fun/Games".
/// </summary>
public sealed record GalleryItem(string Title, string Subtitle, string TileId, TileConfig? NewTile, string Group = "");

/// <summary>A folder in the picker: its own name, its full path, and how many choices it holds (subfolders included).</summary>
public sealed record GalleryFolder(string Name, string Path, int Count);

public static class TileGallery
{
    /// <summary>
    /// Existing tiles this display does not place yet (in config order), then one new tile per kind in tiles\, by name
    /// (broken kinds too: titled with their folder name, the error as subtitle). A new tile is just { "kind": id }.
    /// Existing tiles are always at the top level; new tiles go in their kind's group.
    /// </summary>
    public static IReadOnlyList<GalleryItem> Build(HostConfig config, DisplayEntry display, KindTable kinds)
    {
        var placed = DisplayResolver.PlacedTileIds(display).ToHashSet(StringComparer.Ordinal);
        var items = new List<GalleryItem>();

        foreach (var (id, tile) in config.Tiles)
            if (!placed.Contains(id))
                items.Add(new GalleryItem(id, $"Your tile · {kinds.Find(tile.Kind)?.Name ?? tile.Kind}", id, null));

        foreach (var kind in kinds.Kinds.Values
                     .OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(k => k.Id, StringComparer.OrdinalIgnoreCase))
            items.Add(new GalleryItem(kind.Name, kind.Description, kind.Id, new TileConfig(kind.Id, EmptySettings()), kind.Group));

        return items;
    }

    /// <summary>
    /// What the picker shows in the folder <paramref name="path"/> ("" for the top level): its subfolders by name, then
    /// the choices directly in it, in their order.
    /// </summary>
    public static (IReadOnlyList<GalleryFolder> Folders, IReadOnlyList<GalleryItem> Items) Open(IReadOnlyList<GalleryItem> items, string path)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        var folders = items
            .Where(i => i.Group.Length > prefix.Length && i.Group.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.Group[prefix.Length..].Split('/')[0], StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var name = g.First().Group[prefix.Length..].Split('/')[0]; // spelled as the first kind in it spells it
                return new GalleryFolder(name, prefix + name, g.Count());
            })
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var here = items.Where(i => string.Equals(i.Group, path, StringComparison.OrdinalIgnoreCase)).ToList();
        return (folders, here);
    }

    /// <summary>The folder above <paramref name="path"/>: "Fun" for "Fun/Games", "" for "Fun".</summary>
    public static string Parent(string path) => path.LastIndexOf('/') is var i and >= 0 ? path[..i] : "";

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
