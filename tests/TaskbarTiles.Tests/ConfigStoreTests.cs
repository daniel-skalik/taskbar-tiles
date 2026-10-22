using System.IO;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class ConfigStoreTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void Load_creates_default_file_when_missing()
    {
        using var dir = new TempDir();
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());

        store.Load();

        Assert.True(File.Exists(dir.File("config.json")));
        Assert.Equal("media", store.Current.Tiles["spotify"].Connector);
        Assert.Equal("script", store.Current.Tiles["uptime"].Connector);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Load_of_broken_file_gives_empty_config_and_error()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), "{ broken");
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());

        store.Load();

        Assert.Empty(store.Current.Tiles);
        Assert.NotNull(store.LastError);
        Assert.Equal("{ broken", File.ReadAllText(dir.File("config.json"))); // never overwritten
    }

    [Fact]
    public void Load_when_file_is_locked_gives_empty_config_and_error()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "x": { "connector": "y" } } }""");
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());

        using (new FileStream(dir.File("config.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store.Load();

            Assert.Empty(store.Current.Tiles);
            Assert.NotNull(store.LastError);
            Assert.Contains("Could not read", store.LastError!.Message);
        }
    }

    [Fact]
    public async Task Watch_reloads_valid_edits()
    {
        using var dir = new TempDir();
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        store.StartWatching();
        var changed = new TaskCompletionSource();
        store.Changed += (_, _) => changed.TrySetResult();

        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "x": { "connector": "y" } } }""");

        await changed.Task.WaitAsync(Wait);
        Assert.Equal("y", store.Current.Tiles["x"].Connector);
    }

    [Fact]
    public async Task Watch_keeps_last_good_config_on_invalid_edit()
    {
        using var dir = new TempDir();
        var log = new CollectingLog();
        using var store = new ConfigStore(dir.File("config.json"), log);
        store.Load();
        store.StartWatching();
        var failed = new TaskCompletionSource<ConfigError>();
        store.ReloadFailed += (_, e) => failed.TrySetResult(e);

        File.WriteAllText(dir.File("config.json"), "{ \"tiles\": ");

        var error = await failed.Task.WaitAsync(Wait);
        Assert.NotNull(error.Line);
        Assert.True(store.Current.Tiles.ContainsKey("spotify"));
        Assert.Contains(log.Messages, m => m.StartsWith("WARN ") && m.Contains("rejected"));
    }
}
