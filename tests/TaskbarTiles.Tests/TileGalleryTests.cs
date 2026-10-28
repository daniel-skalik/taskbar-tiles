using System.Text.Json.Nodes;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class TileGalleryTests
{
    const string Config = """
    { "tiles": { "spotify": { "kind": "media" }, "ci": { "kind": "ping" }, "uptime": { "kind": "uptime" } },
      "displays": { "primary": { "slots": [ { "stack": ["spotify"] } ] } } }
    """;

    static readonly KindTable Kinds = TestKinds.Of(
        TestKinds.Script("ping", name: "Ping a host", description: "Pings a host every 15 s"),
        TestKinds.Script("uptime", name: "Clock and uptime", description: "The time, and how long Windows has been up"),
        TestKinds.Plugin("media", typeof(FakeConnector)) with { Name = "Media", Description = "Now playing" });

    static HostConfig Parse(string json) => ConfigParser.Parse(json).Config!;

    static HostController Host(FakeConfigWriter writer, Dictionary<string, FakeBar>? bars = null)
    {
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), id => bars is null ? new FakeBar(id) : (bars[id] = new FakeBar(id)), writer: writer, kinds: () => Kinds);
        host.Start();
        return host;
    }

    [Fact]
    public void Lists_unplaced_tiles_then_one_entry_per_kind_by_name()
    {
        var cfg = Parse(Config);

        var items = TileGallery.Build(cfg, cfg.Displays["primary"], Kinds);

        Assert.Equal(new[] { "ci", "uptime", "Clock and uptime", "Media", "Ping a host" }, items.Select(i => i.Title));
        Assert.Null(items[0].NewTile);
        Assert.Equal("Your tile · Ping a host", items[0].Subtitle);
        Assert.Equal("Pings a host every 15 s", items[4].Subtitle);
        Assert.Equal("ping", items[4].TileId);
        Assert.Equal("ping", items[4].NewTile!.Kind);
        Assert.Equal("{}", items[4].NewTile!.Settings.GetRawText());
    }

    [Fact]
    public void An_instance_of_a_kind_that_is_gone_shows_the_kind_id()
    {
        var cfg = Parse("""{ "tiles": { "w": { "kind": "weather" } } }""");

        Assert.Equal("Your tile · weather", TileGallery.Build(cfg, DisplayLayout.Empty, Kinds)[0].Subtitle);
    }

    [Theory]
    [InlineData(new string[0], "ping")]
    [InlineData(new[] { "ping" }, "ping-2")]
    [InlineData(new[] { "ping", "ping-2", "ping-4" }, "ping-3")]
    public void Unique_id_appends_the_first_free_number(string[] taken, string expected) =>
        Assert.Equal(expected, TileGallery.UniqueId("ping", taken));

    [Fact]
    public void Picking_a_kind_whose_id_is_free_writes_only_the_kind()
    {
        var writer = new FakeConfigWriter(Config);
        var host = Host(writer);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Ping a host");

        Assert.Null(host.AddFromGallery("MAIN", item));

        Assert.Equal("""{"kind":"ping"}""", JsonNode.Parse(writer.Json)!["tiles"]!["ping"]!.ToJsonString());
        Assert.Equal(new TileSlot("ping", false), writer.Current.Displays["primary"].Slots[^1]);
    }

    [Fact]
    public void Picking_a_kind_whose_id_is_taken_writes_it_under_a_free_id_and_places_it()
    {
        var writer = new FakeConfigWriter(Config);
        var bars = new Dictionary<string, FakeBar>();
        var host = Host(writer, bars);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Clock and uptime");

        Assert.Null(host.AddFromGallery("MAIN", item));

        Assert.Equal("""{"kind":"uptime"}""", JsonNode.Parse(writer.Json)!["tiles"]!["uptime-2"]!.ToJsonString());
        Assert.Equal(new TileSlot("uptime-2", false), writer.Current.Displays["primary"].Slots[^1]);
        Assert.Equal(new[] { "spotify", "uptime-2" }, bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Adding_an_existing_tile_only_places_it()
    {
        var writer = new FakeConfigWriter(Config);
        var host = Host(writer);
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
        var host = Host(writer);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Clock and uptime");

        Assert.Null(host.AddFromGallery("MAIN", item, stackWith: 0));

        Assert.Equal("uptime", writer.Current.Tiles["uptime-2"].Kind);
        var slot = Assert.IsType<StackSlot>(Assert.Single(writer.Current.Displays["primary"].Slots));
        Assert.Equal(new[] { "spotify", "uptime-2" }, slot.TileIds);
    }

    [Fact]
    public void Adding_an_existing_tile_to_a_plain_tile_makes_a_stack_in_its_place()
    {
        var writer = new FakeConfigWriter("""
            { "tiles": { "spotify": { "kind": "media" }, "ci": { "kind": "ping" }, "uptime": { "kind": "uptime" } },
              "displays": { "primary": { "slots": [ { "tile": "spotify" }, { "tile": "uptime" } ] } } }
            """);
        var host = Host(writer);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "ci");

        Assert.Null(host.AddFromGallery("MAIN", item, stackWith: 0));

        var slots = writer.Current.Displays["primary"].Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(new[] { "spotify", "ci" }, Assert.IsType<StackSlot>(slots[0]).TileIds);
        Assert.Equal(new TileSlot("uptime", false), slots[1]);
    }
}
