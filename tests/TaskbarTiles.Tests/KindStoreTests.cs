using System.IO;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class KindStoreTests
{
    static void WriteKind(string tiles, string id, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(tiles, id)).FullName;
        File.WriteAllText(Path.Combine(dir, "tile.json"), $$"""{ "name": "{{name}}", "connector": "script", "settings": { "hooks": { "click": "x.ps1" } } }""");
        File.WriteAllText(Path.Combine(dir, "x.ps1"), "'{}'");
    }

    [Fact]
    public async Task Saving_a_tile_json_restarts_only_that_kinds_tiles()
    {
        using var tiles = new TempDir();
        WriteKind(tiles.Path, "ping", "Ping");
        WriteKind(tiles.Path, "uptime", "Uptime");
        using var store = new KindStore(tiles.Path, new CollectingLog());
        store.Load();
        var changed = new TaskCompletionSource<KindTable>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, table) => changed.TrySetResult(table);
        var writer = new FakeConfigWriter("""
            { "tiles": { "p1": { "kind": "ping" }, "p2": { "kind": "ping" }, "u": { "kind": "uptime" } },
              "displays": { "primary": { "slots": [ "p1", "p2", "u" ] } } }
            """);
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), FakeBar.Into(), writer: writer, kinds: () => store.Current);
        host.Start();
        var before = host.Runtimes;
        store.StartWatching();

        File.WriteAllText(Path.Combine(tiles.Path, "ping", "tile.json"),
            """{ "name": "Ping again", "connector": "script", "settings": { "hooks": { "click": "x.ps1" } } }""");
        var table = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.ConfigChanged(); // what the app does on Changed (on the UI thread)

        Assert.Equal("Ping again", table.Kinds["ping"].Name);
        Assert.NotSame(before["p1"], host.Runtimes["p1"]);
        Assert.NotSame(before["p2"], host.Runtimes["p2"]);
        Assert.Same(before["u"], host.Runtimes["u"]);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task A_script_change_is_a_change_but_a_data_file_is_not()
    {
        using var tiles = new TempDir();
        WriteKind(tiles.Path, "ping", "Ping");
        using var store = new KindStore(tiles.Path, new CollectingLog());
        store.Load();
        var changes = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changes);
        store.StartWatching();

        File.WriteAllText(Path.Combine(tiles.Path, "ping", "cache.txt"), "written by the script");
        await Task.Delay(1000);
        Assert.Equal(0, Volatile.Read(ref changes));

        File.WriteAllText(Path.Combine(tiles.Path, "ping", "x.ps1"), "'{ }' # edited");
        await Eventually.Holds(() => Volatile.Read(ref changes) >= 1);
    }

    [Fact]
    public async Task A_new_kind_folder_appears_in_the_table()
    {
        using var tiles = new TempDir();
        using var store = new KindStore(tiles.Path, new CollectingLog());
        store.Load();
        store.StartWatching();

        WriteKind(tiles.Path, "meeting", "Meeting");

        await Eventually.Holds(() => store.Current.Find("meeting")?.Name == "Meeting");
    }

    [Fact]
    public void Rescan_without_changes_raises_nothing()
    {
        using var tiles = new TempDir();
        WriteKind(tiles.Path, "ping", "Ping");
        using var store = new KindStore(tiles.Path, new CollectingLog());
        store.Load();
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.Rescan();

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Rescan_raises_Changed_when_a_kind_turns_broken_without_its_files_changing()
    {
        using var tiles = new TempDir();
        WriteKind(tiles.Path, "ping", "Ping");
        using var store = new KindStore(tiles.Path, new CollectingLog());
        store.Load();
        var changes = 0;
        store.Changed += (_, _) => changes++;

        using (new FileStream(Path.Combine(tiles.Path, "ping", "tile.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            store.Rescan(); // unreadable for the moment: stamp unchanged, kind broken
        Assert.StartsWith("Could not read", store.Current.Kinds["ping"].Error);
        Assert.Equal(1, changes);

        store.Rescan(); // readable again: recovers
        Assert.Null(store.Current.Kinds["ping"].Error);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Start_watching_creates_a_missing_tiles_folder()
    {
        using var data = new TempDir();
        var tiles = Path.Combine(data.Path, "tiles");
        using var store = new KindStore(tiles, new CollectingLog());
        store.Load();

        store.StartWatching();

        Assert.True(Directory.Exists(tiles));
    }
}
