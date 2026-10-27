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
        Assert.Equal("media", store.Current.Tiles["spotify"].Kind);
        Assert.Equal("uptime", store.Current.Tiles["uptime"].Kind);
        Assert.Null(store.LastError);
    }

    [Fact]
    public void Load_and_save_run_the_validator()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "a": { "kind": "ping" } }, "displays": { "primary": { "slots": [] } } }""");
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog(),
            cfg => cfg.Tiles.ContainsKey("bad") ? new ConfigError("no bad tiles") : null);
        store.Load();

        var error = store.Save(root => ConfigJson.SetTile(root, "bad", new TileConfig("ping", store.Current.Tiles["a"].Settings)));

        Assert.Null(store.LastError);
        Assert.Equal("The change was not saved: no bad tiles", error!.Message);
        Assert.DoesNotContain("bad", File.ReadAllText(dir.File("config.json")));
    }

    [Fact]
    public void Load_rejects_a_config_the_validator_refuses()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "a": { "kind": "ghost" } } }""");
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog(), _ => new ConfigError("missing kind"));

        store.Load();

        Assert.Equal("missing kind", store.LastError!.Message);
        Assert.Empty(store.Current.Tiles);
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
        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "x": { "kind": "y" } } }""");
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

        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "x": { "kind": "y" } } }""");

        await changed.Task.WaitAsync(Wait);
        Assert.Equal("y", store.Current.Tiles["x"].Kind);
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
