using System.IO;
using System.Text.Json;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class TileGalleryTests
{
    const string Config = """
    { "tiles": { "spotify": { "connector": "media" }, "ci": { "connector": "script", "settings": { "hooks": { "click": "x" } } }, "uptime": { "connector": "script", "settings": { "hooks": { "click": "x" } } } },
      "displays": { "primary": { "slots": [ { "stack": ["spotify"] } ] } } }
    """;

    static readonly ScriptTemplate Template = new("uptime", "Clock and uptime", "scripts/uptime.ps1", """{ "hooks": { "click": "scripts/uptime.ps1" } }""");

    static HostConfig Parse(string json) => ConfigParser.Parse(json).Config!;

    [Fact]
    public void Lists_unplaced_tiles_then_plugin_connectors_then_script_templates()
    {
        var cfg = Parse(Config);

        var items = TileGallery.Build(cfg, cfg.Displays["primary"], ["sample", "script", "media"], [Template]);

        Assert.Equal(new[] { "ci", "uptime", "media", "sample", "Clock and uptime" }, items.Select(i => i.Title));
        Assert.Null(items[0].NewTile);
        Assert.Equal("Your tile · script", items[0].Subtitle);
        Assert.Equal("media", items[2].NewTile!.Connector);
        Assert.Equal("{}", items[2].NewTile!.Settings.GetRawText());
        Assert.Equal("uptime", items[4].TileId);
        Assert.Equal("script", items[4].NewTile!.Connector);
        Assert.Equal("Script · scripts/uptime.ps1", items[4].Subtitle);
    }

    [Theory]
    [InlineData(new string[0], "ping")]
    [InlineData(new[] { "ping" }, "ping-2")]
    [InlineData(new[] { "ping", "ping-2", "ping-4" }, "ping-3")]
    public void Unique_id_appends_the_first_free_number(string[] taken, string expected) =>
        Assert.Equal(expected, TileGallery.UniqueId("ping", taken));

    [Fact]
    public void Sample_templates_point_at_shipped_scripts_and_have_valid_hooks()
    {
        var repo = RepoRoot();
        Assert.NotEmpty(ScriptTemplates.Samples);
        foreach (var t in ScriptTemplates.Samples)
        {
            Assert.True(File.Exists(Path.Combine(repo, t.Script)), $"{t.Script} is missing");
            using var doc = JsonDocument.Parse(t.SettingsJson);
            Assert.Empty(ScriptSettings.Parse(doc.RootElement).Errors);
            Assert.Contains(t.Script, t.SettingsJson);
        }
    }

    [Fact]
    public void Adding_a_new_tile_writes_its_definition_under_a_free_id_and_places_it()
    {
        var writer = new FakeConfigWriter(Config);
        var bars = new Dictionary<string, FakeBar>();
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => bars[id] = new FakeBar(id), writer: writer, templates: [Template]);
        host.Start();
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Clock and uptime");

        Assert.Null(host.AddFromGallery("MAIN", item));

        var cfg = writer.Current;
        Assert.Equal("script", cfg.Tiles["uptime-2"].Connector);
        Assert.Equal("scripts/uptime.ps1", cfg.Tiles["uptime-2"].Settings.GetProperty("hooks").GetProperty("click").GetString());
        Assert.Equal(new TileSlot("uptime-2", false), cfg.Displays["primary"].Slots[^1]);
        Assert.Equal(new[] { "spotify", "uptime-2" }, bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Adding_an_existing_tile_only_places_it()
    {
        var writer = new FakeConfigWriter(Config);
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), writer: writer, templates: []);
        host.Start();
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "ci");

        Assert.Null(host.AddFromGallery("MAIN", item));

        Assert.Equal(3, writer.Current.Tiles.Count);
        Assert.Equal(new TileSlot("ci", false), writer.Current.Displays["primary"].Slots[^1]);
        Assert.DoesNotContain(host.GalleryFor("MAIN"), i => i.Title == "ci");
    }

    [Fact]
    public void Adding_a_new_tile_to_a_stack_puts_it_into_that_slot()
    {
        var writer = new FakeConfigWriter(Config);
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), writer: writer, templates: [Template]);
        host.Start();
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Clock and uptime");

        Assert.Null(host.AddFromGallery("MAIN", item, stackWith: 0));

        Assert.Equal("script", writer.Current.Tiles["uptime-2"].Connector);
        var slot = Assert.IsType<StackSlot>(Assert.Single(writer.Current.Displays["primary"].Slots));
        Assert.Equal(new[] { "spotify", "uptime-2" }, slot.TileIds);
    }

    [Fact]
    public void Adding_an_existing_tile_to_a_plain_tile_makes_a_stack_in_its_place()
    {
        var writer = new FakeConfigWriter("""
            { "tiles": { "spotify": { "connector": "media" }, "ci": { "connector": "script", "settings": { "hooks": { "click": "x" } } }, "uptime": { "connector": "script", "settings": { "hooks": { "click": "x" } } } },
              "displays": { "primary": { "slots": [ { "tile": "spotify" }, { "tile": "uptime" } ] } } }
            """);
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => new FakeBar(id), writer: writer, templates: []);
        host.Start();
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "ci");

        Assert.Null(host.AddFromGallery("MAIN", item, stackWith: 0));

        var slots = writer.Current.Displays["primary"].Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(new[] { "spotify", "ci" }, Assert.IsType<StackSlot>(slots[0]).TileIds);
        Assert.Equal(new TileSlot("uptime", false), slots[1]);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TaskbarTiles.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("TaskbarTiles.sln not found above the test output folder.");
    }
}
