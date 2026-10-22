using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Host.UI;

/// <summary>Text of the notification shown when config.json cannot be applied.</summary>
public static class ConfigNotice
{
    public const string Title = "config.json was not applied";

    public static string Body(ConfigError error, bool atStartup)
    {
        var what = error.ToString().TrimEnd().TrimEnd('.');
        var consequence = atStartup
            ? "No tiles are shown until it is fixed."
            : "The last good config stays active.";
        return $"{what}. {consequence} Click to open config.json.";
    }
}
