using TaskbarTiles.Contracts;
using TaskbarTiles.Host.UI;

namespace TaskbarTiles.Tests;

public class TileTextTests
{
    [Theory]
    [InlineData("uptime", "U")]
    [InlineData("  #1 build", "1")]
    [InlineData("élan", "É")]
    [InlineData("…", "•")]
    [InlineData("", "•")]
    [InlineData(null, "•")]
    public void Monogram_is_the_first_letter_or_digit(string? title, string expected) =>
        Assert.Equal(expected, TileText.Monogram(title));

    [Fact]
    public void Compact_tooltip_joins_title_subtitle_and_tooltip()
    {
        var state = new TileState { Title = "12:30", Subtitle = "up 2h", Tooltip = "Booted 08:00" };

        Assert.Equal($"12:30{Environment.NewLine}up 2h{Environment.NewLine}Booted 08:00", TileText.CompactTooltip(state));
    }

    [Fact]
    public void Compact_tooltip_skips_empty_and_repeated_lines()
    {
        var state = new TileState { Title = "Error", Subtitle = "exit 3", Tooltip = "exit 3" };

        Assert.Equal($"Error{Environment.NewLine}exit 3", TileText.CompactTooltip(state));
        Assert.Null(TileText.CompactTooltip(new TileState { Title = " " }));
    }

    [Fact]
    public void A_tooltip_repeating_the_title_and_subtitle_on_its_own_lines_adds_nothing()
    {
        var state = new TileState { Title = "No more events", Subtitle = "Tomorrow 09:00", Tooltip = "No more events\r\nTomorrow 09:00\nCalendar 2 failed" };
        Assert.Equal($"No more events{Environment.NewLine}Tomorrow 09:00{Environment.NewLine}Calendar 2 failed", TileText.CompactTooltip(state));
    }
}
