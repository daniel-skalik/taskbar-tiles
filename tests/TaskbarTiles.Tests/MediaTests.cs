using TaskbarTiles.Plugins.Media;

namespace TaskbarTiles.Tests;

public class MediaTests
{
    static readonly DateTimeOffset Updated = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Song = TimeSpan.FromMinutes(4);

    static double? Progress(double positionSeconds, bool playing, double secondsLater) =>
        MediaConnector.Progress(TimeSpan.Zero, Song, TimeSpan.FromSeconds(positionSeconds), Updated, playing, Updated.AddSeconds(secondsLater));

    [Fact]
    public void Playing_moves_on_from_the_last_reported_position() => Assert.Equal(0.5, Progress(60, playing: true, secondsLater: 60));

    [Fact]
    public void Paused_stays_at_the_reported_position() => Assert.Equal(0.25, Progress(60, playing: false, secondsLater: 60));

    [Fact]
    public void Progress_stays_between_zero_and_one()
    {
        Assert.Equal(1, Progress(200, playing: true, secondsLater: 600));
        Assert.Equal(0, Progress(-5, playing: false, secondsLater: 0));
    }

    [Fact]
    public void No_length_means_no_bar() =>
        Assert.Null(MediaConnector.Progress(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, Updated, true, Updated));
}
