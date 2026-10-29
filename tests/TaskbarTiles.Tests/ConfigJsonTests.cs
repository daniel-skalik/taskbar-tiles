using System.Text.Json;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class ConfigJsonTests
{
    static DisplayEntry LeftOnly(DisplayLayout layout) => new(layout, DisplayLayout.Empty);

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(1.5, "1.5s")]
    [InlineData(90, "90s")]
    [InlineData(120, "2m")]
    [InlineData(3600, "1h")]
    public void Durations_are_written_in_the_largest_whole_unit(double seconds, string expected)
    {
        var text = ConfigJson.FormatDuration(TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, text);
        Assert.True(TaskbarTiles.Contracts.Duration.TryParse(text, out var back));
        Assert.Equal(TimeSpan.FromSeconds(seconds), back);
    }

    [Fact]
    public void Slots_round_trip_through_the_parser()
    {
        var layout = new DisplayLayout([
            new TileSlot("a", false),
            new TileSlot("b", true),
            new StackSlot(["a", "b"], TimeSpan.FromSeconds(8)),
        ]);
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "kind": "x" }, "b": { "kind": "x" } } }""");

        ConfigJson.SetDisplay(root, "primary", new DisplayEntry(layout, layout));
        var parsed = ConfigParser.Parse(ConfigJson.Serialize(root));

        Assert.True(parsed.Success, parsed.Error?.ToString());
        Assert.Equal(new DisplayEntry(layout, layout), parsed.Config!.Displays["primary"]);
    }

    [Fact]
    public void Remove_unplaced_tiles_deletes_only_candidates_no_display_places()
    {
        var root = ConfigJson.ParseRoot("""
        { "tiles": { "a": { "kind": "x" }, "b": { "kind": "x" }, "c": { "kind": "x" }, "d": { "kind": "x" }, "spare": { "kind": "x" } },
          "displays": { "primary": { "slots": ["a"] }, "*": { "slots": [ { "stack": ["d"] } ] },
                        "\\\\?\\DISPLAY#X": { "slots": ["b"] } } }
        """);

        ConfigJson.RemoveUnplacedTiles(root, ["b", "c", "d"]);

        // b: placed only by a display that is not connected; d: only through "*"; both stay
        Assert.Equal(new[] { "a", "b", "d", "spare" }, root["tiles"]!.AsObject().Select(t => t.Key));
    }

    [Fact]
    public void Remove_unplaced_tiles_leaves_a_config_that_does_not_parse_alone()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "kind": "x" } }, "displays": { "primary": { "slots": ["ghost"] } } }""");

        ConfigJson.RemoveUnplacedTiles(root, ["a"]);

        Assert.NotNull(root["tiles"]!["a"]);
    }

    [Fact]
    public void Set_display_keeps_other_keys_and_their_order()
    {
        var root = ConfigJson.ParseRoot("""
        {
          // dropped on save
          "custom": 1,
          "tiles": { "a": { "kind": "x", "note": "kept" } },
          "displays": { "*": { "slots": [] }, "primary": { "slots": [], "extra": true }, },
        }
        """);

        ConfigJson.SetDisplay(root, "primary", LeftOnly(new DisplayLayout([new TileSlot("a", false)])));
        var text = ConfigJson.Serialize(root);

        Assert.DoesNotContain("dropped", text);
        Assert.Contains("\"note\": \"kept\"", text);
        Assert.Contains("\"extra\": true", text);
        Assert.True(text.IndexOf("\"custom\"", StringComparison.Ordinal) < text.IndexOf("\"tiles\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"*\"", StringComparison.Ordinal) < text.IndexOf("\"primary\"", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("a", doc.RootElement.GetProperty("displays").GetProperty("primary").GetProperty("left")[0].GetString());
    }

    [Fact]
    public void Set_display_creates_missing_sections_and_entries()
    {
        var root = ConfigJson.ParseRoot("{}");

        ConfigJson.SetDisplay(root, @"\\?\DISPLAY#DEL41A8#5&2b1c", DisplayEntry.Empty);

        using var doc = JsonDocument.Parse(ConfigJson.Serialize(root));
        Assert.Equal(0, doc.RootElement.GetProperty("displays").GetProperty(@"\\?\DISPLAY#DEL41A8#5&2b1c").GetProperty("left").GetArrayLength());
    }

    [Fact]
    public void Set_tile_writes_the_kind_and_settings_only_when_there_are_any()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "kind": "x" } } }""");
        var empty = JsonDocument.Parse("{}").RootElement.Clone();
        var host = JsonDocument.Parse("""{ "host": "192.168.1.1" }""").RootElement.Clone();

        ConfigJson.SetTile(root, "b", new TileConfig("ping", empty));
        ConfigJson.SetTile(root, "c", new TileConfig("ping", host));
        var parsed = ConfigParser.Parse(ConfigJson.Serialize(root));

        Assert.Equal("""{"kind":"ping"}""", root["tiles"]!["b"]!.ToJsonString());
        Assert.True(parsed.Success, parsed.Error?.ToString());
        Assert.Equal(new[] { "a", "b", "c" }, parsed.Config!.Tiles.Keys);
        Assert.Equal("192.168.1.1", parsed.Config.Tiles["c"].Settings.GetProperty("host").GetString());
    }

    [Fact]
    public void Serialize_indents_and_keeps_non_ascii_readable()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "café": { "kind": "x", "settings": {} } } }""");

        var text = ConfigJson.Serialize(root);

        Assert.Contains("café", text);
        Assert.Contains(Environment.NewLine + "  \"tiles\"", text);
        Assert.EndsWith(Environment.NewLine, text);
        Assert.Equal("x", ConfigParser.Parse(text).Config!.Tiles["café"].Kind);
    }

    [Fact]
    public void Parse_root_rejects_a_non_object() =>
        Assert.Throws<InvalidOperationException>(() => ConfigJson.ParseRoot("[1]"));

    [Fact]
    public void Set_display_writes_right_only_with_slots_and_renames_slots_to_left_in_place()
    {
        var root = ConfigJson.ParseRoot("""{ "displays": { "primary": { "before": 1, "slots": [], "note": "x" } } }""");
        var a = new DisplayLayout([new TileSlot("a", false)]);

        ConfigJson.SetDisplay(root, "primary", new DisplayEntry(DisplayLayout.Empty, a));
        var primary = root["displays"]!["primary"]!.AsObject();
        Assert.Equal(new[] { "before", "left", "note", "right" }, primary.Select(p => p.Key));
        Assert.Equal("a", primary["right"]![0]!.GetValue<string>());

        ConfigJson.SetDisplay(root, "primary", LeftOnly(a));
        Assert.Equal(new[] { "before", "left", "note" }, primary.Select(p => p.Key));
    }
}
