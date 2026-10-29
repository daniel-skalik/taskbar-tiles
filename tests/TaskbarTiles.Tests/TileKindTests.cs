using System.Text.Json;
using TaskbarTiles.Host.Plugins;

namespace TaskbarTiles.Tests;

public class TileKindTests
{
    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Instance_keys_replace_the_kinds_keys_one_by_one()
    {
        var kind = TestKinds.Script("ping", """{ "host": "1.1.1.1", "count": 4, "hooks": { "interval": { "every": "15s", "run": "ping.ps1" } } }""");

        var settings = kind.SettingsFor(Json("""{ "host": "192.168.1.1", "label": "home" }"""));

        Assert.Equal("""{"host":"192.168.1.1","count":4,"hooks":{"interval":{"every":"15s","run":"ping.ps1"}},"label":"home"}""",
            settings.GetRawText());
    }

    [Fact]
    public void The_merge_is_shallow()
    {
        var kind = TestKinds.Script("x", """{ "opts": { "a": 1, "b": 2 } }""");

        Assert.Equal("""{"opts":{"a":5}}""", kind.SettingsFor(Json("""{ "opts": { "a": 5 } }""")).GetRawText());
    }

    [Fact]
    public void Tile_json_width_is_optional()
    {
        Assert.Null(TileJson.Parse("""{ "name": "A", "connector": "script" }""", "t.json").Width);
        Assert.Equal(268, TileJson.Parse("""{ "name": "A", "connector": "script", "width": 268 }""", "t.json").Width);
    }

    [Fact]
    public void Tile_json_compact_defaults_to_true()
    {
        Assert.True(TileJson.Parse("""{ "name": "A", "connector": "script" }""", "t.json").Compact);
        Assert.False(TileJson.Parse("""{ "name": "A", "connector": "script", "compact": false }""", "t.json").Compact);
    }

    [Fact]
    public void Tile_json_compact_that_is_not_a_boolean_breaks_the_kind() =>
        Assert.Equal("t.json: \"compact\" must be true or false.",
            TileJson.Parse("""{ "name": "A", "connector": "script", "compact": "no" }""", "t.json").Error);

    [Theory]
    [InlineData("\"wide\"")]
    [InlineData("10")]
    [InlineData("1000")]
    public void Tile_json_width_outside_the_range_breaks_the_kind(string width) =>
        Assert.Equal("t.json: \"width\" must be a number from 48 to 640.",
            TileJson.Parse($$"""{ "name": "A", "connector": "script", "width": {{width}} }""", "t.json").Error);

    [Fact]
    public void No_instance_settings_means_the_kinds_settings() =>
        Assert.Equal("""{"host":"1.1.1.1"}""", TestKinds.Script("ping", """{ "host": "1.1.1.1" }""").SettingsFor(Json("{}")).GetRawText());
}
