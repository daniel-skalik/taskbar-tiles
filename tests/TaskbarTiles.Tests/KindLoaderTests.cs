using System.IO;
using System.Runtime.Loader;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class KindLoaderTests
{
    static readonly string SamplePluginDir = Path.Combine(AppContext.BaseDirectory, "plugins", "TaskbarTiles.Tests.SamplePlugin");

    const string PingJson = """
    {
      // comments are allowed
      "name": "Ping a host",
      "description": "Pings a host every 15 s",
      "connector": "script",
      "settings": { "host": "1.1.1.1", "hooks": { "click": "ping.ps1" } },
      "unknown": "ignored",
    }
    """;

    /// <summary>Creates tiles\&lt;id&gt; (with tile.json unless null) and returns its path.</summary>
    static string Kind(TempDir tiles, string id, string? tileJson)
    {
        var dir = Path.Combine(tiles.Path, id);
        Directory.CreateDirectory(dir);
        if (tileJson is not null) File.WriteAllText(Path.Combine(dir, "tile.json"), tileJson);
        return dir;
    }

    static void CopySamplePlugin(string kindDir)
    {
        foreach (var file in Directory.GetFiles(SamplePluginDir))
            File.Copy(file, Path.Combine(kindDir, Path.GetFileName(file)));
    }

    static string SampleKindJson(string connector) => $$"""{ "name": "Sample", "connector": "{{connector}}" }""";

    [Fact]
    public void Scans_a_script_kind()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "ping", PingJson);

        var table = new KindLoader(new CollectingLog()).Load(tiles.Path);

        var kind = table.Kinds["ping"];
        Assert.Equal("ping", kind.Id);
        Assert.Equal(dir, kind.Directory);
        Assert.Equal("Ping a host", kind.Name);
        Assert.Equal("Pings a host every 15 s", kind.Description);
        Assert.Equal(TileKind.ScriptConnectorId, kind.Connector);
        Assert.Equal("1.1.1.1", kind.Settings.GetProperty("host").GetString());
        Assert.Null(kind.Error);
        Assert.Null(kind.Plugin);
        Assert.Same(kind, table.Find("PING")); // folder names are case-insensitive
        Assert.Equal(tiles.Path, table.TilesDir);
    }

    [Fact]
    public void Description_and_settings_are_optional()
    {
        using var tiles = new TempDir();
        Kind(tiles, "uptime", """{ "name": "Uptime", "connector": "script" }""");

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["uptime"];

        Assert.Equal("", kind.Description);
        Assert.Equal("{}", kind.Settings.GetRawText());
        Assert.Null(kind.Error);
    }

    [Fact]
    public void A_folder_without_tile_json_is_not_a_kind()
    {
        using var tiles = new TempDir();
        Kind(tiles, "notes", null);

        var table = new KindLoader(new CollectingLog()).Load(tiles.Path);

        Assert.Empty(table.Kinds);
        Assert.Contains("notes", table.FoldersWithoutTileJson);
    }

    [Fact]
    public void A_missing_tiles_folder_gives_an_empty_table() =>
        Assert.Empty(new KindLoader(new CollectingLog()).Load(Path.Combine(Path.GetTempPath(), "tt-no-such-" + Guid.NewGuid().ToString("N"))).Kinds);

    [Fact]
    public void Invalid_json_keeps_the_kind_as_broken_naming_the_file_and_line_and_others_still_load()
    {
        using var tiles = new TempDir();
        Kind(tiles, "bad", "{\n  \"name\": \"Bad\",\n  \"connector\": \n}");
        Kind(tiles, "ping", PingJson);
        var log = new CollectingLog();

        var table = new KindLoader(log).Load(tiles.Path);

        var bad = table.Kinds["bad"];
        Assert.StartsWith(@"tiles\bad\tile.json is not valid JSON: ", bad.Error);
        Assert.EndsWith("(line 4)", bad.Error);
        Assert.Equal("bad", bad.Name);          // the picker shows the folder name
        Assert.Equal(bad.Error, bad.Description); // and the error as subtitle
        Assert.Null(table.Kinds["ping"].Error);
        Assert.Contains(log.Snapshot(), m => m.StartsWith("ERROR") && m.Contains("bad"));
    }

    [Fact]
    public void The_line_suffix_is_omitted_when_the_parser_gives_no_line_number()
    {
        Assert.Equal(" (line 4)", TileJson.LineSuffix(3));
        Assert.Equal("", TileJson.LineSuffix(null));
    }

    [Theory]
    [InlineData("""{ "connector": "script" }""", "\"name\"")]
    [InlineData("""{ "name": "X" }""", "\"connector\"")]
    [InlineData("""{ "name": "  ", "connector": "script" }""", "\"name\"")]
    [InlineData("""{ "name": "X", "connector": "script", "settings": 5 }""", "\"settings\" must be an object")]
    [InlineData("""[1]""", "must be a JSON object")]
    public void Tile_json_without_name_or_connector_is_broken(string json, string expected)
    {
        using var tiles = new TempDir();
        Kind(tiles, "x", json);

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["x"];

        Assert.Contains(@"tiles\x\tile.json", kind.Error);
        Assert.Contains(expected, kind.Error);
    }

    [Fact]
    public void A_dll_kind_resolves_its_connector_from_its_own_folder()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "sample", SampleKindJson("sample"));
        CopySamplePlugin(dir);

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["sample"];

        Assert.Null(kind.Error);
        Assert.Equal("sample", kind.Plugin!.Id);
        Assert.Equal(150, kind.Plugin.Width);
        Assert.StartsWith(dir, kind.Plugin.SourcePath);
        Assert.IsAssignableFrom<ITileConnector>(ConnectorCatalog.Instantiate(kind.Plugin));
        Assert.Contains("badwidth", kind.Dlls.Connectors.Keys); // every id the folder declares, private to it
        Assert.False(kind.NeedsRestart);
    }

    [Fact]
    public void Two_kinds_with_the_same_connector_id_do_not_clash()
    {
        using var tiles = new TempDir();
        CopySamplePlugin(Kind(tiles, "one", SampleKindJson("sample")));
        CopySamplePlugin(Kind(tiles, "two", SampleKindJson("sample")));

        var table = new KindLoader(new CollectingLog()).Load(tiles.Path);

        var one = table.Kinds["one"].Plugin!;
        var two = table.Kinds["two"].Plugin!;
        Assert.NotEqual(one.SourcePath, two.SourcePath);
        Assert.NotSame(one.Type, two.Type);
        Assert.NotSame(AssemblyLoadContext.GetLoadContext(one.Type.Assembly), AssemblyLoadContext.GetLoadContext(two.Type.Assembly));
    }

    [Fact]
    public void A_connector_no_dll_in_the_folder_declares_breaks_the_kind()
    {
        using var tiles = new TempDir();
        CopySamplePlugin(Kind(tiles, "x", SampleKindJson("nope")));

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["x"];

        Assert.Equal(@"tiles\x\tile.json names connector 'nope', but no DLL in tiles\x declares it.", kind.Error);
        Assert.Null(kind.Plugin);
    }

    [Fact]
    public void A_dll_that_fails_to_load_breaks_only_its_kind()
    {
        using var tiles = new TempDir();
        File.WriteAllText(Path.Combine(Kind(tiles, "x", SampleKindJson("sample")), "broken.dll"), "not a dll");
        Kind(tiles, "ping", PingJson);

        var table = new KindLoader(new CollectingLog()).Load(tiles.Path);

        Assert.StartsWith(@"Could not load broken.dll from tiles\x: ", table.Kinds["x"].Error);
        Assert.Null(table.Kinds["ping"].Error);
    }

    [Fact]
    public void Two_dlls_in_one_folder_declaring_the_same_id_break_the_kind()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "x", SampleKindJson("sample"));
        CopySamplePlugin(dir);
        File.Copy(Path.Combine(dir, "TaskbarTiles.Tests.SamplePlugin.dll"), Path.Combine(dir, "Copy.dll"));

        var kind = new KindLoader(new CollectingLog()).Load(tiles.Path).Kinds["x"];

        Assert.Contains(@"in tiles\x both declare connector", kind.Error);
    }

    [Fact]
    public void Only_dlls_that_reference_the_contracts_are_connector_dlls()
    {
        Assert.True(KindLoader.ReferencesContracts(Path.Combine(SamplePluginDir, "TaskbarTiles.Tests.SamplePlugin.dll")));
        Assert.False(KindLoader.ReferencesContracts(typeof(ITileConnector).Assembly.Location)); // the contracts themselves
    }

    [Fact]
    public void Rescan_rereads_tile_json_and_scripts_but_not_data_files()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "ping", PingJson);
        File.WriteAllText(Path.Combine(dir, "ping.ps1"), "'{}'");
        var loader = new KindLoader(new CollectingLog());
        var first = loader.Load(tiles.Path);

        File.WriteAllText(Path.Combine(dir, "cache.txt"), "written by the script");
        var afterData = loader.Rescan(first);
        File.WriteAllText(Path.Combine(dir, "ping.ps1"), "'{ }' # edited");
        var afterScript = loader.Rescan(afterData);
        File.WriteAllText(Path.Combine(dir, "tile.json"), """{ "name": "Ping 2", "connector": "script" }""");
        var afterJson = loader.Rescan(afterScript);

        Assert.Equal(first.Kinds["ping"].Stamp, afterData.Kinds["ping"].Stamp);
        Assert.NotEqual(afterData.Kinds["ping"].Stamp, afterScript.Kinds["ping"].Stamp);
        Assert.Equal("Ping 2", afterJson.Kinds["ping"].Name);
        Assert.NotEqual(afterScript.Kinds["ping"].Stamp, afterJson.Kinds["ping"].Stamp);
    }

    [Fact]
    public void Rescan_keeps_the_loaded_dlls_and_flags_a_kind_whose_dlls_changed()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "sample", SampleKindJson("sample"));
        CopySamplePlugin(dir);
        var loader = new KindLoader(new CollectingLog());
        var first = loader.Load(tiles.Path);

        File.WriteAllText(Path.Combine(dir, "extra.dll"), "added while running");
        var next = loader.Rescan(first);

        var kind = next.Kinds["sample"];
        Assert.Same(first.Kinds["sample"].Dlls, kind.Dlls); // nothing loaded again, extra.dll not even read
        Assert.Null(kind.Error);
        Assert.True(kind.NeedsRestart);
        Assert.Equal(new[] { "sample" }, next.DllsChanged);
        Assert.Empty(first.DllsChanged);
    }

    [Fact]
    public void A_dll_kind_whose_folder_vanishes_for_one_rescan_keeps_its_loaded_dlls_when_it_returns()
    {
        using var tiles = new TempDir();
        var dir = Kind(tiles, "sample", SampleKindJson("sample"));
        CopySamplePlugin(dir);
        var loader = new KindLoader(new CollectingLog());
        var first = loader.Load(tiles.Path);
        var away = Path.Combine(tiles.Path, "..", "tt-away-" + Guid.NewGuid().ToString("N"));

        Directory.Move(dir, away);
        var gone = loader.Rescan(first);
        Directory.Move(away, dir);
        var back = loader.Rescan(gone);

        Assert.Null(gone.Find("sample"));
        Assert.Null(back.Kinds["sample"].Error);
        Assert.Same(first.Kinds["sample"].Dlls, back.Kinds["sample"].Dlls);
    }

    [Fact]
    public void A_kind_folder_that_appears_with_dlls_is_broken_until_restart()
    {
        using var tiles = new TempDir();
        var loader = new KindLoader(new CollectingLog());
        var first = loader.Load(tiles.Path);
        CopySamplePlugin(Kind(tiles, "late", SampleKindJson("sample")));
        Kind(tiles, "script-only", """{ "name": "S", "connector": "script" }""");

        var next = loader.Rescan(first);

        Assert.Equal(@"The DLLs in tiles\late were added while Taskbar Tiles was running; restart it to load them.", next.Kinds["late"].Error);
        Assert.True(next.Kinds["late"].NeedsRestart);
        Assert.Null(next.Kinds["script-only"].Error); // no DLLs: usable at once
        Assert.False(next.Kinds["script-only"].NeedsRestart);
    }
}
