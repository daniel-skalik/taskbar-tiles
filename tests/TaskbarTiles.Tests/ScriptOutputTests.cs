using TaskbarTiles.Contracts;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class ScriptOutputTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void Empty_output_is_neither_a_state_nor_an_error(string? text)
    {
        var r = ScriptOutput.ParseTile(text);

        Assert.Null(r.Value);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Every_tile_field_is_mapped()
    {
        var png = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var r = ScriptOutput.ParseTile($$"""
            {
              "title": "CI", "subtitle": "main: passing", "tooltip": "Last run 2 min ago",
              "icon": { "glyph": "check", "color": "ok" },
              "image": "{{png}}",
              "buttons": [ { "id": "rerun", "glyph": "refresh", "tooltip": "Run again", "enabled": false } ],
              "progress": 0.25,
              "sparkline": [1, 2.5, 3],
              "urgent": true
            }
            """);

        Assert.Null(r.Error);
        Assert.Empty(r.Warnings);
        var s = r.Value!;
        Assert.Equal("CI", s.Title);
        Assert.Equal("main: passing", s.Subtitle);
        Assert.Equal("Last run 2 min ago", s.Tooltip);
        Assert.Equal(new TileIcon("check", TileColor.Ok), s.Icon);
        Assert.Equal(new byte[] { 1, 2, 3 }, s.Image);
        Assert.Equal(new TileButton("rerun", "refresh", "Run again", Enabled: false), Assert.Single(s.Buttons));
        Assert.Equal(0.25, s.Progress);
        Assert.Equal(new[] { 1, 2.5, 3 }, s.Sparkline);
        Assert.True(s.Urgent);
    }

    [Fact]
    public void Icon_as_a_string_is_a_neutral_glyph() =>
        Assert.Equal(new TileIcon("clock"), ScriptOutput.ParseTile("""{ "icon": "clock" }""").Value!.Icon);

    [Theory]
    [InlineData("OK", TileColor.Ok)]
    [InlineData("warn", TileColor.Warn)]
    [InlineData("Bad", TileColor.Bad)]
    [InlineData("accent", TileColor.Accent)]
    [InlineData("neutral", TileColor.Neutral)]
    public void Color_names_are_case_insensitive(string name, TileColor expected) =>
        Assert.Equal(expected, ScriptOutput.ParseTile($$"""{ "icon": { "glyph": "info", "color": "{{name}}" } }""").Value!.Icon!.Color);

    [Theory]
    [InlineData("purple")]
    [InlineData("2")]
    public void Unknown_color_falls_back_to_neutral_with_a_warning(string name)
    {
        var r = ScriptOutput.ParseTile($$"""{ "icon": { "glyph": "info", "color": "{{name}}" } }""");

        Assert.Equal(TileColor.Neutral, r.Value!.Icon!.Color);
        Assert.Contains(r.Warnings, w => w.Contains("unknown color"));
    }

    [Fact]
    public void Unknown_fields_are_ignored_with_a_warning()
    {
        var r = ScriptOutput.ParseTile("""{ "tittle": "x", "title": "y" }""");

        Assert.Equal("y", r.Value!.Title);
        Assert.Contains(r.Warnings, w => w.Contains("\"tittle\""));
    }

    [Fact]
    public void Wrong_types_are_dropped_with_warnings()
    {
        var r = ScriptOutput.ParseTile("""{ "title": [], "progress": "half", "urgent": "yes", "sparkline": [1, "a", 2], "icon": 7, "buttons": {} }""");

        var s = r.Value!;
        Assert.Null(s.Title);
        Assert.Null(s.Progress);
        Assert.False(s.Urgent);
        Assert.Equal(new[] { 1.0, 2.0 }, s.Sparkline);
        Assert.Null(s.Icon);
        Assert.Empty(s.Buttons);
        Assert.Equal(6, r.Warnings.Count);
    }

    [Fact]
    public void Numbers_become_text()
    {
        var s = ScriptOutput.ParseTile("""{ "title": 42, "subtitle": 1.5 }""").Value!;

        Assert.Equal("42", s.Title);
        Assert.Equal("1.5", s.Subtitle);
    }

    [Fact]
    public void Buttons_need_an_id_and_get_defaults()
    {
        var r = ScriptOutput.ParseTile("""{ "buttons": [ { "glyph": "play" }, "x", { "id": "go" } ] }""");

        Assert.Equal(new TileButton("go", "info"), Assert.Single(r.Value!.Buttons));
        Assert.Equal(2, r.Warnings.Count);
    }

    [Fact]
    public void Image_accepts_base64_and_data_urls()
    {
        var b64 = Convert.ToBase64String(new byte[] { 9, 8, 7 });

        Assert.Equal(new byte[] { 9, 8, 7 }, ScriptOutput.ParseTile($$"""{ "image": "{{b64}}" }""").Value!.Image);
        Assert.Equal(new byte[] { 9, 8, 7 }, ScriptOutput.ParseTile($$"""{ "image": "data:image/png;base64,{{b64}}" }""").Value!.Image);
        var bad = ScriptOutput.ParseTile("""{ "image": "not base64!" }""");
        Assert.Null(bad.Value!.Image);
        Assert.Single(bad.Warnings);
    }

    [Theory]
    [InlineData("[1, 2]", "an array")]
    [InlineData("42", "a number")]
    [InlineData("\"text\"", "a string")]
    public void Non_object_output_is_an_error(string text, string kind)
    {
        var r = ScriptOutput.ParseTile(text);

        Assert.Null(r.Value);
        Assert.Equal($"Expected a JSON object but got {kind}.", r.Error);
    }

    [Fact]
    public void Invalid_json_is_an_error()
    {
        var r = ScriptOutput.ParseTile("{ \"title\": ");

        Assert.Null(r.Value);
        Assert.StartsWith("Invalid JSON:", r.Error);
    }

    [Fact]
    public void Byte_order_mark_is_ignored() =>
        Assert.Equal("x", ScriptOutput.ParseTile("﻿{ \"title\": \"x\" }").Value!.Title);

    [Fact]
    public void Lone_surrogate_escape_is_an_error_not_an_exception()
    {
        var r = ScriptOutput.ParseTile("{\"title\":\"\\ud800\"}");

        Assert.Null(r.Value);
        Assert.StartsWith("Invalid JSON:", r.Error);
    }

    [Fact]
    public void Raw_lone_surrogate_is_an_error_not_an_exception()
    {
        var r = ScriptOutput.ParseTile("{\"title\":\"\uD800\"}");

        Assert.Null(r.Value);
        Assert.StartsWith("Invalid JSON:", r.Error);
    }

    [Fact]
    public void Non_finite_progress_is_dropped_with_a_warning()
    {
        var r = ScriptOutput.ParseTile("""{ "progress": 1e999 }""");

        Assert.Null(r.Value!.Progress);
        Assert.Contains(r.Warnings, w => w.Contains("finite"));
    }

    [Fact]
    public void Non_finite_sparkline_points_are_removed_with_a_warning()
    {
        var r = ScriptOutput.ParseTile("""{ "sparkline": [1, 1e999, -1e999, 2] }""");

        Assert.Equal(new[] { 1.0, 2.0 }, r.Value!.Sparkline);
        Assert.Contains(r.Warnings, w => w.Contains("finite"));
    }

    [Fact]
    public void Flyout_maps_rows_and_actions()
    {
        var r = ScriptOutput.ParseFlyout("""
            {
              "title": "Builds", "subtitle": "last 3",
              "rows": [ "plain row", { "text": "#41", "value": "2m", "icon": { "glyph": "check", "color": "ok" } } ],
              "actions": [ { "id": "rerun", "label": "Run again", "primary": true }, { "id": "open" } ]
            }
            """);

        Assert.Empty(r.Warnings);
        var f = r.Value!;
        Assert.Equal("Builds", f.Title);
        Assert.Equal("last 3", f.Subtitle);
        Assert.Equal(new FlyoutRow("plain row"), f.Rows[0]);
        Assert.Equal(new FlyoutRow("#41", "2m", new TileIcon("check", TileColor.Ok)), f.Rows[1]);
        Assert.Equal(new FlyoutAction("rerun", "Run again", Primary: true), f.Actions[0]);
        Assert.Equal(new FlyoutAction("open", "open"), f.Actions[1]);
    }

    [Fact]
    public void Flyout_without_title_warns_and_uses_an_empty_title()
    {
        var r = ScriptOutput.ParseFlyout("""{ "rows": [ { "value": "x" } ], "actions": [ { "label": "no id" } ] }""");

        Assert.Equal("", r.Value!.Title);
        Assert.Empty(r.Value.Rows);
        Assert.Empty(r.Value.Actions);
        Assert.Equal(3, r.Warnings.Count);
    }
}
