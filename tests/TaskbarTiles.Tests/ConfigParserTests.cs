using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class ConfigParserTests
{
    const string Sample = """
    {
      // comments are allowed
      "tiles": {
        "spotify": { "kind": "media", "settings": { "app": "Spotify" } },
        "ci":      { "kind": "script" },
        "weather": { "kind": "script" },
      },
      "displays": {
        "primary": { "slots": [ { "tile": "spotify", "pin": true }, "ci", { "stack": ["weather", "ci"], "rotate": "8s" } ] },
        "*":       { "slots": [] }
      }
    }
    """;

    [Fact]
    public void Parses_tiles_and_slots()
    {
        var result = ConfigParser.Parse(Sample);

        Assert.True(result.Success, result.Error?.ToString());
        var cfg = result.Config!;
        Assert.Equal("media", cfg.Tiles["spotify"].Kind);
        Assert.Equal("Spotify", cfg.Tiles["spotify"].Settings.GetProperty("app").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, cfg.Tiles["ci"].Settings.ValueKind);

        var slots = cfg.Displays["primary"].Slots;
        Assert.Equal(new TileSlot("spotify", true), slots[0]);
        Assert.Equal(new TileSlot("ci", false), slots[1]);
        var stack = Assert.IsType<StackSlot>(slots[2]);
        Assert.Equal(new[] { "weather", "ci" }, stack.TileIds);
        Assert.Equal(TimeSpan.FromSeconds(8), stack.Rotate);
        Assert.Empty(cfg.Displays["*"].Slots);
    }

    [Fact]
    public void Stack_rotate_defaults_to_5_seconds()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "a": { "kind": "x" } }, "displays": { "primary": { "slots": [ { "stack": ["a"] } ] } } }""");
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.IsType<StackSlot>(result.Config!.Displays["primary"].Slots[0]).Rotate);
    }

    [Fact]
    public void Invalid_json_reports_one_based_line_and_column()
    {
        var result = ConfigParser.Parse("{\n  \"tiles\": {\n    \"a\": { \"connector\": }\n  }\n}");

        Assert.False(result.Success);
        Assert.Equal(3, result.Error!.Line);
        Assert.NotNull(result.Error.Column);
    }

    [Fact]
    public void Slot_with_unknown_tile_is_an_error()
    {
        var result = ConfigParser.Parse("""{ "tiles": {}, "displays": { "primary": { "slots": ["ghost"] } } }""");
        Assert.False(result.Success);
        Assert.Contains("ghost", result.Error!.Message);
    }

    [Fact]
    public void Tile_without_kind_is_an_error()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "a": { } } }""");
        Assert.False(result.Success);
        Assert.Equal("Tile 'a' needs a \"kind\": the name of its folder in tiles\\.", result.Error!.Message);
    }

    [Fact]
    public void The_old_connector_form_is_an_error_that_shows_the_new_form()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "x": { "connector": "script", "settings": { "hooks": {} } } } }""");

        Assert.False(result.Success);
        Assert.Equal("Tile 'x' uses \"connector\"; tiles now name a kind: \"kind\": \"<folder>\"", result.Error!.Message);
    }

    [Fact]
    public void Hooks_in_instance_settings_are_an_error()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "x": { "kind": "ping", "settings": { "host": "a", "hooks": {} } } } }""");

        Assert.False(result.Success);
        Assert.Equal(@"hooks belong in tiles\ping\tile.json", result.Error!.Message);
    }

    [Fact]
    public void Instance_settings_are_optional()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "ping-dns": { "kind": "ping" } } }""");

        Assert.True(result.Success, result.Error?.ToString());
        Assert.Equal("ping", result.Config!.Tiles["ping-dns"].Kind);
        Assert.Equal("{}", result.Config.Tiles["ping-dns"].Settings.GetRawText());
    }

    [Theory]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("\"99999999999h\"")]
    [InlineData("\"0s\"")]
    [InlineData("\"500ms\"")]
    [InlineData("\"61m\"")]
    public void Non_string_invalid_or_out_of_range_rotate_is_a_config_error(string rotateJson)
    {
        var json = "{ \"tiles\": { \"a\": { \"kind\": \"x\" } }, \"displays\": { \"primary\": { \"slots\": [ { \"stack\": [\"a\"], \"rotate\": " + rotateJson + " } ] } } }";

        var result = ConfigParser.Parse(json);

        Assert.False(result.Success);
        Assert.Contains("rotate", result.Error!.Message);
    }

    [Theory]
    [InlineData("1s", 1)]
    [InlineData("1h", 3600)]
    public void Rotate_accepts_1s_to_1h(string rotate, int seconds)
    {
        var json = "{ \"tiles\": { \"a\": { \"kind\": \"x\" } }, \"displays\": { \"primary\": { \"slots\": [ { \"stack\": [\"a\"], \"rotate\": \"" + rotate + "\" } ] } } }";

        var result = ConfigParser.Parse(json);

        Assert.True(result.Success, result.Error?.ToString());
        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.IsType<StackSlot>(result.Config!.Displays["primary"].Slots[0]).Rotate);
    }

    [Fact]
    public void Missing_displays_section_means_no_layouts()
    {
        var result = ConfigParser.Parse("""{ "tiles": { "a": { "kind": "x" } } }""");
        Assert.True(result.Success);
        Assert.Empty(result.Config!.Displays);
    }
}
