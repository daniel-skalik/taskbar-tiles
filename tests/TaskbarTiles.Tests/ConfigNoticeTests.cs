using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class ConfigNoticeTests
{
    [Fact]
    public void Rejected_edit_names_line_and_column_and_keeps_the_last_config()
    {
        var body = ConfigNotice.Body(new ConfigError("Invalid JSON: unexpected end", 3, 5), atStartup: false);

        Assert.Contains("line 3, column 5", body);
        Assert.Contains("The last good config stays active.", body);
        Assert.EndsWith("Click to show config.json in Explorer.", body);
    }

    [Fact]
    public void Startup_failure_says_no_tiles_are_shown()
    {
        var body = ConfigNotice.Body(new ConfigError("Invalid JSON: bad", 1, 2), atStartup: true);

        Assert.Contains("No tiles are shown until it is fixed.", body);
        Assert.Contains("line 1, column 2", body);
    }

    [Fact]
    public void Message_ending_with_a_period_is_not_doubled()
    {
        var body = ConfigNotice.Body(new ConfigError("Tile 'a' needs a \"connector\" name."), atStartup: false);

        Assert.DoesNotContain("..", body);
        Assert.StartsWith("Tile 'a' needs a \"connector\" name. ", body);
    }

    [Fact]
    public void Dll_change_names_the_kind_folders_and_asks_for_a_restart()
    {
        var body = ConfigNotice.DllsChangedBody(["media", "meeting"]);

        Assert.Equal("Restart Taskbar Tiles", ConfigNotice.DllsChangedTitle);
        Assert.StartsWith(@"The DLLs in tiles\media, tiles\meeting changed while Taskbar Tiles was running.", body);
        Assert.EndsWith("Click to show the folder in Explorer.", body);
    }

    [Fact]
    public void Save_failure_says_the_file_is_unchanged()
    {
        var body = ConfigNotice.SaveFailedBody(new ConfigError("config.json has an error, so nothing was saved: Invalid JSON.", 4, 1));

        Assert.StartsWith("config.json has an error, so nothing was saved: Invalid JSON. (line 4, column 1). config.json is unchanged.", body);
        Assert.EndsWith("Click to show config.json in Explorer.", body);
    }
}
