using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Host.Config;

/// <summary>
/// The check of config.json that needs the kind table: every tile names a folder in tiles\ that has a tile.json.
/// A broken kind passes; only mistakes in config.json reject it.
/// </summary>
public static class KindValidation
{
    /// <summary>The first tile (in config order) whose kind has no folder with tile.json, or null when all have one.</summary>
    public static ConfigError? Check(HostConfig config, KindTable kinds)
    {
        foreach (var (id, tile) in config.Tiles)
        {
            if (kinds.Find(tile.Kind) is not null) continue;
            return kinds.FoldersWithoutTileJson.Contains(tile.Kind)
                ? new ConfigError($@"Tile '{id}' uses kind '{tile.Kind}', but tiles\{tile.Kind} has no tile.json.")
                : new ConfigError($@"Tile '{id}' uses kind '{tile.Kind}', but there is no tiles\{tile.Kind} folder.");
        }
        return null;
    }
}
