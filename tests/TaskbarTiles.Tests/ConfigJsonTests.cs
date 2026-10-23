using System.Text.Json;
using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class ConfigJsonTests
{
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
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "connector": "x" }, "b": { "connector": "x" } } }""");

        ConfigJson.SetDisplay(root, "primary", layout);
        var parsed = ConfigParser.Parse(ConfigJson.Serialize(root));

        Assert.True(parsed.Success, parsed.Error?.ToString());
        Assert.True(LayoutEdits.SameSlots(layout, parsed.Config!.Displays["primary"]));
    }

    [Fact]
    public void Set_display_keeps_other_keys_and_their_order()
    {
        var root = ConfigJson.ParseRoot("""
        {
          // dropped on save
          "custom": 1,
          "tiles": { "a": { "connector": "x", "note": "kept" } },
          "displays": { "*": { "slots": [] }, "primary": { "slots": [], "extra": true }, },
        }
        """);

        ConfigJson.SetDisplay(root, "primary", new DisplayLayout([new TileSlot("a", false)]));
        var text = ConfigJson.Serialize(root);

        Assert.DoesNotContain("dropped", text);
        Assert.Contains("\"note\": \"kept\"", text);
        Assert.Contains("\"extra\": true", text);
        Assert.True(text.IndexOf("\"custom\"", StringComparison.Ordinal) < text.IndexOf("\"tiles\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"*\"", StringComparison.Ordinal) < text.IndexOf("\"primary\"", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(text);
        Assert.Equal("a", doc.RootElement.GetProperty("displays").GetProperty("primary").GetProperty("slots")[0].GetString());
    }

    [Fact]
    public void Set_display_creates_missing_sections_and_entries()
    {
        var root = ConfigJson.ParseRoot("{}");

        ConfigJson.SetDisplay(root, @"\\?\DISPLAY#DEL41A8#5&2b1c", DisplayLayout.Empty);

        using var doc = JsonDocument.Parse(ConfigJson.Serialize(root));
        Assert.Equal(0, doc.RootElement.GetProperty("displays").GetProperty(@"\\?\DISPLAY#DEL41A8#5&2b1c").GetProperty("slots").GetArrayLength());
    }

    [Fact]
    public void Set_tile_writes_connector_and_settings()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "a": { "connector": "x" } } }""");
        var settings = JsonDocument.Parse("""{ "hooks": { "click": "scripts/a.ps1" } }""").RootElement.Clone();

        ConfigJson.SetTile(root, "b", new TileConfig("script", settings));
        var parsed = ConfigParser.Parse(ConfigJson.Serialize(root));

        Assert.True(parsed.Success, parsed.Error?.ToString());
        Assert.Equal(new[] { "a", "b" }, parsed.Config!.Tiles.Keys);
        Assert.Equal("scripts/a.ps1", parsed.Config.Tiles["b"].Settings.GetProperty("hooks").GetProperty("click").GetString());
    }

    [Fact]
    public void Serialize_indents_and_keeps_non_ascii_readable()
    {
        var root = ConfigJson.ParseRoot("""{ "tiles": { "café": { "connector": "x", "settings": {} } } }""");

        var text = ConfigJson.Serialize(root);

        Assert.Contains("café", text);
        Assert.Contains(Environment.NewLine + "  \"tiles\"", text);
        Assert.EndsWith(Environment.NewLine, text);
        Assert.Equal("x", ConfigParser.Parse(text).Config!.Tiles["café"].Connector);
    }

    [Fact]
    public void Parse_root_rejects_a_non_object() =>
        Assert.Throws<InvalidOperationException>(() => ConfigJson.ParseRoot("[1]"));
}
