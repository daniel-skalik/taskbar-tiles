using TaskbarTiles.Host.Config;

namespace TaskbarTiles.Tests;

public class DurationTests
{
    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("30s", 30_000)]
    [InlineData("10m", 600_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData(" 1.5s ", 1_500)]
    public void Parses_supported_units(string text, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), Duration.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("5 days")]
    [InlineData(null)]
    [InlineData("99999999999h")]
    [InlineData("١٠s")]
    public void Rejects_invalid(string? text) => Assert.False(Duration.TryParse(text, out _));
}
