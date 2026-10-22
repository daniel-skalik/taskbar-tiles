using System.Windows;
using System.Windows.Media;
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

    [Fact]
    public void ApplyTo_dark_uses_constraint_colors()
    {
        var dict = new ResourceDictionary();
        Theme.ApplyTo(dict, light: false);
        Assert.Equal(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF), ((SolidColorBrush)dict["TileFill"]).Color);
        Assert.Equal(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF), ((SolidColorBrush)dict["TileOutline"]).Color);
    }
}
