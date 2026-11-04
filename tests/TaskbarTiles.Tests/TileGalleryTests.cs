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
            _ => new CollectingLog(), FakeBar.Into(bars), writer: writer, kinds: () => Kinds);
        host.Start();
        return host;
    }

    [Fact]
    public void Lists_unplaced_tiles_then_one_entry_per_kind_by_name()
    {
        var cfg = Parse(Config);

        var items = TileGallery.Build(cfg, cfg.Displays["primary"], Kinds);

        Assert.Equal(new[] { "Ci", "Uptime", "Clock and uptime", "Media", "Ping a host" }, items.Select(i => i.Title));
        Assert.Null(items[0].NewTile);
        Assert.Equal("Not shown · Ping a host", items[0].Subtitle);
        Assert.Equal(TileGallery.YourTiles, items[0].Group);
        Assert.Equal("Pings a host every 15 s", items[4].Subtitle);
        Assert.Equal("ping", items[4].TileId);
        Assert.Equal("ping", items[4].NewTile!.Kind);
        Assert.Equal("{}", items[4].NewTile!.Settings.GetRawText());
    }

    [Fact]
    public void Your_tiles_say_whether_another_display_shows_them()
    {
        var cfg = Parse("""
            { "tiles": { "a": { "kind": "uptime" }, "b": { "kind": "uptime" } },
              "displays": { "primary": { "left": [] }, "OTHER": { "right": ["a"] } } }
            """);

        var items = TileGallery.Build(cfg, cfg.Displays["primary"], Kinds);

        Assert.Equal("On another display · Clock and uptime", items.Single(i => i.TileId == "a" && i.NewTile is null).Subtitle);
        Assert.Equal("Not shown · Clock and uptime", items.Single(i => i.TileId == "b" && i.NewTile is null).Subtitle);
    }

    [Fact]
    public void New_tiles_sit_in_their_kinds_group_and_your_tiles_in_their_folder()
    {
        var kinds = TestKinds.Of(
            TestKinds.Script("ping", name: "Ping") with { Group = "Network" },
            TestKinds.Script("snake", name: "Snake") with { Group = "Fun/Games" });
        var cfg = Parse("""{ "tiles": { "mine": { "kind": "snake" } } }""");

        var items = TileGallery.Build(cfg, DisplayEntry.Empty, kinds);

        Assert.Equal(new[] { ("Mine", TileGallery.YourTiles), ("Ping", "Network"), ("Snake", "Fun/Games") }, items.Select(i => (i.Title, i.Group)));
    }

    [Fact]
    public void Opening_a_folder_lists_its_subfolders_then_its_tiles()
    {
        GalleryItem Item(string title, string group) => new(title, "", title, null, group);
        var items = new[]
        {
            Item("ci", ""), Item("Snake", "Fun/Games"), Item("Racers", "fun/games"), Item("Farm", "Fun/Scenes"),
            Item("Dice", "Fun"), Item("Deep", "Fun/Games/Retro"), Item("Tea", "Kitchen"), Item("Toast", "Kitchen"),
        };

        var (top, topItems) = TileGallery.Open(items, "");
        var (fun, funItems) = TileGallery.Open(items, "Fun");
        var (games, gameItems) = TileGallery.Open(items, "Fun/Games");

        Assert.Equal(new[] { new GalleryFolder("Fun", "Fun", 5), new GalleryFolder("Kitchen", "Kitchen", 2) }, top);
        Assert.Equal(new[] { "ci" }, topItems.Select(i => i.Title));
        Assert.Equal(new[] { new GalleryFolder("Games", "Fun/Games", 3) }, fun);
        Assert.Equal(new[] { "Farm", "Dice" }, funItems.Select(i => i.Title));
        Assert.Empty(games);
        Assert.Equal(new[] { "Snake", "Racers", "Deep" }, gameItems.Select(i => i.Title)); // folders match ignoring case
    }

    [Fact]
    public void A_group_with_one_tile_is_shown_flat()
    {
        GalleryItem Item(string title, string group) => new(title, "", title, null, group);
        var items = new[] { Item("Alpha", "Media"), Item("Ping", "Network"), Item("Zed", "") };

        var (folders, here) = TileGallery.Open(items, "");

        Assert.Empty(folders);
        Assert.Equal(new[] { "Alpha", "Ping", "Zed" }, here.Select(i => i.Title)); // in their own order
    }

    [Theory]
    [InlineData("Fun/Games", "Fun")]
    [InlineData("Fun", "")]
    [InlineData("", "")]
    public void Parent_is_the_folder_above(string path, string expected) => Assert.Equal(expected, TileGallery.Parent(path));

    [Theory]
    [InlineData("spotify", "Spotify")]
    [InlineData("claude-usage", "Claude usage")]
    [InlineData("grz_visits-2", "Grz visits 2")]
    [InlineData("--", "--")]
    public void Your_tiles_are_titled_from_their_id(string id, string expected) => Assert.Equal(expected, TileGallery.TitleFor(id));

    [Fact]
    public void An_instance_of_a_kind_that_is_gone_shows_the_kind_id()
    {
        var cfg = Parse("""{ "tiles": { "w": { "kind": "weather" } } }""");

        Assert.Equal("Not shown · weather", TileGallery.Build(cfg, DisplayEntry.Empty, Kinds)[0].Subtitle);
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
        Assert.Equal(new TileSlot("ping", false), writer.Current.Displays["primary"].Left.Slots[^1]);
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
        Assert.Equal(new TileSlot("uptime-2", false), writer.Current.Displays["primary"].Left.Slots[^1]);
        Assert.Equal(new[] { "spotify", "uptime-2" }, bars["MAIN"].Tiles.Select(t => t.TileId));
    }

    [Fact]
    public void Adding_an_existing_tile_only_places_it()
    {
        var writer = new FakeConfigWriter(Config);
        var host = Host(writer);
        var item = host.GalleryFor("MAIN").Single(i => i.TileId == "ci" && i.NewTile is null);

        Assert.Null(host.AddFromGallery("MAIN", item));

        Assert.Equal(3, writer.Current.Tiles.Count);
        Assert.Equal(new TileSlot("ci", false), writer.Current.Displays["primary"].Left.Slots[^1]);
        Assert.DoesNotContain(host.GalleryFor("MAIN"), i => i.TileId == "ci");
    }

    [Fact]
    public void Adding_a_new_tile_to_a_stack_puts_it_into_that_slot()
    {
        var writer = new FakeConfigWriter(Config);
        var host = Host(writer);
        var item = host.GalleryFor("MAIN").Single(i => i.Title == "Clock and uptime");

        Assert.Null(host.AddFromGallery("MAIN", item, stackWith: 0));

        Assert.Equal("uptime", writer.Current.Tiles["uptime-2"].Kind);
        var slot = Assert.IsType<StackSlot>(Assert.Single(writer.Current.Displays["primary"].Left.Slots));
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
        var item = host.GalleryFor("MAIN").Single(i => i.TileId == "ci" && i.NewTile is null);

        Assert.Null(host.AddFromGallery("MAIN", item, stackWith: 0));

        var slots = writer.Current.Displays["primary"].Left.Slots;
        Assert.Equal(2, slots.Count);
        Assert.Equal(new[] { "spotify", "ci" }, Assert.IsType<StackSlot>(slots[0]).TileIds);
        Assert.Equal(new TileSlot("uptime", false), slots[1]);
    }
}
