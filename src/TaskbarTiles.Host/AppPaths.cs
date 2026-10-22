using System.IO;

namespace TaskbarTiles.Host;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarTiles");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Built-in plugins next to the exe first, then the user's plugins.</summary>
    public static IReadOnlyList<string> PluginRoots =>
        [Path.Combine(AppContext.BaseDirectory, "plugins"), Path.Combine(DataDir, "plugins")];
}
