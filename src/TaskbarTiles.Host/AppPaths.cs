using System.IO;

namespace TaskbarTiles.Host;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarTiles");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>One folder per tile kind (tile.json, scripts, DLLs); the folder name is the kind's id.</summary>
    public static string TilesDir => Path.Combine(DataDir, "tiles");

    /// <summary>
    /// Connectors built into the app: only the script connector (plugins\TaskbarTiles.Plugins.Script\). Every other
    /// connector DLL lives in a kind folder; %LOCALAPPDATA%\TaskbarTiles\plugins is no longer scanned.
    /// </summary>
    public static string PluginsDir => Path.Combine(AppContext.BaseDirectory, "plugins");
}
