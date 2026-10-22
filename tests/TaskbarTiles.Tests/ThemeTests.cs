using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class ThemeTests
{
    [Theory]
    [InlineData("play", "")]
    [InlineData("pause", "")]
    [InlineData("previous", "")]
    [InlineData("next", "")]
    [InlineData("warning", "")]
    public void Known_glyphs_map_to_segoe_fluent_codepoints(string name, string expected) =>
        Assert.Equal(expected, Theme.Glyph(name));

    [Fact]
    public void Unknown_glyph_falls_back_to_info() => Assert.Equal(Theme.Glyph("info"), Theme.Glyph("no-such-glyph"));

    [Fact]
    public void Decode_returns_null_for_garbage() => Assert.Null(Theme.Decode([1, 2, 3]));
}
