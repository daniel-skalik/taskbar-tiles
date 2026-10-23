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
        return $"{what}. {consequence} Click to show config.json in Explorer.";
    }

    public const string SaveFailedTitle = "Layout not saved";

    /// <summary>Why an edit-mode or menu change was not written; config.json is left as it was.</summary>
    public static string SaveFailedBody(ConfigError error) =>
        $"{error.ToString().TrimEnd().TrimEnd('.')}. config.json is unchanged. Click to show config.json in Explorer.";
}
