using System.IO;
using System.Text.Json.Nodes;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class ConfigStoreSaveTests
{
    const string Original = """
    {
      // my notes
      "tiles": { "a": { "kind": "x" }, "b": { "kind": "x" } },
      "displays": { "primary": { "slots": ["a"] } }
    }
    """;

    static Action<JsonObject> PlaceBoth =>
        root => ConfigJson.SetDisplay(root, "primary", new DisplayEntry(new([new TileSlot("a", false), new TileSlot("b", false)]), DisplayLayout.Empty));

    [Fact]
    public void Save_rewrites_the_file_and_updates_current()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        var error = store.Save(PlaceBoth);

        Assert.Null(error);
        Assert.Equal(1, changed);
        Assert.Equal(2, store.Current.Displays["primary"].Left.Slots.Count);
        var written = File.ReadAllText(dir.File("config.json"));
        Assert.DoesNotContain("my notes", written); // comments are lost: documented
        Assert.Equal(2, ConfigParser.Parse(written).Config!.Displays["primary"].Left.Slots.Count);
        Assert.False(File.Exists(dir.File("config.json.tmp")));
    }

    [Fact]
    public void Save_keeps_the_previous_file_as_bak_each_time()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();

        Assert.Null(store.Save(PlaceBoth));
        Assert.Equal(Original, File.ReadAllText(store.BackupPath));

        var afterFirst = File.ReadAllText(dir.File("config.json"));
        Assert.Null(store.Save(root => ConfigJson.SetDisplay(root, "primary", DisplayEntry.Empty)));
        Assert.Equal(afterFirst, File.ReadAllText(store.BackupPath));
    }

    [Fact]
    public void Save_refuses_a_file_that_does_not_parse_and_leaves_it_alone()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        File.WriteAllText(dir.File("config.json"), "{ \"tiles\": ");

        var error = store.Save(PlaceBoth);

        Assert.NotNull(error);
        Assert.Contains("nothing was saved", error!.Message);
        Assert.NotNull(error.Line);
        Assert.Equal("{ \"tiles\": ", File.ReadAllText(dir.File("config.json")));
        Assert.False(File.Exists(store.BackupPath));
        Assert.Single(store.Current.Displays["primary"].Left.Slots); // last good config stays
    }

    [Fact]
    public void Save_refuses_an_edit_that_would_make_the_config_invalid()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();

        var error = store.Save(root => ConfigJson.SetDisplay(root, "primary", new DisplayEntry(new([new TileSlot("ghost", false)]), DisplayLayout.Empty)));

        Assert.NotNull(error);
        Assert.Contains("ghost", error!.Message);
        Assert.Equal(Original, File.ReadAllText(dir.File("config.json")));
    }

    [Fact]
    public void Save_reports_an_edit_that_throws_without_writing()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        var log = new CollectingLog();
        using var store = new ConfigStore(dir.File("config.json"), log);
        store.Load();

        var error = store.Save(_ => throw new InvalidOperationException("bad edit"));

        Assert.NotNull(error);
        Assert.Contains("bad edit", error!.Message);
        Assert.Equal(Original, File.ReadAllText(dir.File("config.json")));
        Assert.Contains(log.Snapshot(), m => m.StartsWith("ERROR") && m.Contains("Saving config.json failed"));
    }

    [Fact]
    public void Save_reports_a_duplicate_key_and_writes_nothing()
    {
        using var dir = new TempDir();
        var duplicated = Original.Replace("\"primary\": { \"slots\": [\"a\"] }", "\"primary\": { \"slots\": [\"a\"] }, \"primary\": { \"slots\": [] }");
        File.WriteAllText(dir.File("config.json"), duplicated);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        Assert.Null(store.LastError); // the parser accepts duplicate keys, which is why Save has to cope

        var error = store.Save(PlaceBoth);

        Assert.NotNull(error);
        Assert.Contains("duplicate key", error!.Message);
        Assert.Contains("remove it, then try again", error.Message);
        Assert.Equal(duplicated, File.ReadAllText(dir.File("config.json")));
        Assert.False(File.Exists(store.BackupPath));
        Assert.False(File.Exists(dir.File("config.json.tmp")));
    }

    [Fact]
    public void A_failed_replace_leaves_no_tmp_file_and_the_config_untouched()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        Directory.CreateDirectory(store.BackupPath); // a folder where the backup file goes makes File.Replace fail

        var error = store.Save(PlaceBoth);

        Assert.NotNull(error);
        Assert.False(File.Exists(dir.File("config.json.tmp")));
        Assert.Equal(Original, File.ReadAllText(dir.File("config.json")));
    }

    [Fact]
    public void Save_builds_on_the_file_on_disk_so_a_hand_edit_not_yet_reloaded_is_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        File.WriteAllText(dir.File("config.json"), Original.Replace("\"b\": { \"kind\": \"x\" }", "\"b\": { \"kind\": \"x\" }, \"c\": { \"kind\": \"y\" }"));

        Assert.Null(store.Save(PlaceBoth));

        Assert.Equal("y", store.Current.Tiles["c"].Kind);
    }

    [Fact]
    public async Task Our_own_save_does_not_come_back_as_a_reload()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        var log = new CollectingLog();
        using var store = new ConfigStore(dir.File("config.json"), log);
        store.Load();
        store.StartWatching();
        var changed = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changed);

        Assert.Null(store.Save(PlaceBoth));
        await Task.Delay(1000); // well past the 300 ms debounce

        Assert.Equal(1, Volatile.Read(ref changed));
        Assert.DoesNotContain(log.Snapshot(), m => m.Contains("Reloaded config.json"));
    }

    [Fact]
    public async Task A_hand_edit_after_a_save_is_still_picked_up()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.json"), Original);
        using var store = new ConfigStore(dir.File("config.json"), new CollectingLog());
        store.Load();
        store.StartWatching();
        Assert.Null(store.Save(PlaceBoth));
        await Task.Delay(600);
        var reloaded = new TaskCompletionSource();
        store.Changed += (_, _) => reloaded.TrySetResult();

        File.WriteAllText(dir.File("config.json"), """{ "tiles": { "z": { "kind": "x" } } }""");

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.Current.Tiles.ContainsKey("z"));
    }
}
