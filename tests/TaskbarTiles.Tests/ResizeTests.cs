using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using TaskbarTiles.Host;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Layout;
using TaskbarTiles.Host.Plugins;
using TaskbarTiles.Host.Runtime;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class ResizeTests
{
    // --- tile.json and config.json ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "name": "A", "connector": "script" }""", true)]
    [InlineData("""{ "name": "A", "connector": "script", "resizable": false }""", false)]
    public void Tile_json_resizable_defaults_to_true(string json, bool expected) =>
        Assert.Equal(expected, TileJson.Parse(json, "t.json").Resizable);

    [Fact]
    public void Tile_json_resizable_that_is_not_a_boolean_breaks_the_kind() =>
        Assert.Equal("t.json: \"resizable\" must be true or false.",
            TileJson.Parse("""{ "name": "A", "connector": "script", "resizable": "yes" }""", "t.json").Error);

    [Fact]
    public void A_tile_may_have_its_own_width()
    {
        var cfg = ConfigParser.Parse("""{ "tiles": { "a": { "kind": "ping", "width": 180 }, "b": { "kind": "ping" } } }""").Config!;
        Assert.Equal(180, cfg.Tiles["a"].Width);
        Assert.Null(cfg.Tiles["b"].Width);
    }

    [Theory]
    [InlineData("20")]
    [InlineData("\"wide\"")]
    public void A_tile_width_outside_the_range_is_rejected(string width)
    {
        var result = ConfigParser.Parse($$"""{ "tiles": { "a": { "kind": "ping", "width": {{width}} } } }""");
        Assert.False(result.Success);
        Assert.Contains("Tile 'a': \"width\" must be a number from 48 to 640.", result.Error!.ToString());
    }

    [Fact]
    public void Setting_a_width_rounds_and_clamps_it_and_null_removes_it()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "kind": "ping", "settings": { "host": "x" } } } }""");

        ConfigJson.SetTileWidth(root, "a", 180.6);
        Assert.Equal("""{"kind":"ping","settings":{"host":"x"},"width":181}""", root["tiles"]!["a"]!.ToJsonString());
        ConfigJson.SetTileWidth(root, "a", 5000);
        Assert.Equal(640, root["tiles"]!["a"]!["width"]!.GetValue<int>());
        ConfigJson.SetTileWidth(root, "a", null);
        Assert.Equal("""{"kind":"ping","settings":{"host":"x"}}""", root["tiles"]!["a"]!.ToJsonString());
    }

    // --- host ------------------------------------------------------------------------------------------------------

    const string Config = """
        { "tiles": { "a": { "kind": "ping", "width": 210 }, "b": { "kind": "fixed", "width": 210 }, "c": { "kind": "ping" } },
          "displays": { "primary": { "slots": [ "a", "b", { "stack": ["a", "c"], "rotate": "5s" } ] } } }
        """;

    static readonly KindTable Kinds = TestKinds.Of(
        TestKinds.Script("ping", width: 132),
        TestKinds.Script("fixed", width: 150) with { Resizable = false });

    static (HostController Host, FakeConfigWriter Writer, Dictionary<string, FakeBar> Bars) Host(string json = Config)
    {
        var writer = new FakeConfigWriter(json);
        var bars = new Dictionary<string, FakeBar>();
        var host = new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), FakeBar.Into(bars), writer: writer, kinds: () => Kinds);
        host.Start();
        return (host, writer, bars);
    }

    [Fact]
    public void Slots_take_the_resized_width_where_the_kind_allows_it()
    {
        var (_, _, bars) = Host();
        var slots = bars["MAIN"].Slots;

        Assert.Equal((210.0, true), (slots[0].FullWidth, slots[0].Resizable));
        Assert.Equal((150.0, false), (slots[1].FullWidth, slots[1].Resizable)); // "resizable": false ignores the width
        Assert.Equal((210.0, true), (slots[2].FullWidth, slots[2].Resizable));  // a stack: its widest member
    }

    [Fact]
    public void A_stack_with_a_fixed_member_cannot_be_resized()
    {
        var (_, _, bars) = Host("""
            { "tiles": { "a": { "kind": "ping" }, "b": { "kind": "fixed" } },
              "displays": { "primary": { "slots": [ { "stack": ["a", "b"], "rotate": "5s" } ] } } }
            """);
        Assert.False(bars["MAIN"].Slots[0].Resizable);
    }

    [Fact]
    public void Resizing_saves_the_width_and_keeps_the_tile_running()
    {
        var (host, writer, bars) = Host();
        var before = bars["MAIN"].Slots[2].Members[1];

        Assert.Null(host.ResizeTiles(["a", "c"], 250));

        var tiles = JsonNode.Parse(writer.Json)!["tiles"]!;
        Assert.Equal(250, tiles["a"]!["width"]!.GetValue<int>());
        Assert.Equal(250, tiles["c"]!["width"]!.GetValue<int>());
        Assert.Equal(250, bars["MAIN"].Slots[2].FullWidth);
        Assert.Same(before, bars["MAIN"].Slots[2].Members[1]); // not restarted

        Assert.Null(host.ResizeTiles(["a", "c"], null));
        Assert.Null(JsonNode.Parse(writer.Json)!["tiles"]!["a"]!["width"]);
        Assert.Equal(132, bars["MAIN"].Slots[0].FullWidth);
    }

    // --- edit mode -------------------------------------------------------------------------------------------------

    static TileRuntime Runtime(string id) =>
        new(id, "fake", 132, () => new FakeConnector(), JsonDocument.Parse("{}").RootElement.Clone(), new ImmediateDispatcher(), new CollectingLog());

    static SlotView View(BarSlot slot) =>
        new(slot, () => false, new TileCommands(@"C:\nowhere\config.json", @"C:\nowhere\logs", _ => { }, new CollectingLog()), new CollectingLog());

    [Fact]
    public void The_grip_shows_in_edit_mode_for_resizable_slots_only() => Sta.Run(() =>
    {
        var resizable = View(new BarSlot(0, [Runtime("a")], false, null, 132, Resizable: true));
        var fixedSlot = View(new BarSlot(1, [Runtime("b")], false, null, 132, Resizable: false));

        Assert.Equal(Visibility.Collapsed, resizable.ResizeGrip.Visibility);
        resizable.Editing = fixedSlot.Editing = true;
        Assert.Equal(Visibility.Visible, resizable.ResizeGrip.Visibility);
        Assert.Equal(Visibility.Collapsed, fixedSlot.ResizeGrip.Visibility);
        resizable.Compact = true; // an icon has no width to drag
        Assert.Equal(Visibility.Collapsed, resizable.ResizeGrip.Visibility);
        resizable.Detach();
        fixedSlot.Detach();
    });

    [Fact]
    public void A_saved_width_arriving_resizes_the_view() => Sta.Run(() =>
    {
        var runtime = Runtime("a");
        var view = View(new BarSlot(0, [runtime], false, null, 132, Resizable: true));

        view.Retarget(new BarSlot(0, [runtime], false, null, 200, Resizable: true));

        Assert.Equal(200, view.FullWidth);
        Assert.Equal(200, view.Width);
        view.Detach();
    });

    [Fact]
    public async Task A_resized_tile_tells_its_connector_the_new_width()
    {
        var connector = new FakeConnector();
        var runtime = new TileRuntime("a", "fake", 132, () => connector, JsonDocument.Parse("{}").RootElement.Clone(), new ImmediateDispatcher(), new CollectingLog());
        await runtime.StartAsync();
        var resized = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.Context!.Resized += (_, _) => resized.TrySetResult(connector.Context.Width);

        Assert.Equal(132, connector.Context.Width);
        runtime.Resize(250);

        Assert.Equal(250, await resized.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await runtime.DisposeAsync();
    }

    [Fact]
    public void Resizing_a_kept_tile_in_config_resizes_its_runtime()
    {
        var (host, _, bars) = Host();
        var runtime = bars["MAIN"].Slots[0].Members[0];

        Assert.Null(host.ResizeTiles(["a"], 300));

        Assert.Equal(300, runtime.Width);
    }

    // --- min and max width -----------------------------------------------------------------------------------------

    [Fact]
    public void Tile_json_may_bound_the_width_a_tile_is_resized_to()
    {
        var json = TileJson.Parse("""{ "name": "A", "connector": "script", "minWidth": 120, "maxWidth": 300 }""", "t.json");
        Assert.Equal((120.0, 300.0), (json.WidthMin, json.WidthMax));
        var plain = TileJson.Parse("""{ "name": "A", "connector": "script" }""", "t.json");
        Assert.Equal((TileJson.MinWidth, TileJson.MaxWidth), (plain.WidthMin, plain.WidthMax));
    }

    [Theory]
    [InlineData("""{ "name": "A", "connector": "script", "minWidth": 300, "maxWidth": 120 }""", "t.json: \"minWidth\" must not be more than \"maxWidth\".")]
    [InlineData("""{ "name": "A", "connector": "script", "maxWidth": 1000 }""", "t.json: \"maxWidth\" must be a number from 48 to 640.")]
    [InlineData("""{ "name": "A", "connector": "script", "minWidth": "wide" }""", "t.json: \"minWidth\" must be a number from 48 to 640.")]
    public void A_bad_width_range_breaks_the_kind(string json, string error) =>
        Assert.Equal(error, TileJson.Parse(json, "t.json").Error);

    [Fact]
    public void A_saved_width_outside_the_kinds_range_is_kept_within_it()
    {
        var kinds = TestKinds.Of(
            TestKinds.Script("ping", width: 132) with { WidthMin = 100, WidthMax = 250 },
            TestKinds.Script("wide", width: 200) with { WidthMin = 150 });
        var writer = new FakeConfigWriter("""
            { "tiles": { "a": { "kind": "ping", "width": 600 }, "b": { "kind": "ping", "width": 60 }, "c": { "kind": "wide" } },
              "displays": { "primary": { "slots": [ "a", "b", { "stack": ["b", "c"], "rotate": "5s" } ] } } }
            """);
        var bars = new Dictionary<string, FakeBar>();
        new HostController(ConnectorCatalog.Empty, () => writer.Current,
            new FakeTaskbarSource { Current = [FakeTaskbarSource.Display("MAIN", true)] }, new ImmediateDispatcher(),
            _ => new CollectingLog(), FakeBar.Into(bars), writer: writer, kinds: () => kinds).Start();
        var slots = bars["MAIN"].Slots;

        Assert.Equal((250.0, 100.0, 250.0), (slots[0].FullWidth, slots[0].MinWidth, slots[0].MaxWidth));
        Assert.Equal(100, slots[1].FullWidth);
        Assert.Equal((150.0, 250.0), (slots[2].MinWidth, slots[2].MaxWidth)); // a stack: every member's range holds
    }
}
